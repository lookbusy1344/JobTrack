namespace JobTrack.UatSeed;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using Application;
using Domain.Schedules;
using NodaTime;

/// <summary>
///     Development-only synthetic seed for end-user testing (remediation plan §2.3, ADR 0033/0034): a
///     requester, the six operational roles, a holding area, an unassigned request, assigned/acknowledged
///     work, a prerequisite blocker, an active session, cost-reportable work, and audit history. Runs
///     through <see cref="IJobTrackClient" /> throughout — the department and holding-area rows are raw
///     SQL only because no library command exists yet to configure them (deliberately deferred by the
///     client-requester-intake plan's "holding-area admin UI" follow-up, not an oversight here). Uses
///     only fake names, emails, and job content (CLAUDE.md "no real PII" rule).
/// </summary>
public static class UatSeeder
{
	/// <summary>
	///     The synthetic initial password for every seeded employee — forces a change at first
	///     sign-in (<see cref="IEmployeeCommands.CreateEmployeeAsync" />), so it is never a live credential.
	/// </summary>
	public const string KnownPassword = "Uat-Seed-Battery-42!";

	private const string IanaTimeZone = "Europe/London";
	private const short PriorityMedium = 2;

	/// <summary>
	///     Seeds the UAT scenario against an already-deployed, already-bootstrapped database (README
	///     "Running on a development server"). <paramref name="connection" /> is used only for the
	///     department/holding-area rows described above; every other write goes through
	///     <paramref name="client" />.
	/// </summary>
	public static async Task<UatSeedSummary> SeedAsync(
		IJobTrackClient client, DbConnection connection, AppUserId administratorId, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(client);
		ArgumentNullException.ThrowIfNull(connection);

		var adminContext = ContextFor(administratorId);
		var root = await client.Query.GetJobNodeAsync(new() { Context = adminContext, NodeId = null }, cancellationToken)
			.ConfigureAwait(false);

		var jobManagerId =
			await CreateEmployeeAsync(client, adminContext, "Priya Manager", "priya.manager", EmployeeRole.JobManager, cancellationToken)
				.ConfigureAwait(false);
		var workerId = await CreateEmployeeAsync(client, adminContext, "Wendy Worker", "wendy.worker", EmployeeRole.Worker, cancellationToken)
			.ConfigureAwait(false);
		var rateManagerId = await CreateEmployeeAsync(
			client, adminContext, "Raj RateManager", "raj.ratemanager", EmployeeRole.RateManager, cancellationToken).ConfigureAwait(false);
		_ = await CreateEmployeeAsync(client, adminContext, "Cara CostViewer", "cara.costviewer", EmployeeRole.CostViewer, cancellationToken)
			.ConfigureAwait(false);
		_ = await CreateEmployeeAsync(client, adminContext, "Aaron Auditor", "aaron.auditor", EmployeeRole.Auditor, cancellationToken)
			.ConfigureAwait(false);
		var requesterId =
			await CreateEmployeeAsync(client, adminContext, "Rita Requester", "rita.requester", EmployeeRole.Requester, cancellationToken)
				.ConfigureAwait(false);

		var jobManagerContext = ContextFor(jobManagerId);
		var workerContext = ContextFor(workerId);
		var requesterContext = ContextFor(requesterId);

		var departmentId = await SeedDepartmentAsync(connection, "Information Technology", cancellationToken).ConfigureAwait(false);
		await SeedAppUserDepartmentAsync(connection, requesterId, departmentId, cancellationToken).ConfigureAwait(false);

		var holdingAreaNode = await client.Jobs.AddChildAsync(new() {
			Context = jobManagerContext,
			ParentId = root.Node.Id,
			Description = "IT Helpdesk",
			OwnerUserId = jobManagerId,
			Priority = Priority.Medium,
		}, cancellationToken).ConfigureAwait(false);
		var holdingAreaId = await SeedHoldingAreaAsync(connection, holdingAreaNode.Id, departmentId, cancellationToken).ConfigureAwait(false);

		var unassignedRequest = await client.Requests
			.SubmitAsync(new() { Context = requesterContext, HoldingAreaId = holdingAreaId, Description = "Printer will not turn on" },
				cancellationToken).ConfigureAwait(false);

		var assignedRequest = await client.Requests
			.SubmitAsync(new() { Context = requesterContext, HoldingAreaId = holdingAreaId, Description = "New starter laptop setup" },
				cancellationToken).ConfigureAwait(false);
		_ = await client.Requests
			.AcknowledgeAsync(new() { Context = jobManagerContext, NodeId = assignedRequest.JobNodeId, Version = assignedRequest.Version },
				cancellationToken).ConfigureAwait(false);
		// Acknowledgement bumps job_request's own row version, not the job_node's — EditAsync below
		// still expects job_node's version, unchanged from the submission above.
		_ = await client.Jobs.EditAsync(new() {
			Context = jobManagerContext,
			NodeId = assignedRequest.JobNodeId,
			Description = assignedRequest.Description,
			OwnerUserId = workerId,
			Priority = Priority.Medium,
			Version = assignedRequest.Version,
		}, cancellationToken).ConfigureAwait(false);

		var poolLeaf = await client.Jobs.AddChildAsync(new() {
			Context = jobManagerContext,
			ParentId = root.Node.Id,
			Description = "Replace failing UPS battery",
			OwnerUserId = null,
			Priority = Priority.Low,
		}, cancellationToken).ConfigureAwait(false);

		var blockerLeaf = await AddAndAttachLeafAsync(
			client, jobManagerContext, root.Node.Id, "Order replacement toner cartridge", workerId, cancellationToken).ConfigureAwait(false);
		var blockedLeaf = await AddAndAttachLeafAsync(
			client, jobManagerContext, root.Node.Id, "Install new toner cartridge", workerId, cancellationToken).ConfigureAwait(false);
		await client.Jobs
			.AddPrerequisiteAsync(new() { Context = jobManagerContext, RequiredJobId = blockerLeaf.Id, DependentJobId = blockedLeaf.Id },
				cancellationToken).ConfigureAwait(false);

		var activeLeaf = await AddAndAttachLeafAsync(
			client, jobManagerContext, root.Node.Id, "Diagnose network outage", workerId, cancellationToken).ConfigureAwait(false);
		_ = await client.Work
			.StartSessionAsync(new() { Context = workerContext, LeafWorkId = activeLeaf.Id, WorkedByUserId = workerId }, cancellationToken)
			.ConfigureAwait(false);

		var costLeaf = await AddAndAttachLeafAsync(
			client, jobManagerContext, root.Node.Id, "Replace failed network switch", workerId, cancellationToken).ConfigureAwait(false);
		var now = SystemClock.Instance.GetCurrentInstant();
		var session = await client.Work.StartSessionAsync(new() {
			Context = workerContext,
			LeafWorkId = costLeaf.Id,
			WorkedByUserId = workerId,
			StartedAt = now - Duration.FromHours(3),
		}, cancellationToken).ConfigureAwait(false);
		_ = await client.Work.FinishSessionAsync(new() {
			Context = workerContext,
			SessionId = session.Id,
			Version = session.Version,
			FinishedAt = now - Duration.FromHours(1),
		}, cancellationToken).ConfigureAwait(false);
		var inProgress = await client.Work.SetAchievementAsync(new() {
			Context = workerContext,
			JobNodeId = costLeaf.Id,
			NewAchievement = Achievement.InProgress,
			Reason = "Diagnosed and replacement switch ordered.",
			Version = 1,
		}, cancellationToken).ConfigureAwait(false);
		_ = await client.Work.SetAchievementAsync(new() {
			Context = workerContext,
			JobNodeId = costLeaf.Id,
			NewAchievement = Achievement.Success,
			Reason = "Switch replaced and verified.",
			Version = inProgress.Version,
		}, cancellationToken).ConfigureAwait(false);
		_ = await client.Rates
			.AddUserCostRateAsync(
				new() { Context = ContextFor(rateManagerId), UserId = workerId, Rate = new(new(18.50m), now - Duration.FromDays(30), null) },
				cancellationToken).ConfigureAwait(false);
		// CreateEmployeeAsync already provisioned a default effective-dated schedule version for
		// workerId (EmployeeProvisioningDefaults.CreateSchedule); replacing it in place (ADR 0003)
		// avoids adding a second version that would overlap that open-ended default.
		var provisionedSchedule = await client.Query.GetScheduleAsync(new() { Context = workerContext, UserId = workerId }, cancellationToken)
			.ConfigureAwait(false);
		var defaultVersion = provisionedSchedule.Versions[0];
		_ = await client.Schedules.CorrectScheduleVersionAsync(new() {
			Context = workerContext,
			VersionId = defaultVersion.Id,
			UserId = workerId,
			Version = defaultVersion.Version,
			Reason = "UAT seed: wide-open schedule for cost-reportable demo work.",
			Schedule = WideOpenSchedule(now),
		}, cancellationToken).ConfigureAwait(false);

		return new() {
			JobManagerId = jobManagerId,
			WorkerId = workerId,
			RequesterId = requesterId,
			HoldingAreaId = holdingAreaId,
			UnassignedRequestNodeId = unassignedRequest.JobNodeId,
			AssignedRequestNodeId = assignedRequest.JobNodeId,
			PoolLeafNodeId = poolLeaf.Id,
			BlockedLeafNodeId = blockedLeaf.Id,
			ActiveSessionLeafNodeId = activeLeaf.Id,
			CostReportableLeafNodeId = costLeaf.Id,
		};
	}

