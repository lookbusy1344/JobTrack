namespace JobTrack.Web;

using System.Globalization;

/// <summary>
///     Remembers a page's last-used filters — a worker/owner id (or "Everyone/All" for the unfiltered
///     view), a checkbox flag, or free text — in server-side session state, so returning to a page
///     restores the choices the user last made rather than snapping back to a default. A stored empty string is a remembered
///     "Everyone/All"; an absent key means nothing has been remembered yet, so the page falls back to
///     its own permission-aware default. <see cref="TryRecall" /> is the FDG expected-absence
///     <c>Try*</c> form (nothing remembered is an ordinary, non-exceptional state), complementing the
///     always-succeeding <see cref="Remember" />.
/// </summary>
internal static class FilterMemory
{
	private const string TrueMarker = "1";
	private const string FalseMarker = "0";

	internal static void Remember(ISession session, string key, long? value)
	{
		ArgumentNullException.ThrowIfNull(session);
		session.SetString(key, value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
	}

	/// <summary>
	///     Resolves a page's effective single-select filter and keeps the memory current in one step:
	///     when <paramref name="explicitlyProvided" /> (the query carried the filter, so the user just
	///     chose it — empty counts as an explicit "Everyone/All"), that value is used and remembered;
	///     otherwise the last remembered choice is recalled, and failing that
	///     <paramref name="fallback" /> (the page's own permission-aware default) applies. In every case
	///     <see langword="null" /> means the unfiltered "Everyone/All" view.
	/// </summary>
	internal static long? Resolve(ISession session, string key, bool explicitlyProvided, long? explicitValue, long? fallback)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (explicitlyProvided) {
			Remember(session, key, explicitValue);
			return explicitValue;
		}

		return TryRecall(session, key, out var recalled) ? recalled : fallback;
	}

	/// <summary>
	///     <see cref="Resolve" /> for a checkbox-shaped filter (unassigned-only, exclude-blocked, show
	///     the whole tree). A submitted filter form always carries its checkboxes, so
	///     <paramref name="explicitlyProvided" /> distinguishes "the user just unticked it" from "the
	///     request named no filters at all" — only the latter recalls.
	/// </summary>
	internal static bool ResolveFlag(ISession session, string key, bool explicitlyProvided, bool explicitValue)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (explicitlyProvided) {
			session.SetString(key, explicitValue ? TrueMarker : FalseMarker);
			return explicitValue;
		}

		return session.GetString(key) == TrueMarker;
	}

	/// <summary>
	///     <see cref="Resolve" /> for a free-text filter. A remembered empty string is a remembered
	///     "no search text", so it recalls as <see langword="null" /> exactly as an absent key does.
	/// </summary>
	internal static string? ResolveText(ISession session, string key, bool explicitlyProvided, string? explicitValue)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (explicitlyProvided) {
			session.SetString(key, explicitValue ?? string.Empty);
			return explicitValue;
		}

		var recalled = session.GetString(key);

		return string.IsNullOrEmpty(recalled) ? null : recalled;
	}

	/// <summary>
	///     Reads a remembered filter. Returns <see langword="false" /> when nothing is stored under
	///     <paramref name="key" />; otherwise <see langword="true" /> with <paramref name="value" /> set
	///     to the remembered id, or <see langword="null" /> for a remembered "Everyone/All".
	/// </summary>
	internal static bool TryRecall(ISession session, string key, out long? value)
	{
		ArgumentNullException.ThrowIfNull(session);
		var raw = session.GetString(key);
		if (raw is null) {
			value = null;
			return false;
		}

		value = raw.Length == 0 ? null : long.Parse(raw, CultureInfo.InvariantCulture);
		return true;
	}
}
