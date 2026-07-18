namespace JobTrack.AdminCli;

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
		"Usage: JobTrack.AdminCli bootstrap --provider <postgresql|sqlite> --connection-string <connection-string> " +
		"[--password <password>] [--no-force-password-change]\n" +
		"       JobTrack.AdminCli reset-password --provider <postgresql|sqlite> --connection-string <connection-string> --username <username>\n" +
		"       JobTrack.AdminCli reset-2fa --provider <postgresql|sqlite> --connection-string <connection-string> --username <username>\n" +
		"       JobTrack.AdminCli issue-token --provider <postgresql|sqlite> --connection-string <connection-string> --username <username> " +
		"--label <label> [--lifetime-days <days>]\n" +
		"       JobTrack.AdminCli import-tree --provider <postgresql|sqlite> --connection-string <connection-string> --username <username> " +
		"--file <path-to-json> [--parent-id <job-node-id>]\n" +
		"       JobTrack.AdminCli create-employee --provider <postgresql|sqlite> --connection-string <connection-string> " +
		"--actor <admin-username> --username <username> --password <password> --display-name <name> --roles <role[,role...]> " +
		"[--iana-time-zone <iana>] [--default-hourly-rate <amount>] [--no-force-password-change]";

	public static async Task<int> Main(string[] args)
	{
		var io = new SystemConsoleIO();

		try {
			var pico = new PicoArgs(args);
			var command = pico.GetCommandOpt();

			return command switch {
				"bootstrap" => await RunBootstrapAsync(BootstrapCommandOptions.Parse(pico), io).ConfigureAwait(false),
				"reset-password" => await RunResetPasswordAsync(ResetPasswordCommandOptions.Parse(pico), io).ConfigureAwait(false),
				"reset-2fa" => await RunResetTwoFactorAsync(ResetTwoFactorCommandOptions.Parse(pico), io).ConfigureAwait(false),
				"issue-token" => await RunIssueTokenAsync(IssueTokenCommandOptions.Parse(pico), io).ConfigureAwait(false),
				"import-tree" => await RunImportTreeAsync(JobTreeImportCommandOptions.Parse(pico), io).ConfigureAwait(false),
				"create-employee" => await RunCreateEmployeeAsync(CreateEmployeeCommandOptions.Parse(pico), io).ConfigureAwait(false),
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

		// Only clearing the forced password change needs a UserManager; build it lazily so the common
		// path keeps the same lightweight dependency footprint it had before.
		if (options.ForcePasswordChange) {
			return await BootstrapCommand.RunAsync(io, client.Installation, Environment.UserName, CancellationToken.None, options.Password)
				.ConfigureAwait(false);
		}

		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();

		return await BootstrapCommand.RunAsync(
				io, client.Installation, Environment.UserName, CancellationToken.None, options.Password, userManager, false)
			.ConfigureAwait(false);
	}

	private static async Task<int> RunResetPasswordAsync(ResetPasswordCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
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
			io, userManager, identityContext, passwordHasher, options.Provider, options.Username, CancellationToken.None).ConfigureAwait(false);
	}

	private static async Task<int> RunResetTwoFactorAsync(ResetTwoFactorCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
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
			io, userManager, identityContext, options.Provider, options.Username, CancellationToken.None).ConfigureAwait(false);
	}

	private static async Task<int> RunIssueTokenAsync(IssueTokenCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var client = CreateClient(options.Provider, options.ConnectionString);

		return await IssueTokenCommand.RunAsync(
				io, userManager, client, options.Username, options.Label, Duration.FromDays(options.LifetimeDays), CancellationToken.None)
			.ConfigureAwait(false);
	}

	private static async Task<int> RunImportTreeAsync(JobTreeImportCommandOptions options, SystemConsoleIO io)
	{
		string jsonContent;
		try {
			jsonContent = await File.ReadAllTextAsync(options.FilePath, CancellationToken.None).ConfigureAwait(false);
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
				io, userManager, client, options.Username, new(options.ParentJobNodeId), jsonContent, CancellationToken.None)
			.ConfigureAwait(false);
	}

	private static async Task<int> RunCreateEmployeeAsync(CreateEmployeeCommandOptions options, SystemConsoleIO io)
	{
		var services = new ServiceCollection();
		_ = services.AddLogging();
		_ = options.Provider switch {
			AdminCliProvider.PostgreSql => services.AddJobTrackIdentityPostgreSql(options.ConnectionString),
			AdminCliProvider.Sqlite => services.AddJobTrackIdentitySqlite(options.ConnectionString),
			_ => throw new AdminCliUsageException($"Unknown provider '{options.Provider}'."),
		};

		await using var provider = services.BuildServiceProvider();
		using var scope = provider.CreateScope();
		var userManager = scope.ServiceProvider.GetRequiredService<UserManager<JobTrackIdentityUser>>();
		var client = CreateClient(options.Provider, options.ConnectionString);

		return await CreateEmployeeCommand.RunAsync(io, userManager, client, options, CancellationToken.None).ConfigureAwait(false);
	}

	private static IJobTrackClient CreateClient(AdminCliProvider provider, string connectionString) => provider switch {
		AdminCliProvider.PostgreSql => JobTrackPostgreSql.Create(new NpgsqlDataSourceBuilder(connectionString).UseNodaTime().Build()),
		AdminCliProvider.Sqlite => JobTrackSqlite.Create(connectionString),
		_ => throw new AdminCliUsageException($"Unknown provider '{provider}'."),
	};
}
