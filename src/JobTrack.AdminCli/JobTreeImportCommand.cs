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
/// </summary>
public static class JobTreeImportCommand
{
	private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

	public static async Task<int> RunAsync(
		IConsoleIO io,
		UserManager<JobTrackIdentityUser> userManager,
		IJobTrackClient jobTrackClient,
		string username,
		JobNodeId importRootId,
		string jsonContent,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(io);
		ArgumentNullException.ThrowIfNull(userManager);
		ArgumentNullException.ThrowIfNull(jobTrackClient);
		ArgumentException.ThrowIfNullOrWhiteSpace(username);
		ArgumentNullException.ThrowIfNull(jsonContent);

		var user = await userManager.FindByNameAsync(username).ConfigureAwait(false);
		if (user is null) {
			io.WriteError($"No employee account found for username '{username}'.");
			return 1;
		}

		// One captured clock value for the whole import (plan §2): every relative "open"/"closed"
		// duration in the file counts back from this same instant, so a large file cannot drift.
		var importedAt = SystemClock.Instance.GetCurrentInstant();

		EquatableArray<ImportSubtreeNodeSpec> nodes;
		try {
			var rawNodes = JsonSerializer.Deserialize<List<JobTreeImportNodeJson>>(jsonContent, SerializerOptions)
						   ?? throw new AdminCliUsageException("The import file's top level must be a JSON array of nodes.");
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

		ImportSubtreeResult result;
		try {
			result = await jobTrackClient.Jobs.ImportSubtreeAsync(
				new() { Context = new() { Actor = user.AppUserId, CorrelationId = Guid.NewGuid() }, ParentId = importRootId, Nodes = nodes },
				cancellationToken).ConfigureAwait(false);
		}
		catch (JobTrackException ex) {
			io.WriteError($"Import failed; nothing was created: {ex.Message}");
			return 1;
		}

		var descriptionsByLocalId = nodes.ToDictionary(n => n.LocalId, n => n.Description);
		foreach (var node in result.Nodes) {
			io.WriteLine($"Created node {node.LocalId} ('{descriptionsByLocalId[node.LocalId]}') as job node {node.JobNodeId.Value}.");
		}

		io.WriteLine($"Import complete: {result.Nodes.Count} node(s) created for '{username}'.");
		return 0;
	}
}
