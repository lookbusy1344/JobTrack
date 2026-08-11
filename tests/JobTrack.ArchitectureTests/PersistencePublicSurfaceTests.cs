namespace JobTrack.ArchitectureTests;

using System.Reflection;
using AwesomeAssertions;
using Persistence.PostgreSql;
using Persistence.Sqlite;

public sealed class PersistencePublicSurfaceTests
{
	private const string SharedPersistenceAssemblyName = "JobTrack.Persistence.Shared";

	[Theory]
	[InlineData(typeof(JobTrackPostgreSql))]
	[InlineData(typeof(JobTrackSqlite))]
	public void Persistence_assemblies_export_only_their_client_factory(Type factoryType)
	{
		var exportedTypes = factoryType.Assembly.GetExportedTypes();

		exportedTypes.Should().Equal(factoryType);
	}

	/// <summary>
	///     JobTrack.Persistence.Shared is packaged, so anything public in it ships. It is reached only
	///     through <c>InternalsVisibleTo</c> from the two providers, and its csproj Description states
	///     that every type is internal — this is what holds that claim true. Its absence from the theory
	///     above is why four static classes and four row records were public without anyone noticing.
	/// </summary>
	[Fact]
	public void The_shared_persistence_assembly_exports_nothing()
	{
		// Loaded by name rather than through typeof(...): every type in it is internal and this project
		// is not one of its InternalsVisibleTo friends, so there is no type here to name.
		var exportedTypes = Assembly.Load(SharedPersistenceAssemblyName).GetExportedTypes();

		exportedTypes.Should().BeEmpty();
	}
}
