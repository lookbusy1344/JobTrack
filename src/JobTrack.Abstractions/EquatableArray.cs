namespace JobTrack.Abstractions;

using System.Collections;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

/// <summary>
///     An immutable array with <em>value</em> equality: two instances are equal when their elements are
///     equal in order. This is the piece a bare <see cref="IReadOnlyList{T}" /> record member is missing —
///     a list field falls back to reference equality, silently breaking the value semantics a
///     <c>record</c> advertises (the JSV01 defect class). Backed by <see cref="ImmutableArray{T}" />, a
///     <c>readonly struct</c> so it never allocates beyond the array it wraps, and a collection-expression
///     target so <c>[]</c> / <c>[.. xs]</c> construct it directly.
/// </summary>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
public readonly struct EquatableArray<T>(ImmutableArray<T> items) : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
{
	private ImmutableArray<T> Items { get => field.IsDefault ? [] : field; } = items;

	/// <summary>The number of elements.</summary>
	public int Count => Items.Length;

	/// <summary>The element at <paramref name="index" />.</summary>
	public T this[int index] => Items[index];

	/// <summary>Whether the two arrays contain the same elements in the same order.</summary>
	public bool Equals(EquatableArray<T> other) => Items.AsSpan().SequenceEqual(other.Items.AsSpan());

	/// <inheritdoc cref="Equals(EquatableArray{T})" />
	public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

	/// <summary>A hash code combining every element in order.</summary>
	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var item in Items) {
			hash.Add(item);
		}

		return hash.ToHashCode();
	}

	/// <summary>
	///     Enumerates the elements in order. Returns <see cref="ImmutableArray{T}" />'s own struct
	///     enumerator, which <c>foreach</c> binds to by pattern in preference to
	///     <see cref="IEnumerable{T}" /> — a plain interface-returning overload would box both the
	///     underlying array and its enumerator on every iteration of what is meant to be an
	///     allocation-free wrapper.
	/// </summary>
	public ImmutableArray<T>.Enumerator GetEnumerator() => Items.GetEnumerator();

	IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)Items).GetEnumerator();

	/// <inheritdoc cref="Equals(EquatableArray{T})" />
	public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

	/// <summary>The negation of <see cref="operator ==" />.</summary>
	public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);
}
