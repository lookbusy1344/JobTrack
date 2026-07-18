namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-REQ-001/002 contract for <c>department</c>, <c>app_user_department</c>,
///     <c>request_holding_area</c>, and <c>job_request</c> (ADR 0033), asserted identically against
///     PostgreSQL and SQLite by <see cref="PostgreSqlJobRequestSchemaTests" /> and
///     <see cref="SqliteJobRequestSchemaTests" />.
/// </summary>
public abstract class JobRequestSchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly IDisposableTestDatabase database;

	protected JobRequestSchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_empty_department_holding_area_and_job_request_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection, "department")).Should().Be(0);
		(await CountRowsAsync(connection, "request_holding_area")).Should().Be(0);
		(await CountRowsAsync(connection, "job_request")).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_two_active_departments_with_the_same_name_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		await InsertDepartmentAsync(connection, "IT Support", true);

		var act = async () => await InsertDepartmentAsync(connection, "IT Support", true);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_an_active_department_reusing_the_name_of_an_inactive_department_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		await InsertDepartmentAsync(connection, "IT Support", false);

		var id = await InsertDepartmentAsync(connection, "IT Support", true);

		id.Should().BePositive();
	}

	[Fact]
	public async Task Inserting_a_holding_area_referencing_a_nonexistent_job_node_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await InsertHoldingAreaAsync(connection, -1, "IT Intake");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_job_request_anchored_to_an_ordinary_node_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);

		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);

		(await CountRowsAsync(connection, "job_request")).Should().Be(1);
	}

	[Fact]
	public async Task Inserting_a_job_request_referencing_a_nonexistent_requester_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var adminId = await SeedAppUserAsync(connection, "Alice Example");
		var rootId = await InsertNodeAsync(connection, adminId, null);
		var holdingNodeId = await InsertNodeAsync(connection, adminId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, adminId, holdingNodeId);

		var act = async () => await InsertJobRequestAsync(connection, requestNodeId, -1, holdingAreaId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_job_request_referencing_a_nonexistent_holding_area_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var requestNodeId = await InsertNodeAsync(connection, requesterId, rootId);

		var act = async () => await InsertJobRequestAsync(connection, requestNodeId, requesterId, -1);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Anchoring_a_job_request_to_the_permanent_root_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, rootId, "IT Intake");

		var act = async () => await InsertJobRequestAsync(connection, rootId, requesterId, holdingAreaId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Sequential_request_submissions_into_the_same_holding_area_produce_distinct_job_nodes_and_request_rows()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");

		var firstNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, firstNodeId, requesterId, holdingAreaId);
		var secondNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, secondNodeId, requesterId, holdingAreaId);

		firstNodeId.Should().NotBe(secondNodeId);
		(await CountRowsAsync(connection, "job_request")).Should().Be(2);
	}

	[Fact]
	public async Task Acknowledging_a_request_sets_acknowledged_at_and_actor()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var staffId = await SeedAppUserAsync(connection, "Sam Staff");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);

		await AcknowledgeJobRequestAsync(connection, requestNodeId, staffId);

		var (hasAcknowledgedAt, acknowledgedBy) = await ReadAcknowledgementAsync(connection, requestNodeId);
		hasAcknowledgedAt.Should().BeTrue();
		acknowledgedBy.Should().Be(staffId);
	}

	[Fact]
	public async Task A_new_job_request_has_no_acknowledgement_until_explicitly_set()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);

		var (hasAcknowledgedAt, acknowledgedBy) = await ReadAcknowledgementAsync(connection, requestNodeId);

		hasAcknowledgedAt.Should().BeFalse();
		acknowledgedBy.Should().BeNull();
	}

	[Fact]
	public async Task Reacknowledging_a_job_request_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var firstStaffId = await SeedAppUserAsync(connection, "Sam Staff");
		var secondStaffId = await SeedAppUserAsync(connection, "Alex Staff");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);
		await AcknowledgeJobRequestAsync(connection, requestNodeId, firstStaffId);

		var act = async () => await AcknowledgeJobRequestAsync(connection, requestNodeId, secondStaffId);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_non_acknowledgment_request_state_after_acknowledgment_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var staffId = await SeedAppUserAsync(connection, "Sam Staff");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);
		await AcknowledgeJobRequestAsync(connection, requestNodeId, staffId);

		await CloseRequestToRequesterAsync(connection, requestNodeId);

		(await HasClosedToRequesterAtAsync(connection, requestNodeId)).Should().BeTrue();
	}

	[Fact]
	public async Task Inserting_a_job_request_with_mismatched_acknowledgment_columns_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var staffId = await SeedAppUserAsync(connection, "Sam Staff");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);

		var act = async () => await InsertJobRequestWithAcknowledgementAsync(
			connection, requestNodeId, requesterId, holdingAreaId, staffId, false);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_job_request_note_for_an_existing_request_node_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);

		await InsertJobRequestNoteAsync(connection, requestNodeId, requesterId, "Any update?", true);

		(await CountRowsAsync(connection, "job_request_note")).Should().Be(1);
	}

	[Fact]
	public async Task Inserting_a_job_request_note_referencing_a_nonexistent_node_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");

		var act = async () =>
			await InsertJobRequestNoteAsync(connection, -1, requesterId, "Any update?", true);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_job_request_note_for_an_ordinary_job_node_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var ordinaryNodeId = await InsertNodeAsync(connection, requesterId, rootId);

		var act = async () =>
			await InsertJobRequestNoteAsync(connection, ordinaryNodeId, requesterId, "Any update?", true);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_job_request_note_with_blank_content_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);

		var act = async () => await InsertJobRequestNoteAsync(connection, requestNodeId, requesterId, "   ", true);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Updating_a_job_request_note_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);
		var noteId = await InsertJobRequestNoteAsync(connection, requestNodeId, requesterId, "Original", true);

		var act = async () => await UpdateJobRequestNoteContentAsync(connection, noteId, "Edited");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Deleting_a_job_request_note_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var requesterId = await SeedAppUserAsync(connection, "Rita Requester");
		var rootId = await InsertNodeAsync(connection, requesterId, null);
		var holdingNodeId = await InsertNodeAsync(connection, requesterId, rootId);
		var holdingAreaId = await InsertHoldingAreaAsync(connection, holdingNodeId, "IT Intake");
		var requestNodeId = await InsertNodeAsync(connection, requesterId, holdingNodeId);
		await InsertJobRequestAsync(connection, requestNodeId, requesterId, holdingAreaId);
		var noteId = await InsertJobRequestNoteAsync(connection, requestNodeId, requesterId, "Original", true);

		var act = async () => await DeleteJobRequestNoteAsync(connection, noteId);

		await act.Should().ThrowAsync<DbException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	/// <summary>SQLite needs <c>PRAGMA foreign_keys/busy_timeout</c> set per connection; PostgreSQL needs nothing.</summary>
	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	protected abstract object EncodeInstant(DateTimeOffset value);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = await OpenExistingConnectionAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private async Task<DbConnection> OpenExistingConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);
		return connection;
	}

	private static async Task<long> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO app_user (display_name, iana_time_zone)
							  VALUES (@displayName, 'Europe/London')
							  RETURNING id;
							  """;
		AddParameter(command, "@displayName", displayName);
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId, string description = "A job")
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, @postedAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", description);
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);
		AddParameter(command, "@postedAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task<long> InsertDepartmentAsync(DbConnection connection, string name, bool isActive)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO department (name, is_active)
							  VALUES (@name, @isActive)
							  RETURNING id;
							  """;
		AddParameter(command, "@name", name);
		AddParameter(command, "@isActive", EncodeBoolean(isActive));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long> InsertHoldingAreaAsync(DbConnection connection, long jobNodeId, string name)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO request_holding_area (job_node_id, name, default_priority_id)
							  VALUES (@jobNodeId, @name, @priorityId)
							  RETURNING id;
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@name", name);
		AddParameter(command, "@priorityId", PriorityMedium);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private async Task InsertJobRequestWithAcknowledgementAsync(
		DbConnection connection, long jobNodeId, long requesterUserId, long holdingAreaId, long? acknowledgedByUserId, bool hasAcknowledgedAt)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id, submitted_at, acknowledged_at, acknowledged_by_user_id)
							  VALUES (@jobNodeId, @requesterUserId, @holdingAreaId, @submittedAt, @acknowledgedAt, @acknowledgedByUserId);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@requesterUserId", requesterUserId);
		AddParameter(command, "@holdingAreaId", holdingAreaId);
		AddParameter(command, "@submittedAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@acknowledgedAt", hasAcknowledgedAt ? EncodeInstant(DateTimeOffset.UtcNow) : DBNull.Value);
		AddParameter(command, "@acknowledgedByUserId", (object?)acknowledgedByUserId ?? DBNull.Value);

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task InsertJobRequestAsync(DbConnection connection, long jobNodeId, long requesterUserId, long holdingAreaId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id, submitted_at)
							  VALUES (@jobNodeId, @requesterUserId, @holdingAreaId, @submittedAt);
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@requesterUserId", requesterUserId);
		AddParameter(command, "@holdingAreaId", holdingAreaId);
		AddParameter(command, "@submittedAt", EncodeInstant(DateTimeOffset.UtcNow));

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task AcknowledgeJobRequestAsync(DbConnection connection, long jobNodeId, long acknowledgedByUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE job_request
							  SET acknowledged_at = @acknowledgedAt, acknowledged_by_user_id = @acknowledgedByUserId
							  WHERE job_node_id = @jobNodeId;
							  """;
		AddParameter(command, "@acknowledgedAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@acknowledgedByUserId", acknowledgedByUserId);
		AddParameter(command, "@jobNodeId", jobNodeId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private async Task CloseRequestToRequesterAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  UPDATE job_request
							  SET closed_to_requester_at = @closedToRequesterAt
							  WHERE job_node_id = @jobNodeId;
							  """;
		AddParameter(command, "@closedToRequesterAt", EncodeInstant(DateTimeOffset.UtcNow));
		AddParameter(command, "@jobNodeId", jobNodeId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<bool> HasClosedToRequesterAtAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT closed_to_requester_at FROM job_request WHERE job_node_id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", jobNodeId);

		var closedToRequesterAt = await command.ExecuteScalarAsync();
		return closedToRequesterAt is not null && closedToRequesterAt != DBNull.Value;
	}

	private static async Task<(bool HasAcknowledgedAt, long? AcknowledgedBy)> ReadAcknowledgementAsync(
		DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT acknowledged_at, acknowledged_by_user_id FROM job_request WHERE job_node_id = @jobNodeId;";
		AddParameter(command, "@jobNodeId", jobNodeId);

		await using var reader = await command.ExecuteReaderAsync();
		_ = await reader.ReadAsync();
		var hasAcknowledgedAt = !reader.IsDBNull(0);
		var acknowledgedBy = reader.IsDBNull(1) ? (long?)null : Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
		return (hasAcknowledgedAt, acknowledgedBy);
	}

	private async Task<long> InsertJobRequestNoteAsync(
		DbConnection connection, long jobNodeId, long authorUserId, string content, bool isVisibleToRequester)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_request_note (job_node_id, author_user_id, content, is_visible_to_requester, created_at)
							  VALUES (@jobNodeId, @authorUserId, @content, @isVisibleToRequester, @createdAt)
							  RETURNING id;
							  """;
		AddParameter(command, "@jobNodeId", jobNodeId);
		AddParameter(command, "@authorUserId", authorUserId);
		AddParameter(command, "@content", content);
		AddParameter(command, "@isVisibleToRequester", EncodeBoolean(isVisibleToRequester));
		AddParameter(command, "@createdAt", EncodeInstant(DateTimeOffset.UtcNow));

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task UpdateJobRequestNoteContentAsync(DbConnection connection, long noteId, string content)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE job_request_note SET content = @content WHERE id = @id;";
		AddParameter(command, "@content", content);
		AddParameter(command, "@id", noteId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task DeleteJobRequestNoteAsync(DbConnection connection, long noteId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "DELETE FROM job_request_note WHERE id = @id;";
		AddParameter(command, "@id", noteId);

		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection, string tableName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	protected abstract object EncodeBoolean(bool value);

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
