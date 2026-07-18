namespace JobTrack.Web;

using Application;
using Identity;

internal static class AuthenticationAudit
{
	public static Task RecordKnownAsync(
		IJobTrackClient jobTrackClient,
		JobTrackIdentityUser user,
		AuthenticationAuditEventKind kind,
		CancellationToken cancellationToken = default) =>
		jobTrackClient.AuthenticationAudit.RecordAsync(
			new() {
				ActorUserId = user.AppUserId,
				IdentityUserId = user.Id,
				Kind = kind,
				CorrelationId = Guid.NewGuid(),
			},
			cancellationToken);

	public static Task RecordUnknownLoginFailedAsync(
		IJobTrackClient jobTrackClient,
		CancellationToken cancellationToken = default) =>
		jobTrackClient.AuthenticationAudit.RecordAsync(
			new() { Kind = AuthenticationAuditEventKind.LoginFailed, CorrelationId = Guid.NewGuid() },
			cancellationToken);
}
