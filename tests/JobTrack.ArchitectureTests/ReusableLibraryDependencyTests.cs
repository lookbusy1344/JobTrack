namespace JobTrack.ArchitectureTests;

using System.Collections.Frozen;
using System.Reflection;
using Abstractions;
using Application;
using AwesomeAssertions;
using Domain.Intervals;
using Persistence.PostgreSql;
using Persistence.Sqlite;

public sealed class ReusableLibraryDependencyTests
{
	private static readonly FrozenDictionary<string, FrozenSet<string>> AllowedProjectReferences =
		new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal) {
			["JobTrack.Abstractions"] = FrozenSet<string>.Empty,
			["JobTrack.Domain"] = FrozenSet.ToFrozenSet(["JobTrack.Abstractions"], StringComparer.Ordinal),
			["JobTrack.Application"] = FrozenSet.ToFrozenSet(["JobTrack.Abstractions", "JobTrack.Domain"], StringComparer.Ordinal),
			// Shared hosts the provider-neutral port implementations both providers derive from, so it
			// sees Application's internal port interfaces and records (and Domain transitively). It must
			// still never reach a provider assembly or ASP.NET Core.
			["JobTrack.Persistence.Shared"] = FrozenSet.ToFrozenSet(
				["JobTrack.Abstractions", "JobTrack.Domain", "JobTrack.Application"], StringComparer.Ordinal),
			["JobTrack.Persistence.PostgreSql"] = FrozenSet.ToFrozenSet(
				["JobTrack.Abstractions", "JobTrack.Application", "JobTrack.Domain", "JobTrack.Persistence.Shared"], StringComparer.Ordinal),
			["JobTrack.Persistence.Sqlite"] = FrozenSet.ToFrozenSet(
				["JobTrack.Abstractions", "JobTrack.Application", "JobTrack.Domain", "JobTrack.Persistence.Shared"], StringComparer.Ordinal),
		}.ToFrozenDictionary(StringComparer.Ordinal);

	public static TheoryData<Type> ReusableAssemblyMarkers => new() {
		typeof(AppUserId),
		typeof(WorkInterval),
		typeof(IJobTrackClient),
		typeof(JobTrackPostgreSql),
		typeof(JobTrackSqlite),
	};

	[Theory]
	[MemberData(nameof(ReusableAssemblyMarkers))]
	public void Reusable_assemblies_reference_only_allowed_JobTrack_projects(Type markerType)
	{
		var assembly = markerType.Assembly;
		var projectReferences = assembly.GetReferencedAssemblies()
			.Select(reference => reference.Name)
			.Where(name => name is not null && name.StartsWith("JobTrack.", StringComparison.Ordinal))
			.Cast<string>();

		projectReferences.Should().BeSubsetOf(AllowedProjectReferences[assembly.GetName().Name!]);
	}

	[Theory]
	[MemberData(nameof(ReusableAssemblyMarkers))]
	public void Reusable_assemblies_have_no_ASP_NET_Core_dependency(Type markerType)
	{
		var references = markerType.Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

		references.Should().NotContain(name => name != null && name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
	}

	[Fact]
	public void Shared_persistence_configuration_preserves_its_narrow_dependency_boundary()
	{
		var assembly = Assembly.Load("JobTrack.Persistence.Shared");
		var projectReferences = assembly.GetReferencedAssemblies()
			.Select(reference => reference.Name)
			.Where(name => name is not null && name.StartsWith("JobTrack.", StringComparison.Ordinal))
			.Cast<string>();

		projectReferences.Should().BeSubsetOf(AllowedProjectReferences[assembly.GetName().Name!]);
		assembly.GetReferencedAssemblies().Select(reference => reference.Name)
			.Should().NotContain(name => name != null && name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
	}
}
