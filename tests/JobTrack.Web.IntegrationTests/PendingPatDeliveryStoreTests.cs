namespace JobTrack.Web.IntegrationTests;

using Abstractions;
using AwesomeAssertions;
using NodaTime;

/// <summary>
///     Unit tests for the bounded, one-use, short-lived PAT plaintext delivery store (fix-plan
///     §2.7): the mechanism that lets <c>OnPostIssueAsync</c> follow Post/Redirect/Get without ever
///     putting the plaintext token in TempData, a cookie, or a URL.
/// </summary>
public sealed class PendingPatDeliveryStoreTests
{
	private static readonly Duration DeliveryWindow = Duration.FromMinutes(2);
	private static readonly AppUserId ActorOne = new(1);
	private static readonly AppUserId ActorTwo = new(2);

	[Fact]
	public void A_published_slot_is_consumed_exactly_once()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 4, DeliveryWindow);

		store.TryReserve(ActorOne, out var handle).Should().BeTrue();
		store.Publish(handle, "laptop", "jtpat_secret");

		store.TryConsume(handle, ActorOne, out var label, out var plaintext).Should().BeTrue();
		label.Should().Be("laptop");
		plaintext.Should().Be("jtpat_secret");

		store.TryConsume(handle, ActorOne, out _, out _).Should().BeFalse("the slot is destroyed by the first consume");
	}

	[Fact]
	public async Task Concurrent_consumers_receive_a_published_slot_once()
	{
		const int consumerCount = 32;
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 1, DeliveryWindow);

		store.TryReserve(ActorOne, out var handle).Should().BeTrue();
		store.Publish(handle, "laptop", "jtpat_secret");

		using var ready = new CountdownEvent(consumerCount);
		using var start = new ManualResetEventSlim();
		var consumers = Enumerable.Range(0, consumerCount).Select(_ => Task.Factory.StartNew(() => {
			ready.Signal();
			start.Wait();
			return store.TryConsume(handle, ActorOne, out var ignoredLabel, out var ignoredPlaintext);
		}, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

		ready.Wait();
		start.Set();
		var results = await Task.WhenAll(consumers);

		results.Count(result => result).Should().Be(1, "the delivery handle is a one-use secret capability");
	}

	[Fact]
	public void Consuming_with_a_different_actor_fails_and_leaves_the_slot_for_the_owner()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 4, DeliveryWindow);

		store.TryReserve(ActorOne, out var handle).Should().BeTrue();
		store.Publish(handle, "laptop", "jtpat_secret");

		store.TryConsume(handle, ActorTwo, out _, out _).Should().BeFalse();
		store.TryConsume(handle, ActorOne, out var label, out var plaintext).Should().BeTrue();
		label.Should().Be("laptop");
		plaintext.Should().Be("jtpat_secret");
	}

	[Fact]
	public void An_unpublished_reservation_cannot_be_consumed()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 4, DeliveryWindow);

		store.TryReserve(ActorOne, out var handle).Should().BeTrue();

		store.TryConsume(handle, ActorOne, out _, out _).Should()
			.BeFalse("a process crash between reservation and publication must not leak an empty slot as success");
	}

	[Fact]
	public void Releasing_a_reservation_frees_capacity_for_another_issuance()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 1, DeliveryWindow);

		store.TryReserve(ActorOne, out var firstHandle).Should().BeTrue();
		store.TryReserve(ActorOne, out _).Should().BeFalse("capacity is exhausted");

		store.Release(firstHandle);

		store.TryReserve(ActorOne, out _).Should().BeTrue("releasing the failed reservation frees its slot");
	}

	[Fact]
	public void Reservation_is_refused_once_bounded_capacity_is_exhausted()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 2, DeliveryWindow);

		store.TryReserve(ActorOne, out _).Should().BeTrue();
		store.TryReserve(ActorOne, out _).Should().BeTrue();
		store.TryReserve(ActorOne, out _).Should().BeFalse();
	}

	[Fact]
	public void An_expired_slot_cannot_be_consumed_and_its_capacity_is_reclaimed()
	{
		var clock = new ManualClock();
		using var store = new PendingPatDeliveryStore(clock, 1, DeliveryWindow);

		store.TryReserve(ActorOne, out var handle).Should().BeTrue();
		store.Publish(handle, "laptop", "jtpat_secret");

		clock.Current += DeliveryWindow + Duration.FromSeconds(1);

		store.TryConsume(handle, ActorOne, out _, out _).Should().BeFalse("the delivery window has elapsed");
		store.TryReserve(ActorOne, out _).Should().BeTrue("the expired entry's capacity is reclaimed on the next reserve");
	}

	private sealed class ManualClock : IClock
	{
		public Instant Current { get; set; } = Instant.FromUnixTimeTicks(0);

		public Instant GetCurrentInstant() => Current;
	}
}
