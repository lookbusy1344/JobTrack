namespace JobTrack.Web;

using System.Globalization;

/// <summary>
///     Remembers a page's last-used single-select filter — a worker/owner id, or "Everyone/All" for
///     the unfiltered view — in server-side session state, so returning to a page restores the choice
///     the user last made rather than snapping back to a default. A stored empty string is a remembered
///     "Everyone/All"; an absent key means nothing has been remembered yet, so the page falls back to
///     its own permission-aware default. <see cref="TryRecall" /> is the FDG expected-absence
///     <c>Try*</c> form (nothing remembered is an ordinary, non-exceptional state), complementing the
///     always-succeeding <see cref="Remember" />.
/// </summary>
internal static class FilterMemory
{
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
