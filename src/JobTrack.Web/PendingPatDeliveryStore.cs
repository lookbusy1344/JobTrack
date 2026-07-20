namespace JobTrack.Web;

using Abstractions;
using NodaTime;

/// <summary>
///     Bounded, short-lived, one-use server-side delivery slot for a freshly issued personal access
///     token's plaintext (fix-plan §2.7). <see cref="PersonalAccessTokensModel.OnPostIssueAsync" />
///     reserves a slot before calling <c>ITokenCommands.IssueAsync</c>, publishes the plaintext into
///     it on success, and redirects to a GET carrying only the opaque <see cref="Guid" /> handle --
///     never the plaintext itself -- so a refresh of the POST response cannot resubmit the form and
///     mint another live credential. The GET consumes the slot exactly once, scoped to the
///     originally reserving actor. Nothing here is ever logged. An unpublished reservation (the
///     process crashes between the database commit and <see cref="Publish" />) simply expires
///     unconsumed -- that window is accepted, not compensated for.
/// </summary>
public sealed class PendingPatDeliveryStore : IDisposable
{
	private const int DefaultCapacity = 64;
	private static readonly Duration DefaultDeliveryWindow = Duration.FromMinutes(2);

	private readonly SemaphoreSlim capacity;
	private readonly IClock clock;
	private readonly Duration deliveryWindow;
	private readonly Dictionary<Guid, Entry> entries = [];
	private readonly Lock entriesLock = new();

	public PendingPatDeliveryStore(IClock clock, int capacity = DefaultCapacity, Duration? deliveryWindow = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

		this.clock = clock;
		this.deliveryWindow = deliveryWindow ?? DefaultDeliveryWindow;
		this.capacity = new(capacity, capacity);
	}

	public void Dispose() => capacity.Dispose();

	/// <summary>Reserves a slot for <paramref name="actor" /> before the mutating command runs. Call <see cref="Release" /> if that command then fails.</summary>
	public bool TryReserve(AppUserId actor, out Guid handle)
	{
		lock (entriesLock) {
			var now = clock.GetCurrentInstant();
			PruneExpired(now);

			if (!capacity.Wait(TimeSpan.Zero)) {
				handle = default;
				return false;
			}

			handle = Guid.NewGuid();
			entries.Add(handle, new(actor, null, null, now + deliveryWindow));
			return true;
		}
	}

	/// <summary>Publishes the plaintext into an already-reserved slot. A no-op if the slot expired and was pruned first.</summary>
	public void Publish(Guid handle, string label, string plaintext)
	{
		lock (entriesLock) {
			PruneExpired(clock.GetCurrentInstant());

			if (entries.TryGetValue(handle, out var entry)) {
				entries[handle] = entry with { Label = label, Plaintext = plaintext };
			}
		}
	}

	/// <summary>Releases a reservation whose command failed, freeing its capacity immediately rather than waiting for expiry.</summary>
	public void Release(Guid handle)
	{
		lock (entriesLock) {
			if (entries.Remove(handle)) {
				_ = capacity.Release();
			}
		}
	}

	/// <summary>
	///     Consumes a published slot exactly once, scoped to <paramref name="actor" />. Returns
	///     <see langword="false" /> without consuming anything for a missing, unpublished, expired, or
	///     wrong-actor handle -- the caller cannot tell those apart, and a wrong-actor guess (a 122-bit
	///     random handle is not practically guessable) leaves the real owner's slot intact.
	/// </summary>
	public bool TryConsume(Guid handle, AppUserId actor, out string label, out string plaintext)
	{
		lock (entriesLock) {
			var now = clock.GetCurrentInstant();
			PruneExpired(now);

			if (entries.TryGetValue(handle, out var entry) && entry.Actor == actor && entry.Plaintext is not null) {
				_ = entries.Remove(handle);
				_ = capacity.Release();

				label = entry.Label!;
				plaintext = entry.Plaintext;
				return true;
			}

			label = string.Empty;
			plaintext = string.Empty;
			return false;
		}
	}

	private void PruneExpired(Instant now)
	{
		foreach (var (handle, entry) in entries.ToArray()) {
			if (entry.ExpiresAt <= now && entries.Remove(handle)) {
				_ = capacity.Release();
			}
		}
	}

	private sealed record Entry(AppUserId Actor, string? Label, string? Plaintext, Instant ExpiresAt);
}
