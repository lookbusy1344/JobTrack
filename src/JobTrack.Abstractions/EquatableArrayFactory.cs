namespace JobTrack.Abstractions;

using System.Collections.Immutable;

/// <summary>
///     The collection-expression builder and explicit copy helpers for <see cref="EquatableArray{T}" />
///     (<c>[]</c> / <c>[.. xs]</c>).
/// </summary>
public static class EquatableArray
{
	/// <summary>The collection-expression builder invoked for <c>EquatableArray&lt;T&gt; xs = [.. items];</c>.</summary>
	public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) => new([.. items]);

	/// <summary>
	///     Explicitly copy a sequence into a structurally comparable immutable array.
	/// </summary>
	public static EquatableArray<T> CopyOf<T>(IEnumerable<T> items)
	{
		ArgumentNullException.ThrowIfNull(items);
		return new([.. items]);
	}

	/// <summary>Wrap an already immutable array without another copy.</summary>
	public static EquatableArray<T> CopyOf<T>(ImmutableArray<T> items) => new(items);
}
