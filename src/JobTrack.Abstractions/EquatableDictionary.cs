namespace JobTrack.Abstractions;

using System.Collections;

/// <summary>
///     An immutable dictionary with <em>value</em> equality: two instances are equal when they hold the
///     same key→value entries, irrespective of insertion order. The dictionary counterpart to
///     <see cref="EquatableArray{T}" /> — a bare <see cref="IReadOnlyDictionary{TKey, TValue}" /> record
///     member compares by reference and so breaks the record's value semantics (JSV01). A
///     <c>readonly struct</c> wrapping one <see cref="Dictionary{TKey, TValue}" />; <c>default</c> behaves
///     as empty.
/// </summary>
public readonly struct EquatableDictionary<TKey, TValue> : IReadOnlyDictionary<TKey, TValue>, IEquatable<EquatableDictionary<TKey, TValue>>
	where TKey : notnull
{
	// A shared instance for the `default` case: `entries ?? []` would otherwise allocate a throwaway
	// dictionary on every single member access of a default-valued instance. Never handed out, and
	// never mutated, so sharing it is safe.
	private static readonly Dictionary<TKey, TValue> Empty = [];

	private readonly Dictionary<TKey, TValue>? entries;

	internal EquatableDictionary(Dictionary<TKey, TValue> entries) => this.entries = entries;

	private Dictionary<TKey, TValue> Entries => entries ?? Empty;

	/// <summary>The number of entries.</summary>
	public int Count => Entries.Count;

	/// <summary>The dictionary's keys.</summary>
	public IEnumerable<TKey> Keys => Entries.Keys;

	/// <summary>The dictionary's values.</summary>
	public IEnumerable<TValue> Values => Entries.Values;

	/// <summary>The value stored for <paramref name="key" />.</summary>
	public TValue this[TKey key] => Entries[key];

	/// <summary>Whether <paramref name="key" /> has an entry.</summary>
	public bool ContainsKey(TKey key) => Entries.ContainsKey(key);

	/// <summary>Attempts to read the value stored for <paramref name="key" />.</summary>
	public bool TryGetValue(TKey key, out TValue value) => Entries.TryGetValue(key, out value!);

	/// <summary>Whether the two dictionaries hold the same key/value entries, regardless of order.</summary>
	public bool Equals(EquatableDictionary<TKey, TValue> other)
	{
		if (Count != other.Count) {
			return false;
		}

		var comparer = EqualityComparer<TValue>.Default;
		foreach (var (key, value) in Entries) {
			if (!other.Entries.TryGetValue(key, out var otherValue) || !comparer.Equals(value, otherValue)) {
				return false;
			}
		}

		return true;
	}

	/// <inheritdoc cref="Equals(EquatableDictionary{TKey, TValue})" />
	public override bool Equals(object? obj) => obj is EquatableDictionary<TKey, TValue> other && Equals(other);

	/// <summary>An order-independent hash code combining every key/value entry.</summary>
	public override int GetHashCode()
	{
		// Order-independent: XOR per-entry hashes so equal maps with differing insertion order agree.
		var hash = 0;
		foreach (var (key, value) in Entries) {
			hash ^= HashCode.Combine(key, value);
		}

		return hash;
	}

	/// <summary>
	///     Enumerates the key/value entries. Returns <see cref="Dictionary{TKey, TValue}" />'s own struct
	///     enumerator, which <c>foreach</c> binds to by pattern — see the matching note on
	///     <see cref="EquatableArray{T}.GetEnumerator" />.
	/// </summary>
	public Dictionary<TKey, TValue>.Enumerator GetEnumerator() => Entries.GetEnumerator();

	IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => Entries.GetEnumerator();

	IEnumerator IEnumerable.GetEnumerator() => Entries.GetEnumerator();

	/// <inheritdoc cref="Equals(EquatableDictionary{TKey, TValue})" />
	public static bool operator ==(EquatableDictionary<TKey, TValue> left, EquatableDictionary<TKey, TValue> right) => left.Equals(right);

	/// <summary>The negation of <see cref="operator ==" />.</summary>
	public static bool operator !=(EquatableDictionary<TKey, TValue> left, EquatableDictionary<TKey, TValue> right) => !left.Equals(right);
}
