namespace JobTrack.Web;

using Abstractions;
using Application;
using NodaTime;

/// <inheritdoc cref="IViewerTimeZoneResolver" />
public sealed class ViewerTimeZoneResolver(IJobTrackClient jobTrackClient) : IViewerTimeZoneResolver
{
	public async Task<DateTimeZone> ResolveAsync(AppUserId actorId, CancellationToken cancellationToken = default)
	{
		var profile = await jobTrackClient.Query.GetEmployeeProfileAsync(
			new() { Context = new() { Actor = actorId, CorrelationId = Guid.NewGuid() }, TargetUserId = actorId },
			cancellationToken).ConfigureAwait(false);

		return DateTimeZoneProviders.Tzdb.GetZoneOrNull(profile.IanaTimeZone)
			   ?? throw new UnknownStoredTimeZoneException(
				   $"Employee {actorId} stores time zone '{profile.IanaTimeZone}', which the current TZDB no longer recognizes.");
	}
}
