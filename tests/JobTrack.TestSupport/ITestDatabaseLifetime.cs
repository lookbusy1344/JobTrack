namespace JobTrack.TestSupport;

/// <summary>
///     Mirrors xUnit's <c>IAsyncLifetime</c> shape without a package dependency
///     on xUnit — referencing xUnit here would make VSTest treat this shared
///     support library itself as a runnable (and broken) test project.
/// </summary>
public interface ITestDatabaseLifetime
{
	Task InitializeAsync();

	Task DisposeAsync();
}
