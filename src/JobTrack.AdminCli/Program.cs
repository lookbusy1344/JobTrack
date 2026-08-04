namespace JobTrack.AdminCli;

using Abstractions;
using Application;
using Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using Persistence.PostgreSql;
using Persistence.Sqlite;
using PicoArgs_dotnet;

public static class Program
{
	private const string UsageMessage =
		"Usage: JobTrack.AdminCli bootstrap --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) " +
		"[--password-stdin] [--no-force-password-change]\n" +
		"       JobTrack.AdminCli reset-password --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) --username <username>\n" +
		"       JobTrack.AdminCli reset-2fa --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) --username <username>\n" +
		"       JobTrack.AdminCli import-tree --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) --username <username> " +
		"--file <path-to-json> [--parent-id <job-node-id>] [--home-node-for <username[,username...]>]\n" +
		"       JobTrack.AdminCli set-home-node --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) --username <username> " +
		"(--node-id <job-node-id> | --clear)\n" +
		"       JobTrack.AdminCli create-employee --provider <postgresql|sqlite> " +
		"(--connection-string <connection-string> | --connection-string-file <path>) " +
		"--actor <admin-username> --username <username> [--password-stdin] --display-name <name> " +
		"--roles <role[,role...]> [--iana-time-zone <iana>] [--default-hourly-rate <amount>] [--no-force-password-change]\n" +
		"  A direct --connection-string must not contain a password; use --connection-string-file, a PostgreSQL passfile, " +
		"or integrated authentication. Omit --password-stdin to prompt interactively without echo.";

	public static async Task<int> Main(string[] args)
	{
		var io = new SystemConsoleIO();

		try {
			var pico = new PicoArgs(args);
			var command = pico.GetCommandOpt();

			return command switch {
				"bootstrap" => await RunBootstrapAsync(BootstrapCommandOptions.Parse(pico), io),
				"reset-password" => await RunResetPasswordAsync(ResetPasswordCommandOptions.Parse(pico), io),
				"reset-2fa" => await RunResetTwoFactorAsync(ResetTwoFactorCommandOptions.Parse(pico), io),
				"import-tree" => await RunImportTreeAsync(JobTreeImportCommandOptions.Parse(pico), io),
				"set-home-node" => await RunSetHomeNodeAsync(SetHomeNodeCommandOptions.Parse(pico), io),
				"create-employee" => await RunCreateEmployeeAsync(CreateEmployeeCommandOptions.Parse(pico), io),
				_ => Usage(io),
			};
		}
		catch (AdminCliUsageException ex) {
			io.WriteError(ex.Message);
			return Usage(io);
		}
		catch (PicoArgsException ex) {
			io.WriteError(ex.Message);
			return Usage(io);
		}
	}

	private static int Usage(SystemConsoleIO io)
	{
		io.WriteError(UsageMessage);
		return 1;
	}

	private static async Task<int> RunBootstrapAsync(BootstrapCommandOptions options, SystemConsoleIO io)
	{
		var client = CreateClient(options.Provider, options.ConnectionString);
		var password = ResolveOptionalPassword(options.Password, options.PasswordFromStdin, io);

		// Only clearing the forced password change needs a UserManager; build it lazily so the common
		// path keeps the same lightweight dependency footprint it had before.
		if (options.ForcePasswordChange) {
			return await BootstrapCommand.RunAsync(io, client.Installation, Environment.UserName, CancellationToken.None, password);
		}

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();

		return await BootstrapCommand.RunAsync(
				io, client.Installation, Environment.UserName, CancellationToken.None, password, userManager, false);
	}

	private static async Task<int> RunResetPasswordAsync(ResetPasswordCommandOptions options, SystemConsoleIO io)
	{
		ValidateTransportSecurity(options.Provider, options.ConnectionString);

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();
		var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<JobTrackIdentityUser>>();

		return await EmergencyPasswordReset.RunAsync(
				io, userManager, identityContext, passwordHasher, options.Provider, options.Username, SystemClock.Instance,
				CancellationToken.None);
	}

