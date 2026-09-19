namespace JobTrack.Application.Tests;

using System.Data.Common;
using Abstractions;
using AwesomeAssertions;

public sealed class JobTrackOperationTests
{
	[Fact]
	public async Task A_provider_exception_is_wrapped_at_the_public_operation_boundary()
	{
		var providerException = new FakeDbException();

		var act = () => JobTrackOperation.TraceAsync(
			"test.provider-failure", () => Task.FromException(providerException));

		var exception = await act.Should().ThrowExactlyAsync<PersistenceException>();
		exception.Which.InnerException.Should().BeSameAs(providerException);
	}

	[Fact]
	public async Task A_provider_exception_from_a_result_operation_is_wrapped_at_the_public_operation_boundary()
	{
		var providerException = new FakeDbException();

		Func<Task> act = () => JobTrackOperation.TraceAsync(
			"test.provider-failure", () => Task.FromException<int>(providerException));

		var exception = await act.Should().ThrowExactlyAsync<PersistenceException>();
		exception.Which.InnerException.Should().BeSameAs(providerException);
	}

	[Fact]
	public async Task An_existing_JobTrackException_with_a_provider_cause_is_not_rewrapped()
	{
		var expected = new TransientPersistenceException(new FakeDbException());

		var act = () => JobTrackOperation.TraceAsync(
			"test.transient-provider-failure", () => Task.FromException(expected));

		var exception = await act.Should().ThrowExactlyAsync<TransientPersistenceException>();
		exception.Which.Should().BeSameAs(expected);
	}

	[Fact]
	public async Task Cancellation_with_a_provider_cause_preserves_the_cancellation_contract()
	{
		var expected = new OperationCanceledException("Cancelled.", new FakeDbException(), CancellationToken.None);

		var act = () => JobTrackOperation.TraceAsync(
			"test.cancelled-provider-operation", () => Task.FromException(expected));

		var exception = await act.Should().ThrowExactlyAsync<OperationCanceledException>();
		exception.Which.Should().BeSameAs(expected);
	}

	private sealed class FakeDbException : DbException;
}
