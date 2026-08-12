namespace JobTrack.AdminCli;

using System.Text.Json;
using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using NodaTime;

/// <summary>
///     The <c>import-tree</c> command: reads a flat, file-local-id-keyed JSON array of nodes and
///     atomically creates them as a job-node subtree, all owned by one existing employee, via
///     <see cref="IJobCommands.ImportSubtreeAsync" /> — either every node and edge is created, or none
///     is. Every created node's <see cref="ImportSubtreeNodeSpec.OwnerUserId" /> and
///     <see cref="CommandContext.Actor" /> are that same employee — this is a bulk-authoring tool for
///     small trees, not a multi-owner import, so there is deliberately no separate actor/owner
///     distinction.
///     <para>
///         A file may flag one row (<c>"home": true</c>) as the home node the import establishes: that
///         node becomes the post-login landing node of the importing employee and of every account
///         named in <c>--home-node-for</c>, whose real <c>job_node</c> id no caller can know in
///         advance. Those assignments are carried <em>into</em> the import request
///         (<see cref="ImportSubtreeRequest.HomeNodeLocalId" />/
///         <see cref="ImportSubtreeRequest.HomeNodeUserIds" />) and written inside its transaction, so
///         this command performs exactly one <see cref="IJobTrackClient" /> mutation and partial
///         success is not a possible outcome. The importing employee stays the single actor throughout;
///         the named accounts are affected entities, not actors of their own.
///     </para>
/// </summary>
public static class JobTreeImportCommand
{
	private static readonly JsonSerializerOptions SerializerOptions = new() {
		PropertyNameCaseInsensitive = true,
	};

	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		string username,
		JobNodeId importRootId,
		string jsonContent,
		IClock clock,
		IReadOnlyList<string> homeNodeUsernames,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentNullException.ThrowIfNull(clock);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentNullException.ThrowIfNull(jsonContent);
		ArgumentNullException.ThrowIfNull(homeNodeUsernames);

		var user = await userManager.FindByNameAsync(username);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		// One captured clock value for the whole import (plan §2): every relative "open"/"closed"
		// duration in the file counts back from this same instant, so a large file cannot drift.
		var importedAt = clock.GetCurrentInstant();

		EquatableArray<ImportSubtreeNodeSpec> nodes;
		long? homeLocalId;
		try {
			var rawNodes = JsonSerializer.Deserialize<List<JobTreeImportNodeJson>>(jsonContent, SerializerOptions)
						   ?? throw new AdminCliUsageException("The import file's top level must be a JSON array of nodes.");
			homeLocalId = ResolveHomeLocalId(rawNodes);
			nodes = [
				.. rawNodes.Select(raw => new ImportSubtreeNodeSpec {
					LocalId = raw.Id,
					ParentLocalId = raw.ParentId,
					Description = raw.Title,
					OwnerUserId = user.AppUserId,
					Priority = Priority.Medium,
					PrerequisiteLocalIds = EquatableArray.CopyOf(raw.PrerequisiteIds ?? []),
					LeafWork = JobTreeImportWork.Resolve(raw, importedAt, user.AppUserId),
				}),
			];
		}
		catch (JsonException ex) {
			io.WriteError($"Failed to parse the import file: {ex.Message}");
			return 1;
		}
		catch (AdminCliUsageException ex) {
			// A malformed row is a problem with the file's contents, not with the command line, so
			// report it directly rather than letting Main answer with the whole usage banner.
			io.WriteError($"Failed to parse the import file: {ex.Message}");
			return 1;
		}

		// Resolved before the import runs so an unknown account is reported as the command-line mistake
		// it is, rather than as a domain rejection from inside the transaction. The transaction rejects
		// it too, so this is a better message, not the guarantee — that comes from the single mutation.
		var homeNodeUsers = new List<(string Username, AppUserId Id)>();
		if (homeLocalId.HasValue) {
			homeNodeUsers.Add((username, user.AppUserId));
			foreach (var homeUsername in homeNodeUsernames
										 .Distinct(StringComparer.OrdinalIgnoreCase)
										 .Where(n => !string.Equals(n, username, StringComparison.OrdinalIgnoreCase))) {
				var homeUser = await userManager.FindByNameAsync(homeUsername);
				if (homeUser is null) {
					io.WriteError($"No employee account found for username '{homeUsername}'.");
					return 1;
				}

				homeNodeUsers.Add((homeUsername, homeUser.AppUserId));
			}
		}

		ImportSubtreeResult result;
		try {
			result = await jobTrackClient.Jobs.ImportSubtreeAsync(
				new() {
					Context = new() {
						Actor = user.AppUserId,
						CorrelationId = Guid.NewGuid(),
					},
					ParentId = importRootId,
					Nodes = nodes,
					HomeNodeLocalId = homeLocalId,
					HomeNodeUserIds = [.. homeNodeUsers.Select(u => u.Id)],
				},
				cancellationToken);
		}
		catch (JobTrackException ex) {
			io.WriteError($"Import failed; nothing was created: {ex.Message}");
			return 1;
		}

		var descriptionsByLocalId = nodes.ToDictionary(n => n.LocalId, n => n.Description);
		foreach (var node in result.Nodes) {
			io.WriteLine($"Created node {node.LocalId} ('{descriptionsByLocalId[node.LocalId]}') as job node {node.JobNodeId.Value}.");
		}

		if (homeLocalId.HasValue) {
			var homeNodeId = result.Nodes.Single(n => n.LocalId == homeLocalId.Value).JobNodeId;
			foreach (var (homeUsername, _) in homeNodeUsers) {
				io.WriteLine($"Set job node {homeNodeId.Value} as the home node for '{homeUsername}'.");
			}
		}

		io.WriteLine($"Import complete: {result.Nodes.Count} node(s) created for '{username}'.");
		return 0;
	}

	/// <summary>
	///     The file-local id of the row flagged <c>"home": true</c>, or <see langword="null" /> when no
	///     row is. Rejects both a second flagged row and a flagged row with no children of its own — the
	///     latter would import as a leaf, which <see cref="IEmployeeCommands.SetHomeNodeAsync" /> refuses
	///     (<c>home-node-must-not-be-leaf</c>), so it is caught here rather than after the tree exists.
	/// </summary>
	private static long? ResolveHomeLocalId(List<JobTreeImportNodeJson> rawNodes)
	{
		var flagged = rawNodes.Where(n => n.Home).ToArray();
		if (flagged.Length == 0) {
			return null;
		}

		if (flagged.Length > 1) {
			throw new AdminCliUsageException(
				$"Only one node may be flagged \"home\": true; found {flagged.Length} (ids {string.Join(", ", flagged.Select(n => n.Id))}).");
		}

		var home = flagged[0];
		return rawNodes.Any(n => n.ParentId == home.Id)
			? home.Id
			: throw new AdminCliUsageException(
				$"Node {home.Id} ('{home.Title}') is flagged \"home\": true but has no children, and a home node may not be a leaf.");
	}
}
