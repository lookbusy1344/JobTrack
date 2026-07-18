namespace JobTrack.Domain.Authorization;

using Abstractions;

/// <summary>
///     Pure authorization rules for personal access token (PAT) management (ADR 0029). A PAT
///     authenticates strictly as its issuing user and carries no capability beyond that user's own
///     scope, so issuing a token is always self-service; listing and revoking extend to
///     <see cref="EmployeeRole.Administrator" /> so an incident responder can revoke another user's
///     tokens without needing that user's own credential.
/// </summary>
public static class PersonalAccessTokenAccessPolicy
{
	/// <summary>An actor may only issue a token for themselves — never on another user's behalf.</summary>
	public static bool CanIssue(AppUserId actorId, AppUserId targetUserId) => actorId == targetUserId;

	/// <summary>
	///     An actor may list or revoke a user's tokens if they are that user, or hold
	///     <see cref="EmployeeRole.Administrator" />.
	/// </summary>
	public static bool CanManage(
		AppUserId actorId, AppUserId targetUserId, IReadOnlyCollection<EmployeeRole> actorRoles)
	{
		ArgumentNullException.ThrowIfNull(actorRoles);

		return actorId == targetUserId || actorRoles.Contains(EmployeeRole.Administrator);
	}
}
