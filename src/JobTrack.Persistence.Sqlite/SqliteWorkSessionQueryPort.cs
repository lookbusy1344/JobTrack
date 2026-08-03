namespace JobTrack.Persistence.Sqlite;

using Abstractions;
using Application;
using Application.Ports;
using Domain.Concurrency;
using Domain.Intervals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using Shared;
using Shared.Entities;

/// <summary>
///     SQLite implementation of <see cref="IWorkSessionQueryPort" /> (plan §8.5 slice 4). One
///     <see cref="SqliteJobTrackDbContext" /> per call, read-only throughout.
/// </summary>
internal sealed class SqliteWorkSessionQueryPort : IWorkSessionQueryPort
{
	private readonly IReadOnlyList<IInterceptor> _interceptors;
	private readonly IClock clock;
	private readonly string connectionString;

	/// <summary>Creates the port over the given SQLite connection string.</summary>
	public SqliteWorkSessionQueryPort(string connectionString, IClock clock) : this(connectionString, clock, [])
	{
	}

	/// <summary>Test-only seam (Stage 4 efficiency guards) for attaching a command-count interceptor.</summary>
	internal SqliteWorkSessionQueryPort(string connectionString, IClock clock, IReadOnlyList<IInterceptor> interceptors)
	{
		this.connectionString = connectionString;
		this.clock = clock;
		_interceptors = interceptors;
	}

