namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application.Ports;
using Database;
using NodaTime;
using Npgsql;
using Shared.Ports;
using TestSupport;

public sealed class PostgreSqlEmployeeCommandPortTests()
	: EmployeeCommandPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	internal override IInstallationBootstrapPort CreateBootstrapPort(string connectionString) =>
		new PostgreSqlInstallationBootstrapPort(
			PostgreSqlRoleDataSource.CreateBuilder(connectionString, "jobtrack_credential_administration").Build(), SystemClock.Instance);

	internal override IEmployeeCommandPort CreateCommandPort(string connectionString) =>
		new EmployeeCommandPort(new PostgreSqlWriteOperations(
				PostgreSqlRoleDataSource.CreateBuilder(connectionString, "jobtrack_credential_administration").Build()),
			SystemClock.Instance);

	protected override object EncodeInstant(DateTimeOffset value) => value;
}
