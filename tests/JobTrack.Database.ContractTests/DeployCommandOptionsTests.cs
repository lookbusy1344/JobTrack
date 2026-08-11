namespace JobTrack.Database.ContractTests;

using AwesomeAssertions;

/// <summary>
///     Security review remediation §2.7: <c>--connection-string-file</c> as an alternative to
///     <c>--connection-string</c>, mutually exclusive, so a production connection string need not
///     appear in process listings or shell history.
/// </summary>
public sealed class DeployCommandOptionsTests
{
	[Fact]
	public void Connection_string_flag_alone_is_accepted()
	{
		var options = DeployCommandOptions.Parse(["--provider", "sqlite", "--connection-string", "Data Source=jobtrack.db"]);

		options.ConnectionString.Should().Be("Data Source=jobtrack.db");
	}

	[Fact]
	public void Direct_connection_string_with_a_password_is_rejected_without_echoing_it()
	{
		const string secret = "database-secret-in-argv";
		var act = () => DeployCommandOptions.Parse([
			"--provider", "postgresql", "--connection-string", $"Host=localhost;Password={secret}",
		]);

		act.Should().Throw<SchemaDeploymentException>().Which.Message.Should().NotContain(secret);
	}

	[Fact]
	public void Direct_connection_string_with_the_pwd_alias_is_rejected()
	{
		var act = () => DeployCommandOptions.Parse([
			"--provider", "postgresql", "--connection-string", "Host=localhost;Pwd=secret",
		]);

		act.Should().Throw<SchemaDeploymentException>();
	}

	[Fact]
	public void Connection_string_file_flag_alone_is_accepted_and_reads_the_trimmed_file_contents()
	{
		var path = Path.GetTempFileName();
		try {
			File.WriteAllText(path, "  Data Source=jobtrack.db  \n");

			var options = DeployCommandOptions.Parse(["--provider", "sqlite", "--connection-string-file", path]);

			options.ConnectionString.Should().Be("Data Source=jobtrack.db");
		}
		finally {
			File.Delete(path);
		}
	}

	[Fact]
	public void Both_connection_string_and_connection_string_file_is_a_parse_error()
	{
		var act = () => DeployCommandOptions.Parse([
			"--provider", "sqlite", "--connection-string", "Data Source=jobtrack.db", "--connection-string-file", "/tmp/does-not-matter",
		]);

		act.Should().Throw<SchemaDeploymentException>().WithMessage("*mutually exclusive*");
	}

	[Fact]
	public void Neither_connection_string_nor_connection_string_file_is_a_parse_error()
	{
		var act = () => DeployCommandOptions.Parse(["--provider", "sqlite"]);

		act.Should().Throw<SchemaDeploymentException>().WithMessage("*--connection-string*");
	}

	[Fact]
	public void A_missing_connection_string_file_produces_an_error_that_names_the_path_but_not_a_secret()
	{
		var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.txt");

		var act = () => DeployCommandOptions.Parse(["--provider", "sqlite", "--connection-string-file", missingPath]);

		act.Should().Throw<SchemaDeploymentException>().WithMessage($"*{missingPath}*");
	}
}