	/// <inheritdoc />
	public async Task<WorkSessionQueryResult> GetSessionsAsync(
		AppUserId actorId, JobNodeId leafWorkId, AppUserId? workedByUserId,
		int offset = 0, int? limit = null, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (!await context.Set<JobNodeEntity>().AsNoTracking()
				.AnyAsync(n => n.Id == leafWorkId, cancellationToken).ConfigureAwait(false)) {
			throw new EntityNotFoundException($"Job node {leafWorkId} does not exist.");
		}

		var query = context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => s.LeafWorkId == leafWorkId && (workedByUserId == null || s.WorkedByUserId == workedByUserId))
			.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
			.Skip(offset)
			.Select(s => new WorkSessionResult {
				Id = s.Id,
				LeafWorkId = s.LeafWorkId,
				WorkedByUserId = s.WorkedByUserId,
				StartedAt = s.StartedAt,
				FinishedAt = s.FinishedAt,
				ChangedAt = s.ChangedAt,
				Version = s.RowVersion,
			});
		var sessions = await (limit.HasValue ? query.Take(limit.Value) : query)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, Sessions = [.. sessions] };
	}

	/// <inheritdoc />
	public async Task<WorkSessionQueryResult> GetActiveSessionsAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (leafWorkIds.Count == 0) {
			return new() { ActorRoles = actorRoles, Sessions = [] };
		}

		var leafWorkIdList = leafWorkIds.ToList();
		var sessions = await context.Set<WorkSessionEntity>().AsNoTracking()
			.Where(s => s.FinishedAt == null && leafWorkIdList.Contains(s.LeafWorkId))
			.Select(s => new WorkSessionResult {
				Id = s.Id,
				LeafWorkId = s.LeafWorkId,
				WorkedByUserId = s.WorkedByUserId,
				StartedAt = s.StartedAt,
				FinishedAt = s.FinishedAt,
				ChangedAt = s.ChangedAt,
				Version = s.RowVersion,
			})
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, Sessions = [.. sessions] };
	}

	/// <inheritdoc />
	public async Task<WorkSessionManageCapabilityQueryResult> GetManageCapabilitiesAsync(
		AppUserId actorId, EquatableArray<JobNodeId> leafWorkIds, CancellationToken cancellationToken = default)
	{
		await using var context = CreateContext();

		var actorRoles = await GetActorRolesAsync(context, actorId, cancellationToken).ConfigureAwait(false);

		if (leafWorkIds.Count == 0) {
			return new() { ActorRoles = actorRoles, ControlledLeafWorkIds = [] };
		}

		var controlledIds = await SqliteControlledLeafQuery.GetControlledLeafIdsAsync(
			context, actorId.Value, [.. leafWorkIds.Select(id => id.Value)], cancellationToken).ConfigureAwait(false);

		return new() { ActorRoles = actorRoles, ControlledLeafWorkIds = [.. controlledIds.Select(id => new JobNodeId(id))] };
	}

	/// <inheritdoc />
	public async Task<ConcurrentWorkQueryResult> GetConcurrentSessionsAsync(
		JobNodeId nodeId, Instant asOf, int maxSubjectSessionCount, int maxConcurrentSessionCount,
		CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubjectSessionCount);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentSessionCount);

		await using var context = CreateContext();

		// A session that had not yet started as at asOf has no interval to report; every other one is
		// bounded by asOf, so no interval is ever empty or inverted (WorkInterval rejects both).
		var started = context.Set<WorkSessionEntity>().AsNoTracking().Where(s => s.StartedAt < asOf);

		var subject = await started
			.Where(s => s.LeafWorkId == nodeId)
			.OrderByDescending(s => s.StartedAt).ThenByDescending(s => s.Id)
			.Take(maxSubjectSessionCount)
			.Select(s => new ConcurrentSessionRow(s.Id, s.LeafWorkId, s.WorkedByUserId, s.StartedAt, s.FinishedAt))
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);
		if (subject.Length == 0) {
			return new() { SubjectSessions = [], ConcurrentSessions = [], IsTruncated = false };
		}

		// The overlap join runs in the database rather than pulling a worker's whole history back:
		// same worker, different node, half-open intersection (spec §10.2.1 -- touching at a boundary
		// is not overlap), each side clipped to asOf inside the comparison.
		var concurrent = await started
			.Where(candidate => candidate.LeafWorkId != nodeId
								&& started.Any(s => s.LeafWorkId == nodeId
													&& s.WorkedByUserId == candidate.WorkedByUserId
													&& s.StartedAt < (candidate.FinishedAt == null || candidate.FinishedAt > asOf
														? asOf
														: candidate.FinishedAt)
													&& candidate.StartedAt < (s.FinishedAt == null || s.FinishedAt > asOf
														? asOf
														: s.FinishedAt)))
			.OrderByDescending(candidate => candidate.StartedAt).ThenByDescending(candidate => candidate.Id)
			.Take(maxConcurrentSessionCount)
			.Select(candidate => new ConcurrentSessionRow(
				candidate.Id, candidate.LeafWorkId, candidate.WorkedByUserId, candidate.StartedAt, candidate.FinishedAt))
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return new() {
			SubjectSessions = [.. subject.Select(row => row.ToSession(asOf))],
			ConcurrentSessions = [.. concurrent.Select(row => row.ToSession(asOf))],
			IsTruncated = subject.Length == maxSubjectSessionCount || concurrent.Length == maxConcurrentSessionCount,
		};
	}

	/// <summary>
	///     One session's raw columns as projected by the concurrency queries. The clip to <c>asOf</c> and
	///     the <see cref="WorkInterval" /> construction happen on materialized rows rather than in the
	///     expression tree, so the interval type's own invariant is enforced by its constructor instead
	///     of being pushed into SQL.
	/// </summary>
	private sealed record ConcurrentSessionRow(
		WorkSessionId Id, JobNodeId NodeId, AppUserId WorkedByUserId, Instant StartedAt, Instant? FinishedAt)
	{
		public ConcurrentWorkSession ToSession(Instant asOf) =>
			new(Id, NodeId, WorkedByUserId,
				new WorkInterval(StartedAt, FinishedAt is Instant finishedAt && finishedAt < asOf ? finishedAt : asOf));
	}

	private SqliteJobTrackDbContext CreateContext() => SqliteDbContextFactory.CreateContext(connectionString, _interceptors);

	private async Task<EquatableArray<EmployeeRole>> GetActorRolesAsync(
		SqliteJobTrackDbContext context, AppUserId actorId, CancellationToken cancellationToken)
	{
		var actorIdentityUser = await context.Set<IdentityUserEntity>().AsNoTracking()
									.FirstOrDefaultAsync(iu => iu.AppUserId == actorId, cancellationToken).ConfigureAwait(false)
								?? throw new EntityNotFoundException($"Actor {actorId} does not exist.");
		ActorAccountState.EnsureMayAct(actorIdentityUser, actorId, clock.GetCurrentInstant());

		var roles = await context.Set<IdentityUserRoleEntity>().AsNoTracking()
			.Where(ur => ur.IdentityUserId == actorIdentityUser.Id)
			.Select(ur => (EmployeeRole)ur.IdentityRoleId)
			.ToArrayAsync(cancellationToken).ConfigureAwait(false);

		return [.. roles];
	}
}
