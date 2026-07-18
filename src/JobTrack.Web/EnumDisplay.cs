namespace JobTrack.Web;

using System.Text;
using Microsoft.AspNetCore.Mvc.Rendering;

/// <summary>
///     Shared formatting for enum-backed status/kind labels — "InProgress" reads as "In Progress"
///     everywhere a status enum reaches the user, whether as inline text (<see cref="Label" />) or a
///     dropdown's visible option text (<see cref="BuildOptions{TEnum}" />), instead of the raw PascalCase
///     member name <c>ToString()</c>/<c>Html.GetEnumSelectList</c> would otherwise show.
/// </summary>
internal static class EnumDisplay
{
	internal static string Label(Enum value)
	{
		var name = value.ToString();
		var builder = new StringBuilder(name.Length + 4);
		for (var i = 0; i < name.Length; i++) {
			if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) {
				_ = builder.Append(' ');
			}

			_ = builder.Append(name[i]);
		}

		return builder.ToString();
	}

	internal static List<SelectListItem> BuildOptions<TEnum>() where TEnum : struct, Enum =>
		[.. Enum.GetValues<TEnum>().Select(value => new SelectListItem(Label(value), value.ToString()))];
}
