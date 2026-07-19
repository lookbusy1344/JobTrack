namespace JobTrack.Web;

using Abstractions;
using NodaTime;

/// <summary>Resolves the viewing employee's own <see cref="DateTimeZone" />, for timestamp display and backdate parsing alike.</summary>
public interface IViewerTimeZoneResolver
{
	/// <summary>
	///     The <see cref="DateTimeZone" /> for <paramref name="actorId" />'s own
	///     <c>EmployeeProfileResult.IanaTimeZone</c>. Every account (employee or Requester) carries a
	///     mandatory zone (schema <c>app_user.iana_time_zone NOT NULL</c>), so this only falls back to
	///     UTC if a stored id somehow no longer resolves in the TZDB -- display should never hard-fail
	///     over a formatting concern.
	/// </summary>
	Task<DateTimeZone> ResolveAsync(AppUserId actorId, CancellationToken cancellationToken = default);
}
