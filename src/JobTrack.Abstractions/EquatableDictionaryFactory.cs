namespace JobTrack.Abstractions;

/// <summary>Explicit copy helpers for <see cref="EquatableDictionary{TKey, TValue}" />.</summary>
public static class EquatableDictionaryFactory
{
	/// <summary>
	///     Explicitly copy a dictionary into an immutable value object.
	/// </summary>
	public static EquatableDictionary<TKey, TValue> CopyOf<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> entries)
		where TKey : notnull
	{
		ArgumentNullException.ThrowIfNull(entries);
		return new(new(entries));
	}
}
