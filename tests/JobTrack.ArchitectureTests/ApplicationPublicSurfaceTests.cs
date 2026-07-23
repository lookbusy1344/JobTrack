namespace JobTrack.ArchitectureTests;

using System.Reflection;
using Application;
using AwesomeAssertions;

/// <summary>
///     ADR 0049 (remediation plan §2.3): the application assembly exports only the
///     <see cref="IJobTrackClient" /> facade contract graph and a deliberately small set of supporting
///     integration types. Any other public type is an unreviewed compatibility commitment.
/// </summary>
public sealed class ApplicationPublicSurfaceTests
{
	/// <summary>
	///     Public application types intentionally not reachable from an <see cref="IJobTrackClient" />
	///     member: consumer paging limits, diagnostics integration, and password-hasher marker types
	///     used by the two providers' public factory signatures.
	/// </summary>
	private static readonly Type[] ApprovedSupportingTypes = [
		typeof(AuditSearchPaging),
		typeof(AwaitingProgressPaging),
		typeof(BootstrapCredentialSubject),
		typeof(EmployeeCredentialSubject),
		typeof(JobTrackDiagnostics),
	];

	[Fact]
	public void Application_exports_only_the_approved_facade_contract_graph()
	{
		var applicationAssembly = typeof(IJobTrackClient).Assembly;
		var approvedTypes = FindFacadeContractGraph(applicationAssembly)
			.Concat(ApprovedSupportingTypes)
			.ToHashSet();
		var unexpectedTypes = applicationAssembly.GetExportedTypes()
			.Where(type => !approvedTypes.Contains(type))
			.Select(type => type.FullName)
			.Order(StringComparer.Ordinal)
			.ToList();

		unexpectedTypes.Should().BeEmpty(
			"new public application types must be reachable facade contracts or added to the narrow reviewed supporting-type allowlist");
	}

	private static HashSet<Type> FindFacadeContractGraph(Assembly applicationAssembly)
	{
		var discovered = new HashSet<Type>();
		var pending = new Queue<Type>();
		pending.Enqueue(typeof(IJobTrackClient));

		while (pending.TryDequeue(out var candidate)) {
			EnqueueTypeComponents(candidate, applicationAssembly, discovered, pending);
		}

		return discovered;
	}

	private static void EnqueueTypeComponents(
		Type candidate,
		Assembly applicationAssembly,
		HashSet<Type> discovered,
		Queue<Type> pending)
	{
		if (candidate.HasElementType) {
			pending.Enqueue(candidate.GetElementType()!);
		}

		foreach (var argument in candidate.GetGenericArguments()) {
			pending.Enqueue(argument);
		}

		if (candidate.Assembly != applicationAssembly || !discovered.Add(candidate)) {
			return;
		}

		foreach (var property in candidate.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
			pending.Enqueue(property.PropertyType);
		}

		if (!candidate.IsInterface) {
			return;
		}

		foreach (var method in candidate.GetMethods()) {
			pending.Enqueue(method.ReturnType);
			foreach (var parameter in method.GetParameters()) {
				pending.Enqueue(parameter.ParameterType);
			}
		}
	}
}
