namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using Persistence.PostgreSql;
using Persistence.Sqlite;

public sealed class PersistencePublicSurfaceTests
{
	[Theory]
	[InlineData(typeof(JobTrackPostgreSql))]
	[InlineData(typeof(JobTrackSqlite))]
	public void Persistence_assemblies_export_only_their_client_factory(Type factoryType)
	{
		var exportedTypes = factoryType.Assembly.GetExportedTypes();

		exportedTypes.Should().Equal(factoryType);
	}
}
