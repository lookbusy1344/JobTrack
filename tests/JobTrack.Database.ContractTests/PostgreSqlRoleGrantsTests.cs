namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using AwesomeAssertions;
using Npgsql;
using TestSupport;

/// <summary>
///     TC-DB-ROLES-001: PostgreSQL-only contract for the roles-and-grants
///     script (impl plan §6.1, §6.7 gate item "role grants prove the normal
///     application role cannot perform DDL, erase audit rows, or delete
///     retained history") plus the jobtrack_domain/jobtrack_identity split and
///     personal_access_token SECURITY DEFINER function boundary (security
///     review remediation §2.6). No SQLite equivalent -- SQLite has no roles
///     or GRANT concept. Function-behavior contract tests (issue/authenticate/
///     list/revoke round trips) live in <c>PostgreSqlSecurityDefinerFunctionsTests</c>;
///     this file covers the negative "no direct table access" half of the
///     boundary.
///     Every negative assertion is exercised via <c>SET ROLE</c> on the same
///     admin connection used to deploy the schema, rather than a separate
///     authenticated connection per role: the local/CI admin account is a
///     superuser, which may <c>SET ROLE</c> to any role without needing prior
///     membership or a password, so this needs no pg_hba.conf changes or
///     environment-specific login credentials to prove the grants hold.
/// </summary>
public sealed class PostgreSqlRoleGrantsTests : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const short PriorityMedium = 2;

	private readonly PostgreSqlDatabaseFixture database = new();

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task The_domain_role_cannot_create_a_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "CREATE TABLE rogue_table (id integer);");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_domain_role_cannot_alter_an_existing_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "ALTER TABLE app_user ADD COLUMN rogue_column text;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_domain_role_cannot_delete_audit_event_rows()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertAuditEventAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "DELETE FROM audit_event;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		(await CountRowsAsync(connection, "audit_event")).Should().Be(1);
	}

	[Fact]
	public async Task The_domain_role_cannot_update_audit_event_rows()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertAuditEventAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "UPDATE audit_event SET reason = 'tampered';");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_domain_role_cannot_delete_retained_work_session_history()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		await InsertWorkSessionAsync(connection, leafWorkId, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "DELETE FROM work_session;");

		await act.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		(await CountRowsAsync(connection, "work_session")).Should().Be(1);
	}

	[Fact]
	public async Task The_domain_role_can_still_read_and_write_ordinary_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_domain",
			"INSERT INTO app_user (display_name, iana_time_zone) VALUES ('Bob Example', 'Europe/London');");

		await act.Should().NotThrowAsync();
	}

	/// <summary>
	///     Every application table must be reachable by the role the application actually runs as.
	///     The named-table tests above only prove the tables someone remembered to name: schema version
	///     0020 added five tables (department, app_user_department, job_request, job_request_note,
	///     request_holding_area) and the grants script was never updated, so on PostgreSQL every page
	///     touching a job request failed with "permission denied for table job_request" (SqlState 42501)
	///     while SQLite -- which has no roles at all -- stayed perfectly healthy. That asymmetry is why
	///     this is enumerated from the live catalog rather than a hand-kept list: a table added tomorrow
	///     is covered without anyone remembering to extend this test.
	///     Deliberately asserts SELECT only. Which tables the domain role may write is a per-table
	///     decision the surrounding tests pin down (it must NOT write personal_access_token, and must
	///     not delete audit_event); what no table may be is invisible.
	/// </summary>
	[Fact]
	public async Task The_domain_role_can_read_every_application_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var unreadable = new List<string>();
		await using (var command = connection.CreateCommand()) {
			// The catalog is the deployed truth, rather than re-parsing the schema-version scripts.
			// schema_version is deployment-tool bookkeeping the application never reads, so it is
			// excluded rather than granted.
			//
			// has_table_privilege is given the OID, not the name: PostgreSQL does not guarantee the
			// order in which WHERE predicates are evaluated, so the name form can be called on a row
			// the schema filter was going to reject and throw 42P01 for an information_schema table
			// that is not on the search_path. An OID from the catalog always resolves.
			//
			// personal_access_token is the one deliberate exception, not an oversight: the domain role
			// has no direct grant on it at all so a compromised domain credential cannot read
			// token_hash for offline replay (security review remediation §2.6). Its whole lifecycle
			// goes through SECURITY DEFINER functions, and
			// The_domain_role_has_no_direct_access_to_personal_access_token below pins that. Excluded
			// here by name so the two tests cannot silently contradict each other.
			// data_protection_key (schema version 0021, ADR 0066 Stage 2) is the second deliberate
			// exception: it is JobTrackIdentityDbContext's own table, read/written only by ASP.NET
			// Core Data Protection's EF Core repository -- no domain code path ever touches it, so
			// jobtrack_domain has no grant on it at all. The_identity_role_can_read_and_write_
			// data_protection_key and The_domain_role_has_no_access_to_data_protection_key below pin
			// both halves of that boundary. rate_limit_window and rate_limit_capacity_lock (schema
			// versions 0022-0023, ADR 0066 Stage 5) are the remaining exceptions: reached only through
			// the SECURITY DEFINER rate_limit_try_consume function, never a direct table grant to any
			// role. The explicit rate-limit privilege tests below pin that boundary.
			command.CommandText =
				"SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
				"WHERE n.nspname = 'public' AND c.relkind = 'r' " +
				"AND c.relname NOT IN " +
				"('schema_version', 'identity_user', 'personal_access_token', 'data_protection_key', 'rate_limit_window', 'rate_limit_capacity_lock') " +
				"AND NOT has_table_privilege('jobtrack_domain', c.oid, 'SELECT') " +
				"ORDER BY c.relname;";
			await using var reader = await command.ExecuteReaderAsync();
			while (await reader.ReadAsync()) {
				unreadable.Add(reader.GetString(0));
			}
		}

		unreadable.Should().BeEmpty(
			"every application table needs a grant to jobtrack_domain; these have none in " +
			"database/postgresql/roles/jobtrack-roles-and-grants.sql");
	}

	[Fact]
	public async Task Only_the_history_deletion_role_can_force_delete_work_sessions()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, leafWorkId) = await SeedUserAndLeafWorkAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);
		await InsertWorkSessionAsync(connection, leafWorkId, userId);
		await using (var grantAdministrator = connection.CreateCommand()) {
			grantAdministrator.CommandText =
				$"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1);";
			_ = await grantAdministrator.ExecuteNonQueryAsync();
		}

		var directDeleteAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "DELETE FROM work_session;");
		var domainFunctionAct = async () =>
			await ExecuteAsRoleAsync(
				connection, "jobtrack_domain",
				$"SELECT delete_worked_leaf_history({leafWorkId}, 1, {userId}, now(), gen_random_uuid(), 'duplicate', '{{}}'::jsonb);");
		var historyDeletionFunctionAct = async () =>
			await ExecuteAsRoleAsync(
				connection, "jobtrack_history_deletion",
				$"SELECT delete_worked_leaf_history({leafWorkId}, 1, {userId}, now(), gen_random_uuid(), 'duplicate', '{{}}'::jsonb);");

		await directDeleteAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await domainFunctionAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await historyDeletionFunctionAct.Should().NotThrowAsync();
		(await CountRowsAsync(connection, "work_session")).Should().Be(0);
	}

	[Fact]
	public async Task Security_definer_authority_matches_the_reviewed_capability_matrix()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		await using var command = connection.CreateCommand();
		command.CommandText =
			"WITH runtime_role(role_name) AS (VALUES " +
			"('jobtrack_domain'), ('jobtrack_history_deletion'), ('jobtrack_credential_administration'), ('jobtrack_identity'), " +
			"('jobtrack_pat_management'), ('jobtrack_pat_authentication'), ('jobtrack_readonly'), " +
			"('jobtrack_emergency_reset')) " +
			"SELECT role_name || ':' || p.proname || '(' || pg_get_function_identity_arguments(p.oid) || ')' " +
			"FROM runtime_role CROSS JOIN pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
			"WHERE n.nspname = 'public' AND p.prosecdef " +
			"AND has_function_privilege(role_name, p.oid, 'EXECUTE') ORDER BY 1;";

		var actual = new List<string>();
		await using var reader = await command.ExecuteReaderAsync();
		while (await reader.ReadAsync()) {
			actual.Add(reader.GetString(0));
		}

		actual.Should().Equal(
			"jobtrack_credential_administration:pat_revoke_all(p_app_user_id bigint, p_now timestamp with time zone)",
			"jobtrack_domain:pat_revoke_all(p_app_user_id bigint, p_now timestamp with time zone)",
			"jobtrack_history_deletion:delete_subtree_history(p_root_id bigint, p_expected_version bigint, p_actor_user_id bigint, p_occurred_at timestamp with time zone, p_correlation_id uuid, p_reason text, p_before_data jsonb)",
			"jobtrack_history_deletion:delete_worked_leaf_history(p_node_id bigint, p_expected_version bigint, p_actor_user_id bigint, p_occurred_at timestamp with time zone, p_correlation_id uuid, p_reason text, p_before_data jsonb)",
			"jobtrack_history_deletion:pat_revoke_all(p_app_user_id bigint, p_now timestamp with time zone)",
			"jobtrack_identity:rate_limit_live_partition_count()",
			"jobtrack_identity:rate_limit_try_consume(p_purpose text, p_partition_digest bytea, p_backstop_digest bytea, p_now timestamp with time zone, p_window_seconds integer, p_permit_limit integer, p_backstop_permit_limit integer, OUT out_allowed boolean, OUT out_rows_pruned integer)",
			"jobtrack_identity:rate_limit_try_consume(p_purpose text, p_partition_digest bytea, p_backstop_digest bytea, p_now timestamp with time zone, p_window_seconds integer, p_permit_limit integer, p_backstop_permit_limit integer, p_max_partition_count integer, OUT out_allowed boolean, OUT out_rows_pruned integer)",
			"jobtrack_pat_authentication:pat_try_authenticate(p_token_hash text, p_now timestamp with time zone)",
			"jobtrack_pat_management:pat_issue(p_app_user_id bigint, p_token_hash text, p_label text, p_created_at timestamp with time zone, p_expires_at timestamp with time zone)",
			"jobtrack_pat_management:pat_list(p_app_user_id bigint)",
			"jobtrack_pat_management:pat_revoke(p_token_id bigint, p_app_user_id bigint, p_now timestamp with time zone)",
			"jobtrack_pat_management:pat_revoke_all(p_app_user_id bigint, p_now timestamp with time zone)");
	}

	[Fact]
	public async Task The_domain_role_can_delete_a_job_request()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var jobNodeId = await SeedJobRequestAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", $"DELETE FROM job_request WHERE job_node_id = {jobNodeId};");

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_domain_role_has_no_direct_access_to_personal_access_token()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var tokenId = await InsertPersonalAccessTokenAsync(connection, userId);

		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT token_hash FROM personal_access_token;");
		var insertAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_domain",
			"INSERT INTO personal_access_token (app_user_id, token_hash, label, expires_at) " +
			$"VALUES ({userId}, 'rogue-hash', 'rogue', now() + interval '1 day');");
		var updateAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", $"UPDATE personal_access_token SET last_used_at = now() WHERE id = {tokenId};");

		await selectAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await updateAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_domain_role_cannot_issue_authenticate_or_list_personal_access_tokens()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var issueAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_domain",
			$"SELECT pat_issue({userId}, 'a-hash', 'a-label', now(), now() + interval '1 day');");
		var listAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", $"SELECT * FROM pat_list({userId});");
		var authenticateAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", "SELECT * FROM pat_try_authenticate('a-hash', now());");

		await issueAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await listAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await authenticateAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_personal_access_token_roles_have_disjoint_function_authority()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");

		var readonlyAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT * FROM pat_list(1);");
		var emergencyResetAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_emergency_reset", "SELECT * FROM pat_list(1);");
		var identityAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_identity", "SELECT * FROM pat_list(1);");
		var managementIssueAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_management", $"SELECT pat_issue({userId}, 'managed-hash', 'managed', now(), now() + interval '1 day');");
		var managementListAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_pat_management", $"SELECT * FROM pat_list({userId});");
		var managementAuthenticateAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_management", "SELECT * FROM pat_try_authenticate('managed-hash', now());");
		var managementAuditAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_pat_management",
			$"INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id) " +
			$"VALUES ({userId}, 'issue-personal-access-token', 'personal_access_token', 1, gen_random_uuid());");
		var authenticationIssueAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_authentication", $"SELECT pat_issue({userId}, 'rogue-hash', 'rogue', now(), now() + interval '1 day');");
		var authenticationAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_authentication", "SELECT * FROM pat_try_authenticate('managed-hash', now());");

		await readonlyAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await emergencyResetAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await identityAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await managementIssueAct.Should().NotThrowAsync();
		await managementListAct.Should().NotThrowAsync();
		await managementAuditAct.Should().NotThrowAsync();
		await managementAuthenticateAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await authenticationIssueAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await authenticationAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_identity_role_can_manage_identity_user_but_not_domain_tables()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);

		var updateIdentityAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_identity", $"UPDATE identity_user SET security_stamp = 'reset' WHERE id = {identityUserId};");
		var assignRoleAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_identity", $"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1);");
		var insertJobNodeAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			"INSERT INTO job_node (description, posted_by_user_id, owner_user_id, priority_id, posted_at) " +
			$"VALUES ('Rogue', {userId}, {userId}, {PriorityMedium}, now());");
		var selectPatAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_identity", "SELECT * FROM personal_access_token;");
		var insertAuditAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			$"INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id) " +
			$"VALUES ({userId}, 'Rogue', 'app_user', {userId}, gen_random_uuid());");

		await updateIdentityAct.Should().NotThrowAsync();
		await assignRoleAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertJobNodeAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await selectPatAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertAuditAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_identity_role_can_read_and_write_data_protection_key()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var insertAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_identity", "INSERT INTO data_protection_key (friendly_name, xml) VALUES ('k', '<key/>');");
		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_identity", "SELECT * FROM data_protection_key;");

		await insertAct.Should().NotThrowAsync();
		await selectAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_domain_role_has_no_access_to_data_protection_key()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT * FROM data_protection_key;");
		var insertAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", "INSERT INTO data_protection_key (friendly_name, xml) VALUES ('k', '<key/>');");

		await selectAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_identity_role_can_execute_rate_limit_try_consume_but_has_no_direct_table_access()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var executeAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			"SELECT out_allowed FROM rate_limit_try_consume('api', '\\x01'::bytea, NULL, now(), 60, 5, 0);");
		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_identity", "SELECT * FROM rate_limit_window;");
		var insertAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			"INSERT INTO rate_limit_window (purpose, partition_digest, window_start) VALUES ('login', '\\x02'::bytea, now());");

		await executeAct.Should().NotThrowAsync();
		await selectAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_identity_role_cannot_create_an_unknown_rate_limit_purpose()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			"SELECT out_allowed FROM rate_limit_try_consume('rogue', '\\x01'::bytea, NULL, now(), 60, 5, 0, 4096);");

		await act.Should().ThrowAsync<PostgresException>();
		(await CountRowsAsync(connection, "rate_limit_capacity_lock")).Should().Be(0);
		(await CountRowsAsync(connection, "rate_limit_window")).Should().Be(0);
	}

	[Theory]
	[InlineData("0, 5, 0, 4096")]
	[InlineData("60, 0, 0, 4096")]
	[InlineData("60, 5, 0, 65537")]
	public async Task The_identity_role_cannot_supply_rate_limit_policy_outside_database_safety_bounds(string policyArguments)
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var act = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_identity",
			$"SELECT out_allowed FROM rate_limit_try_consume('api', '\\x01'::bytea, NULL, now(), {policyArguments});");

		await act.Should().ThrowAsync<PostgresException>();
		(await CountRowsAsync(connection, "rate_limit_capacity_lock")).Should().Be(0);
		(await CountRowsAsync(connection, "rate_limit_window")).Should().Be(0);
	}

	[Fact]
	public async Task The_domain_role_has_no_access_to_rate_limit_window()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		var executeAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", "SELECT out_allowed FROM rate_limit_try_consume('api', '\\x01'::bytea, NULL, now(), 60, 5, 0);");
		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT * FROM rate_limit_window;");

		await executeAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await selectAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task Application_roles_have_no_direct_access_to_rate_limit_capacity_lock()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		foreach (var role in new[] {
					 "jobtrack_domain", "jobtrack_identity",
				 }) {
			var selectAct = async () => await ExecuteAsRoleAsync(connection, role, "SELECT * FROM rate_limit_capacity_lock;");
			var insertAct = async () => await ExecuteAsRoleAsync(
				connection, role, "INSERT INTO rate_limit_capacity_lock (purpose) VALUES ('rogue');");

			await selectAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
			await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		}
	}

	[Fact]
	public async Task The_readonly_role_can_select_but_cannot_insert()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		await SeedAppUserAsync(connection, "Alice Example");

		var selectAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT COUNT(*) FROM app_user;");
		var insertAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "INSERT INTO app_user (display_name, iana_time_zone) VALUES ('Carol Example', 'Europe/London');");

		await selectAct.Should().NotThrowAsync();
		await insertAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_identity_secret_columns()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, userId);

		var passwordHashAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT password_hash FROM identity_user;");
		var securityStampAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT security_stamp FROM identity_user;");
		var concurrencyStampAct = async () =>
			await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT concurrency_stamp FROM identity_user;");
		var userNameAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT user_name FROM identity_user;");

		await passwordHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await securityStampAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await concurrencyStampAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await userNameAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_domain_role_cannot_administer_credentials_roles_or_their_audit_trail()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);

		var readPasswordHashAct = async () =>
			await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT password_hash FROM identity_user;");
		var readSecurityStampAct = async () =>
			await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT security_stamp FROM identity_user;");
		var readAuthenticatorKeyAct = async () =>
			await ExecuteAsRoleAsync(connection, "jobtrack_domain", "SELECT authenticator_key_protected FROM identity_user;");
		var insertIdentityAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain",
			$"INSERT INTO identity_user (app_user_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp) " +
			$"VALUES ({userId}, 'rogue', 'ROGUE', 'hash', 'stamp', 'concurrency');");
		var updateIdentityAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", $"UPDATE identity_user SET password_hash = 'chosen' WHERE id = {identityUserId};");
		var assignRoleAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain",
			$"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1);");
		var revokeRoleAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain", $"DELETE FROM identity_user_role WHERE identity_user_id = {identityUserId};");
		var fabricateAuditAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_domain",
			$"INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id) " +
			$"VALUES ({userId}, 'reset-employee-password', 'identity_user', {identityUserId}, gen_random_uuid());");

		await readPasswordHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await readSecurityStampAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await readAuthenticatorKeyAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await insertIdentityAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await updateIdentityAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await assignRoleAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await revokeRoleAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await fabricateAuditAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_credential_administration_role_can_mutate_credentials_roles_and_their_audit_trail()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);

		var act = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_credential_administration",
			$"UPDATE identity_user SET password_hash = 'replacement' WHERE id = {identityUserId}; " +
			$"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1); " +
			$"INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id) " +
			$"VALUES ({userId}, 'reset-employee-password', 'identity_user', {identityUserId}, gen_random_uuid());");

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_personal_access_token_management_role_cannot_select_identity_secrets()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, userId);

		var passwordHashAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_management", "SELECT password_hash FROM identity_user;");
		var accountStateAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_pat_management", "SELECT id, app_user_id, is_enabled, lockout_enabled, lockout_end FROM identity_user;");

		await passwordHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await accountStateAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_the_totp_key_but_can_read_the_two_factor_enabled_flag()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertIdentityUserAsync(connection, userId);

		var authenticatorKeyAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT authenticator_key_protected FROM identity_user;");
		var twoFactorEnabledAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT two_factor_enabled, two_factor_enabled_at FROM identity_user;");

		await authenticatorKeyAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await twoFactorEnabledAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_readonly_role_cannot_select_the_personal_access_token_hash_but_can_read_reporting_columns()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		await InsertPersonalAccessTokenAsync(connection, userId);

		var tokenHashAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT token_hash FROM personal_access_token;");
		var labelAct = async () => await ExecuteAsRoleAsync(connection, "jobtrack_readonly", "SELECT label FROM personal_access_token;");
		var expiryAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_readonly", "SELECT created_at, expires_at, revoked_at, last_used_at FROM personal_access_token;");

		await tokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await labelAct.Should().NotThrowAsync();
		await expiryAct.Should().NotThrowAsync();
	}

	[Fact]
	public async Task The_emergency_reset_role_can_revoke_a_token_but_cannot_issue_one_or_assign_roles()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);
		var tokenId = await InsertPersonalAccessTokenAsync(connection, userId);

		var revokeAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET revoked_at = now() WHERE id = {tokenId};");
		var issueAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			"INSERT INTO personal_access_token (app_user_id, token_hash, label, expires_at) " +
			$"VALUES ({userId}, 'rogue-hash', 'rogue', now() + interval '1 day');");
		var assignRoleAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			$"INSERT INTO identity_user_role (identity_user_id, identity_role_id) VALUES ({identityUserId}, 1);");
		var selectTokenHashAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", "SELECT token_hash FROM personal_access_token;");
		var changeTokenHashAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET token_hash = 'known-rogue-hash' WHERE id = {tokenId};");
		var reassignTokenAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE personal_access_token SET app_user_id = {userId} WHERE id = {tokenId};");
		var extendTokenAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset",
			$"UPDATE personal_access_token SET expires_at = now() + interval '30 days' WHERE id = {tokenId};");

		await revokeAct.Should().NotThrowAsync();
		await issueAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await assignRoleAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await selectTokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await changeTokenHashAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await reassignTokenAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
		await extendTokenAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	[Fact]
	public async Task The_emergency_reset_role_can_update_identity_user_but_not_job_node()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var (userId, _) = await SeedAppUserAsync(connection, "Alice Example");
		var identityUserId = await InsertIdentityUserAsync(connection, userId);

		var updateIdentityAct = async () => await ExecuteAsRoleAsync(
			connection, "jobtrack_emergency_reset", $"UPDATE identity_user SET security_stamp = 'reset' WHERE id = {identityUserId};");
		var insertJobNodeAct = async () => await ExecuteAsRoleAsync(
			connection,
			"jobtrack_emergency_reset",
			$"INSERT INTO job_node (description, posted_by_user_id, owner_user_id, priority_id, posted_at) " +
			$"VALUES ('Rogue', {userId}, {userId}, {PriorityMedium}, now());");

		await updateIdentityAct.Should().NotThrowAsync();
		await insertJobNodeAct.Should().ThrowAsync<PostgresException>().Where(ex => ex.SqlState == "42501");
	}

	private async Task<NpgsqlConnection> OpenDeployedConnectionAsync()
	{
		var connection = new NpgsqlConnection(database.ConnectionString);
		await connection.OpenAsync();

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.PostgreSql));
		var deployer = new SchemaDeployer(
			connection, new PostgreSqlSchemaVersionStore(), new PostgreSqlDeploymentLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlRolesAndGrantsScriptPath(), CancellationToken.None);
		await PostgreSqlRolesAndGrants.ApplyAsync(connection, RepositoryPaths.PostgreSqlFunctionsScriptPath(), CancellationToken.None);

		return connection;
	}

	private static async Task ExecuteAsRoleAsync(NpgsqlConnection connection, string role, string commandText)
	{
		await using var setRole = connection.CreateCommand();
		setRole.CommandText = $"SET ROLE {role};";
		_ = await setRole.ExecuteNonQueryAsync();

		try {
			await using var command = connection.CreateCommand();
			command.CommandText = commandText;
			_ = await command.ExecuteNonQueryAsync();
		}
		finally {
			await using var resetRole = connection.CreateCommand();
			resetRole.CommandText = "RESET ROLE;";
			_ = await resetRole.ExecuteNonQueryAsync();
		}
	}

	private static async Task<(long AppUserId, long IdentityUserId)> SeedAppUserAsync(DbConnection connection, string displayName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", displayName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		return (appUserId, 0);
	}

	private static async Task<long> InsertIdentityUserAsync(DbConnection connection, long appUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO identity_user
							  (app_user_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp)
							  VALUES (@appUserId, @userName, @normalizedUserName, 'hash', 'stamp', 'concurrency')
							  RETURNING id;
							  """;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@userName", $"user{appUserId}");
		AddParameter(command, "@normalizedUserName", $"USER{appUserId}");

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<long> InsertPersonalAccessTokenAsync(DbConnection connection, long appUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO personal_access_token (app_user_id, token_hash, label, expires_at)
							  VALUES (@appUserId, @tokenHash, 'test-token', now() + interval '1 day')
							  RETURNING id;
							  """;
		AddParameter(command, "@appUserId", appUserId);
		AddParameter(command, "@tokenHash", $"hash-{appUserId}-{Guid.NewGuid():N}");

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task<(long UserId, long LeafWorkId)> SeedUserAndLeafWorkAsync(DbConnection connection, string displayName)
	{
		var (userId, _) = await SeedAppUserAsync(connection, displayName);
		var rootId = await InsertNodeAsync(connection, userId, null);
		var leafId = await InsertNodeAsync(connection, userId, rootId);
		await InsertLeafWorkAsync(connection, leafId);
		return (userId, leafId);
	}

	private static async Task<long> InsertNodeAsync(DbConnection connection, long ownerUserId, long? parentId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO job_node
							  (parent_id, description, posted_by_user_id, owner_user_id, priority_id, posted_at)
							  VALUES
							  (@parentId, @description, @ownerUserId, @ownerUserId, @priorityId, now())
							  RETURNING id;
							  """;
		AddParameter(command, "@parentId", (object?)parentId ?? DBNull.Value);
		AddParameter(command, "@description", "A job");
		AddParameter(command, "@ownerUserId", ownerUserId);
		AddParameter(command, "@priorityId", PriorityMedium);

		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task InsertLeafWorkAsync(DbConnection connection, long jobNodeId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "INSERT INTO leaf_work (job_node_id, changed_at) VALUES (@jobNodeId, now());";
		AddParameter(command, "@jobNodeId", jobNodeId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task InsertWorkSessionAsync(DbConnection connection, long leafWorkId, long workedByUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO work_session (leaf_work_id, worked_by_user_id, started_at, finished_at, changed_at)
							  VALUES (@leafWorkId, @workedByUserId, now() - interval '1 hour', now(), now());
							  """;
		AddParameter(command, "@leafWorkId", leafWorkId);
		AddParameter(command, "@workedByUserId", workedByUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> SeedJobRequestAsync(DbConnection connection, long requesterUserId)
	{
		var rootId = await InsertNodeAsync(connection, requesterUserId, null);
		var requestNodeId = await InsertNodeAsync(connection, requesterUserId, rootId);

		await using var holdingAreaCommand = connection.CreateCommand();
		holdingAreaCommand.CommandText = """
										 INSERT INTO request_holding_area (job_node_id, name, default_priority_id)
										 VALUES (@jobNodeId, 'Intake', @priorityId)
										 RETURNING id;
										 """;
		AddParameter(holdingAreaCommand, "@jobNodeId", rootId);
		AddParameter(holdingAreaCommand, "@priorityId", PriorityMedium);
		var holdingAreaId = Convert.ToInt64(await holdingAreaCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var jobRequestCommand = connection.CreateCommand();
		jobRequestCommand.CommandText = """
										INSERT INTO job_request (job_node_id, requester_user_id, holding_area_id)
										VALUES (@jobNodeId, @requesterUserId, @holdingAreaId);
										""";
		AddParameter(jobRequestCommand, "@jobNodeId", requestNodeId);
		AddParameter(jobRequestCommand, "@requesterUserId", requesterUserId);
		AddParameter(jobRequestCommand, "@holdingAreaId", holdingAreaId);
		_ = await jobRequestCommand.ExecuteNonQueryAsync();

		return requestNodeId;
	}

	private static async Task InsertAuditEventAsync(DbConnection connection, long actorUserId)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = """
							  INSERT INTO audit_event (actor_user_id, operation, entity_type, entity_id, correlation_id)
							  VALUES (@actorUserId, 'Test', 'app_user', @actorUserId, gen_random_uuid());
							  """;
		AddParameter(command, "@actorUserId", actorUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection, string tableName)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = $"SELECT COUNT(*) FROM {tableName};";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
