namespace JobTrack.Web.EndToEndTests;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Database;
using Microsoft.Data.Sqlite;
using TestSupport;
using Program = Program;

/// <summary>
///     Hosts the real <c>JobTrack.Web</c> application as a real child process listening on real
///     Kestrel sockets, in the <c>Production</c> environment (not <c>Development</c>, unlike every
///     other end-to-end fixture) -- HSTS, HTTPS redirection, and the forwarded-headers/data-protection
///     startup guards only activate outside Development (security review remediation §2.6). Exists
///     specifically to prove Kestrel/host-level body-size, timeout, and forwarded-header behavior that
///     <c>WebApplicationFactory</c>'s in-process <c>TestServer</c> cannot exercise -- see
///     <c>docs/operations/web-host-security.md</c>'s "Settings that cannot be meaningfully proven"
///     section, which this fixture closes out.
/// </summary>
public class ProductionHostFixture : IAsyncLifetime, IDisposable
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";
	private const string CertificatePassword = "production-host-smoke-cert";
	private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ReadinessPollInterval = TimeSpan.FromMilliseconds(200);
	private readonly SqliteDatabaseFixture database = new();

	private readonly string[] knownProxies;
	private readonly StringBuilder processOutput = new();
	private string? certificatePath;
	private string? dataProtectionKeyPath;
	private Process? webProcess;

	/// <summary>Creates the fixture with the given trusted-proxy configuration.</summary>
	/// <param name="knownProxies">
	///     <c>ForwardedHeaders:KnownProxies</c> for this instance. Pass <c>["127.0.0.1"]</c> to trust
	///     the loopback connection every test client in this project connects from, or a documented
	///     non-matching address (e.g. the TEST-NET-3 block <c>203.0.113.1</c>, RFC 5737) to prove an
	///     untrusted proxy's forwarded headers are ignored.
	/// </param>
	public ProductionHostFixture(string[] knownProxies) => this.knownProxies = knownProxies;

	public string HttpsBaseAddress { get; private set; } = string.Empty;

	public string HttpBaseAddress { get; private set; } = string.Empty;

	public async Task InitializeAsync()
	{
		await database.InitializeAsync();
		await DeploySchemaAsync();

		var httpsPort = GetFreeLoopbackPort();
		var httpPort = GetFreeLoopbackPort();
		HttpsBaseAddress = $"https://127.0.0.1:{httpsPort}";
		HttpBaseAddress = $"http://127.0.0.1:{httpPort}";
		certificatePath = WriteSelfSignedCertificate();
		dataProtectionKeyPath = Directory.CreateTempSubdirectory("jobtrack-production-host-smoke-keys-").FullName;

		StartWebProcess(httpsPort, httpPort, certificatePath, dataProtectionKeyPath);
		await WaitForReadinessAsync();
	}

	public async Task DisposeAsync()
	{
		Dispose();
		await database.DisposeAsync();
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);

		if (webProcess is { HasExited: false }) {
			webProcess.Kill(true);
			webProcess.WaitForExit((int)ReadinessTimeout.TotalMilliseconds);
		}

		webProcess?.Dispose();
		webProcess = null;

		if (certificatePath is not null && File.Exists(certificatePath)) {
			File.Delete(certificatePath);
		}

		certificatePath = null;

		if (dataProtectionKeyPath is not null && Directory.Exists(dataProtectionKeyPath)) {
			Directory.Delete(dataProtectionKeyPath, true);
		}

		dataProtectionKeyPath = null;
	}

	private void StartWebProcess(int httpsPort, int httpPort, string certPath, string keyPath)
	{
		var webAssemblyPath = typeof(Program).Assembly.Location;
		var startInfo = new ProcessStartInfo {
			FileName = "dotnet",
			ArgumentList = { webAssemblyPath },
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetDirectoryName(webAssemblyPath),
		};
		startInfo.EnvironmentVariables["ASPNETCORE_ENVIRONMENT"] = "Production";
		startInfo.EnvironmentVariables["ASPNETCORE_URLS"] = $"{HttpsBaseAddress};{HttpBaseAddress}";
		startInfo.EnvironmentVariables["Database__Provider"] = "Sqlite";
		startInfo.EnvironmentVariables["ConnectionStrings__JobTrackIdentity"] = database.ConnectionString;
		startInfo.EnvironmentVariables["Kestrel__Certificates__Default__Path"] = certPath;
		startInfo.EnvironmentVariables["Kestrel__Certificates__Default__Password"] = CertificatePassword;
		startInfo.EnvironmentVariables["DataProtection__KeyPath"] = keyPath;

		for (var i = 0; i < knownProxies.Length; i++) {
			startInfo.EnvironmentVariables[$"ForwardedHeaders__KnownProxies__{i}"] = knownProxies[i];
		}

		webProcess = new() { StartInfo = startInfo, EnableRaisingEvents = true };
		webProcess.OutputDataReceived += (_, args) => {
			if (args.Data is not null) {
				lock (processOutput) {
					_ = processOutput.AppendLine(args.Data);
				}
			}
		};
		webProcess.ErrorDataReceived += (_, args) => {
			if (args.Data is not null) {
				lock (processOutput) {
					_ = processOutput.AppendLine(args.Data);
				}
			}
		};

		_ = webProcess.Start();
		webProcess.BeginOutputReadLine();
		webProcess.BeginErrorReadLine();
	}

	private async Task WaitForReadinessAsync()
	{
		using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
		using var probeClient = new HttpClient(handler);
		var deadline = DateTime.UtcNow + ReadinessTimeout;

		while (DateTime.UtcNow < deadline) {
			if (webProcess is { HasExited: true }) {
				throw new InvalidOperationException($"The JobTrack.Web process exited early (code {webProcess.ExitCode}). Output:\n{processOutput}");
			}

			try {
				var response = await probeClient.GetAsync($"{HttpsBaseAddress}/Account/Login");
				if (response.IsSuccessStatusCode) {
					return;
				}
			}
			catch (HttpRequestException) {
				// Not listening yet; keep polling until the deadline.
			}

			await Task.Delay(ReadinessPollInterval);
		}

		throw new TimeoutException($"JobTrack.Web did not become ready within {ReadinessTimeout}. Output so far:\n{processOutput}");
	}

	private static int GetFreeLoopbackPort()
	{
		using var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		var port = ((IPEndPoint)listener.LocalEndpoint).Port;
		listener.Stop();
		return port;
	}

	private static string WriteSelfSignedCertificate()
	{
		using var rsa = RSA.Create(2048);
		var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

		var path = Path.Combine(Path.GetTempPath(), $"jobtrack-production-host-smoke-{Guid.NewGuid():N}.pfx");
		File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, CertificatePassword));
		return path;
	}

	private async Task DeploySchemaAsync()
	{
		await using var connection = new SqliteConnection(database.ConnectionString);
		await connection.OpenAsync();
		await using (var pragma = connection.CreateCommand()) {
			pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
			_ = await pragma.ExecuteNonQueryAsync();
		}

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(SchemaProvider.Sqlite));
		var deployer = new SchemaDeployer(connection, new SqliteSchemaVersionStore(), new SqliteDeploymentLockStrategy(), ApplicationVersion,
			AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);
	}
}

/// <summary>Trusts loopback -- every test client in this project connects from 127.0.0.1 -- so forwarded headers are honored.</summary>
public sealed class TrustedProxyProductionHostFixture() : ProductionHostFixture(["127.0.0.1"]);

/// <summary>
///     Trusts only a documented, never-matching TEST-NET-3 address (RFC 5737), so forwarded headers
///     from the loopback connection every test client in this project actually uses are ignored.
/// </summary>
public sealed class UntrustedProxyProductionHostFixture() : ProductionHostFixture(["203.0.113.1"]);
