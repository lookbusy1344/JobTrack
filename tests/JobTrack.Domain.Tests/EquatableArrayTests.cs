namespace JobTrack.Domain.Tests;

using Abstractions;
using AwesomeAssertions;

/// <summary>See <see cref="MoneyTests" /> for why this Abstractions value type is tested here.</summary>
public sealed class EquatableArrayTests
{
	[Fact]
	public void Two_arrays_with_the_same_elements_in_the_same_order_are_equal()
	{
		EquatableArray<int> a = [1, 2, 3];
		EquatableArray<int> b = [1, 2, 3];

		(a == b).Should().BeTrue();
	}

	[Fact]
	public void Two_arrays_with_the_same_elements_in_a_different_order_are_not_equal()
	{
		EquatableArray<int> a = [1, 2, 3];
		EquatableArray<int> b = [3, 2, 1];

		(a == b).Should().BeFalse();
	}

	[Fact]
	public void Arrays_of_different_length_are_not_equal()
	{
		EquatableArray<int> a = [1, 2];
		EquatableArray<int> b = [1, 2, 3];

		(a == b).Should().BeFalse();
	}

	[Fact]
	public void Equal_arrays_share_a_hash_code()
	{
		EquatableArray<int> a = [1, 2, 3];
		EquatableArray<int> b = [1, 2, 3];

		a.GetHashCode().Should().Be(b.GetHashCode());
	}

	[Fact]
	public void An_empty_array_is_valid_and_has_zero_count()
	{
		EquatableArray<int> empty = [];

		empty.Count.Should().Be(0);
	}

	[Fact]
	public void Elements_are_indexable_and_enumerable_in_order()
	{
		EquatableArray<int> array = [1, 2, 3];

		array[1].Should().Be(2);
		array.Should().Equal(1, 2, 3);
	}

	[Fact]
	public void The_default_uninitialized_value_behaves_as_empty()
	{
		var uninitialized = default(EquatableArray<int>);

		uninitialized.Count.Should().Be(0);
		(uninitialized == []).Should().BeTrue();
	}
}