	private static async Task<JobNodeResult> AddAndAttachLeafAsync(
		IJobTrackClient client, CommandContext context, JobNodeId parentId, string description, AppUserId ownerId,
		CancellationToken cancellationToken)
	{
		var leaf = await client.Jobs.AddChildAsync(new() {
			Context = context,
			ParentId = parentId,
			Description = description,
			OwnerUserId = ownerId,
			Priority = Priority.Medium,
		}, cancellationToken).ConfigureAwait(false);
		_ = await client.Jobs
			.AttachLeafWorkAsync(
				new() { Context = context, JobNodeId = leaf.Id, FullCriteria = "Done when the reported fault no longer reproduces." },
				cancellationToken).ConfigureAwait(false);

		return leaf;
	}

	/// <summary>
	///     Every day, 00:00-23:59:00, so the cost-reportable session's dynamically calculated
	///     eligible time (spec §10.1) is non-zero regardless of which day of the week the seed happens to
	///     run on — a realistic named working pattern is out of scope for a UAT fixture.
	/// </summary>
	private static ScheduleVersion WideOpenSchedule(Instant now)
	{
		var zone = DateTimeZoneProviders.Tzdb[IanaTimeZone];
		var effectiveStart = now.InZone(zone).Date.PlusDays(-60);
		EquatableArray<WeeklyInterval> weeklyIntervals = [
			.. Enum.GetValues<IsoDayOfWeek>()
				.Where(day => day != IsoDayOfWeek.None)
				.Select(day => new WeeklyInterval(day, new(0, 0, 0), new(23, 59, 0))),
		];

		return new(zone, effectiveStart, null, weeklyIntervals);
	}