	private static async Task<int> RunResetTwoFactorAsync(ResetTwoFactorCommandOptions options, SystemConsoleIO io)
	{
		ValidateTransportSecurity(options.Provider, options.ConnectionString);

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var identityContext = scope.ServiceProvider.GetRequiredService<JobTrackIdentityDbContext>();

		return await EmergencyTwoFactorReset.RunAsync(
				io, userManager, identityContext, options.Provider, options.Username, SystemClock.Instance, CancellationToken.None);
	}

	private static async Task<int> RunImportTreeAsync(JobTreeImportCommandOptions options, SystemConsoleIO io)
	{
		string jsonContent;
		try {
			jsonContent = await File.ReadAllTextAsync(options.FilePath, CancellationToken.None);
		}
		catch (IOException ex) {
			io.WriteError($"Failed to read '{options.FilePath}': {ex.Message}");
			return 1;
		}
		catch (UnauthorizedAccessException ex) {
			io.WriteError($"Failed to read '{options.FilePath}': {ex.Message}");
			return 1;
		}

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var client = CreateClient(options.Provider, options.ConnectionString);

		return await JobTreeImportCommand.RunAsync(
				io, userManager, client, options.Username, new(options.ParentJobNodeId), jsonContent, SystemClock.Instance,
				options.HomeNodeUsernames, CancellationToken.None);
	}

	private static async Task<int> RunSetHomeNodeAsync(SetHomeNodeCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var client = CreateClient(options.Provider, options.ConnectionString);

		return await SetHomeNodeCommand.RunAsync(
				io, userManager, client, options.Username, options.JobNodeId is long nodeId ? new JobNodeId(nodeId) : null,
				CancellationToken.None);
	}

	private static async Task<int> RunCreateEmployeeAsync(CreateEmployeeCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = services.AddSingleton<IClock>(SystemClock.Instance);
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var client = CreateClient(options.Provider, options.ConnectionString);
		var password = options.Password
					   ?? (options.PasswordFromStdin ? io.ReadStdinLine() : PasswordPrompt.ReadConfirmed(io));
		var resolvedOptions = options with { Password = password };

		return await CreateEmployeeCommand.RunAsync(io, userManager, client, resolvedOptions, CancellationToken.None);
	}

	/// <summary>
	///     Security review remediation §2.7: resolves an optional password's effective value —
	///     <paramref name="explicitValue" /> when already resolved by an internal caller, one line from stdin when
	///     <paramref name="fromStdin" /> is set, or <see langword="null" /> otherwise (the caller, e.g.
	///     <see cref="BootstrapCommand" />, falls back to its own masked interactive prompt).
	/// </summary>
	private static string? ResolveOptionalPassword(string? explicitValue, bool fromStdin, SystemConsoleIO io) =>
		explicitValue ?? (fromStdin ? io.ReadStdinLine() : null);

	private static IJobTrackClient CreateClient(AdminCliProvider provider, string connectionString)
	{
		ValidateTransportSecurity(provider, connectionString);

		return provider switch {
			AdminCliProvider.PostgreSql => JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()),
			AdminCliProvider.Sqlite => JobTrackSqlite.Create(connectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{provider}'."),
		};
	}

	/// <summary>
	///     Security review remediation §2.9: outside a same-host Unix-domain socket or loopback TCP
	///     connection, PostgreSQL requires an authenticated encrypted channel (<c>SSL Mode=VerifyFull</c>
	///     with a trusted root certificate) -- Npgsql's own default neither
	///     guarantees encryption nor authenticates the server. No-op for SQLite, which has no
	///     transport-security concept of its own.
	/// </summary>
	private static void ValidateTransportSecurity(AdminCliProvider provider, string connectionString)
	{
		if (provider == AdminCliProvider.PostgreSql) {
			PostgreSqlTransportSecurity.Validate(connectionString);
		}
	}
}
