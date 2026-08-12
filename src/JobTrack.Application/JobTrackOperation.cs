namespace JobTrack.Application;

using System.Diagnostics;
using Abstractions;

internal static class JobTrackOperation
{
	private static readonly ActivitySource Source = new(JobTrackDiagnostics.ActivitySourceName);

	public static Task<T> TraceAsync<T>(
		string operation,
		CommandContext context,
		Func<Activity, Activity>? enrich,
		Func<Task<T>> action) =>
		TraceCoreAsync(Start(operation, context, enrich), action);

	public static Task<T> TraceAsync<T>(
		string operation,
		Guid correlationId,
		Func<Activity, Activity>? enrich,
		Func<Task<T>> action) =>
		TraceCoreAsync(Start(operation, null, correlationId, enrich), action);

	public static Task<T> TraceAsync<T>(string operation, Func<Task<T>> action) =>
		TraceCoreAsync(Start(operation, null, null), action);

	public static Task TraceAsync(
		string operation,
		CommandContext context,
		Func<Activity, Activity>? enrich,
		Func<Task> action) =>
		TraceCoreAsync(Start(operation, context, enrich), action);

	public static Task TraceAsync(string operation, Func<Task> action) =>
		TraceCoreAsync(Start(operation, null, null), action);

	private static async Task<T> TraceCoreAsync<T>(Activity? activity, Func<Task<T>> action)
	{
		using (activity) {
			try {
				var result = await action().ConfigureAwait(false);
				_ = activity?.SetStatus(ActivityStatusCode.Ok);
				return result;
			}
			catch (Exception exception) {
				_ = activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
				throw;
			}
		}
	}

	private static async Task TraceCoreAsync(Activity? activity, Func<Task> action)
	{
		using (activity) {
			try {
				await action().ConfigureAwait(false);
				_ = activity?.SetStatus(ActivityStatusCode.Ok);
			}
			catch (Exception exception) {
				_ = activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
				throw;
			}
		}
	}

	private static Activity? Start(string operation, CommandContext? context, Func<Activity, Activity>? enrich)
	{
		return context is null
			? Start(operation, null, Guid.Empty, enrich)
			: Start(operation, context.Actor, context.CorrelationId, enrich);
	}

	private static Activity? Start(
		string operation, AppUserId? actorId, Guid correlationId, Func<Activity, Activity>? enrich)
	{
		var activity = Source.StartActivity(operation);
		if (activity is null) {
			return null;
		}

		if (actorId is not null) {
			_ = activity.SetTag(JobTrackDiagnostics.Tags.ActorId, actorId.Value.Value);
		}

		if (correlationId != Guid.Empty) {
			_ = activity.SetTag(JobTrackDiagnostics.Tags.CorrelationId, correlationId.ToString("D"));
		}

		return enrich is null ? activity : enrich(activity);
	}

	public static Func<Activity, Activity> WithNodeId(JobNodeId nodeId) =>
		activity => {
			_ = activity.SetTag(JobTrackDiagnostics.Tags.TargetNodeId, nodeId.Value);
			return activity;
		};

	public static Func<Activity, Activity> WithUserId(AppUserId userId) =>
		activity => {
			_ = activity.SetTag(JobTrackDiagnostics.Tags.TargetUserId, userId.Value);
			return activity;
		};
}
