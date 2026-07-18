using System.Globalization;
using JobTrack.ExternalApiClient;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length < 4) {
			await Console.Error.WriteLineAsync(
				"Usage: JobTrack.ExternalApiClient <baseUrl> <bearerToken> <browse|start-session> <nodeId> [workedByUserId]");
			return 1;
		}

		var baseUrl = new Uri(args[0]);
		var token = args[1];
		var command = args[2];
		var nodeId = long.Parse(args[3], CultureInfo.InvariantCulture);

		using var client = new JobTrackApiClient(baseUrl, token);

		try {
			return await RunAsync(client, command, nodeId, args);
		}
		catch (JobTrackApiUnauthorizedException ex) {
			await Console.Error.WriteLineAsync($"Authentication failed: {ex.Message}. Issue a fresh personal access token and retry.");
			return 401;
		}
		catch (JobTrackApiForbiddenException ex) {
			await Console.Error.WriteLineAsync($"Not authorized: {ex.Message}");
			return 403;
		}
		catch (JobTrackApiNotFoundException ex) {
			await Console.Error.WriteLineAsync($"Not found: {ex.Message}");
			return 404;
		}
		catch (JobTrackApiConflictException ex) {
			await Console.Error.WriteLineAsync($"Conflict: {ex.Message}");
			return 409;
		}
	}

	private static async Task<int> RunAsync(JobTrackApiClient client, string command, long nodeId, string[] args)
	{
		switch (command) {
			case "browse": {
					var detail = await client.GetJobNodeAsync(nodeId);
					Console.WriteLine($"Node {detail.Node.Id}: {detail.Node.Description} ({detail.Node.Kind})");
					foreach (var ancestor in detail.Ancestors) {
						Console.WriteLine($"  ancestor: {ancestor.Id} {ancestor.Description}");
					}

					var children = await client.GetJobChildrenAsync(nodeId);
					foreach (var child in children.Items) {
						Console.WriteLine($"  child: {child.Id} {child.Description}");
					}

					if (children.HasMore) {
						Console.WriteLine($"  ({children.Items.Count} of more than {children.Offset + children.Items.Count} children shown)");
					}

					return 0;
				}

			case "start-session": {
					var workedByUserId = long.Parse(args[4], CultureInfo.InvariantCulture);
					var session = await client.StartSessionAsync(nodeId, workedByUserId);
					Console.WriteLine($"Session {session.Id} started at {session.StartedAt:O}");
					return 0;
				}

			default:
				await Console.Error.WriteLineAsync($"Unknown command '{command}'.");
				return 1;
		}
	}
}
