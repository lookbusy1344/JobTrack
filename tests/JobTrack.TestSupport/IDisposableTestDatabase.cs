namespace JobTrack.TestSupport;

/// <summary>
///     Common shape of <see cref="PostgreSqlDatabaseFixture" /> and
///     <see cref="SqliteDatabaseFixture" />, so provider-agnostic test bases can
///     hold either behind one abstraction.
/// </summary>
public interface IDisposableTestDatabase : ITestDatabaseLifetime
{
	string ConnectionString { get; }
}
