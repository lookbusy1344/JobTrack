namespace JobTrack.Domain.Tests;

using Abstractions;
using AwesomeAssertions;

/// <summary>See <see cref="MoneyTests" /> for why this Abstractions value type is tested here.</summary>
public sealed class EquatableDictionaryTests
{
	private static EquatableDictionary<string, int> Of(params (string Key, int Value)[] entries) =>
		EquatableDictionaryFactory.CopyOf(entries.ToDictionary(entry => entry.Key, entry => entry.Value));

	[Fact]
	public void Two_dictionaries_with_the_same_entries_are_equal()
	{
		var a = Of(("a", 1), ("b", 2));
		var b = Of(("a", 1), ("b", 2));

		(a == b).Should().BeTrue();
	}

	[Fact]
	public void Two_dictionaries_with_the_same_entries_in_a_different_order_are_equal()
	{
		var a = Of(("a", 1), ("b", 2));
		var b = Of(("b", 2), ("a", 1));

		(a == b).Should().BeTrue();
	}

	[Fact]
	public void Dictionaries_with_a_differing_value_for_a_shared_key_are_not_equal()
	{
		var a = Of(("a", 1));
		var b = Of(("a", 2));

		(a == b).Should().BeFalse();
	}

	[Fact]
	public void Dictionaries_of_different_size_are_not_equal()
	{
		var a = Of(("a", 1));
		var b = Of(("a", 1), ("b", 2));

		(a == b).Should().BeFalse();
	}

	[Fact]
	public void Equal_dictionaries_share_a_hash_code()
	{
		var a = Of(("a", 1), ("b", 2));
		var b = Of(("b", 2), ("a", 1));

		a.GetHashCode().Should().Be(b.GetHashCode());
	}

	[Fact]
	public void The_default_uninitialized_value_behaves_as_empty()
	{
		var uninitialized = default(EquatableDictionary<string, int>);

		uninitialized.Count.Should().Be(0);
		uninitialized.ContainsKey("a").Should().BeFalse();
	}

	[Fact]
	public void Values_are_readable_by_key()
	{
		var dictionary = Of(("a", 1));

		dictionary["a"].Should().Be(1);
		dictionary.ContainsKey("a").Should().BeTrue();
	}
}