	private static async Task<AppUserId> CreateEmployeeAsync(
		IJobTrackClient client, CommandContext adminContext, string displayName, string userName, EmployeeRole role,
		CancellationToken cancellationToken)
	{
		var result = await client.Employees.CreateEmployeeAsync(new() {
			Context = adminContext,
			DisplayName = displayName,
			IanaTimeZone = IanaTimeZone,
			UserName = userName,
			Password = KnownPassword,
			Role = role,
		}, cancellationToken).ConfigureAwait(false);

		return result.Id;
	}

	private static CommandContext ContextFor(AppUserId actor) => new() { Actor = actor, CorrelationId = Guid.NewGuid() };

	private static async Task<DepartmentId> SeedDepartmentAsync(DbConnection connection, string name, CancellationToken cancellationToken)
	{
		var isSqlite = IsSqlite(connection);
		await using var command = connection.CreateCommand();
		command.CommandText = isSqlite
			? "INSERT INTO department (name, is_active) VALUES (@name, 1); SELECT last_insert_rowid();"
			: "INSERT INTO department (name, is_active) VALUES (@name, true) RETURNING id;";
		AddParameter(command, "@name", name);
		return new(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture));
	}

	private static async Task SeedAppUserDepartmentAsync(
		DbConnection connection, AppUserId appUserId, DepartmentId departmentId, CancellationToken cancellationToken)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO app_user_department (app_user_id, department_id) VALUES (@appUserId, @departmentId);";
		AddParameter(command, "@appUserId", appUserId.Value);
		AddParameter(command, "@departmentId", departmentId.Value);
		_ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
	}

	private static async Task<RequestHoldingAreaId> SeedHoldingAreaAsync(
		DbConnection connection, JobNodeId jobNodeId, DepartmentId departmentId, CancellationToken cancellationToken)
	{
		var isSqlite = IsSqlite(connection);
		await using var command = connection.CreateCommand();
		command.CommandText = isSqlite
			? """
			  INSERT INTO request_holding_area
			  (job_node_id, department_id, name, default_priority_id, default_owner_user_id, is_active)
			  VALUES
			  (@jobNodeId, @departmentId, 'IT Helpdesk Intake', @priorityId, NULL, 1);
			  SELECT last_insert_rowid();
			  """
			: """
			  INSERT INTO request_holding_area
			  (job_node_id, department_id, name, default_priority_id, default_owner_user_id, is_active)
			  VALUES
			  (@jobNodeId, @departmentId, 'IT Helpdesk Intake', @priorityId, NULL, true)
			  RETURNING id;
			  """;
		AddParameter(command, "@jobNodeId", jobNodeId.Value);
		AddParameter(command, "@departmentId", departmentId.Value);
		AddParameter(command, "@priorityId", PriorityMedium);
		return new(
			Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture));
	}

	private static bool IsSqlite(DbConnection connection) => connection.GetType().Name.Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		_ = command.Parameters.Add(parameter);
	}
}
