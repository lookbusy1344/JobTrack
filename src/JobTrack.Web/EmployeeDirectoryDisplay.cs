namespace JobTrack.Web;

using System.Globalization;
using Abstractions;
using Application;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     Shared formatting for <see cref="EmployeeDirectoryEntry" /> across every page that shows or
///     selects an employee by id — "display name (username)" instead of a bare <see cref="AppUserId" />,
///     with a fallback that still names the numeric id when it does not resolve in the loaded
///     directory (e.g. an owner disabled or role-revoked since assignment when using
///     <see cref="IJobQueries.GetEmployeeDirectoryAsync" />'s narrower, workflow-only scope).
/// </summary>
internal static class EmployeeDirectoryDisplay
{
	internal static string Format(EmployeeDirectoryEntry entry) => $"{entry.DisplayName} ({entry.UserName})";

	internal static string Describe(IReadOnlyDictionary<AppUserId, EmployeeDirectoryEntry> directoryById, long? userId,
		string noneLabel = "Unassigned")
	{
		if (userId is not long id) {
			return noneLabel;
		}

		return directoryById.TryGetValue(new(id), out var entry)
			? Format(entry)
			: $"User #{id.ToString(CultureInfo.InvariantCulture)}";
	}

	internal static List<SelectListItem> BuildOptions(IEnumerable<EmployeeDirectoryEntry> directory, SelectListItem? firstOption = null)
	{
		var options = directory
			.OrderBy(entry => entry.DisplayName, StringComparer.Ordinal)
			.Select(entry => new SelectListItem(Format(entry), entry.Id.Value.ToString(CultureInfo.InvariantCulture)));

		return firstOption is null ? [.. options] : [firstOption, .. options];
	}

	/// <summary>
	///     Like <see cref="BuildOptions(IEnumerable{EmployeeDirectoryEntry}, SelectListItem?)" />,
	///     with <paramref name="selectedId" />'s option marked <see cref="SelectListItem.Selected" /> —
	///     for a <c>&lt;select&gt;</c> rendered outside a bound <c>asp-for</c> context (e.g. inside a
	///     shared partial with no page-model property of its own).
	/// </summary>
	internal static List<SelectListItem> BuildOptions(IEnumerable<EmployeeDirectoryEntry> directory, AppUserId selectedId)
	{
		var options = BuildOptions(directory);
		var selectedValue = selectedId.Value.ToString(CultureInfo.InvariantCulture);
		foreach (var option in options) {
			option.Selected = option.Value == selectedValue;
		}

		return options;
	}
}
