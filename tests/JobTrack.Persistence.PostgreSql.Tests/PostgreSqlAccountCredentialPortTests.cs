namespace JobTrack.Persistence.PostgreSql.Tests;

using System.Data.Common;
using Application;
using Application.Ports;
using Database;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using Npgsql;
using TestSupport;

public sealed class PostgreSqlAccountCredentialPortTests()
	: AccountCredentialPortContractTestsBase(new PostgreSqlDatabaseFixture())
{
	protected override SchemaProvider Provider => SchemaProvider.PostgreSql;

	protected override DbConnection CreateConnection(string connectionString) => new NpgsqlConnection(connectionString);

	protected override ISchemaVersionStore CreateStore() => new PostgreSqlSchemaVersionStore();

	protected override IDeploymentLockStrategy CreateLockStrategy() => new PostgreSqlDeploymentLockStrategy();

	protected override Task PrepareConnectionAsync(DbConnection connection) => Task.CompletedTask;

	protected override object FormatInstantForRawSql(Instant instant) => instant.ToDateTimeOffset();

	internal override IAccountCredentialPort CreatePort(string connectionString, IClock clock) =>
		new PostgreSqlAccountCredentialPort(
			new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build(),
			clock,
			new PasswordHasher<EmployeeCredentialSubject>());
}
