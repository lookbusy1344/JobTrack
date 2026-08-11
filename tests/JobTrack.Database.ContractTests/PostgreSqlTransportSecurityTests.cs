namespace JobTrack.Database.ContractTests;

using AwesomeAssertions;

/// <summary>
///     TC-DB-TRANSPORT-002: validator-level coverage for security review remediation §2.9's
///     <c>JobTrack.Database</c>-side copy of the transport-security check (deliberately duplicated
///     from <c>JobTrack.Persistence.PostgreSql.PostgreSqlTransportSecurity</c> -- see that type's own
///     remarks for why). No live PostgreSQL connection needed -- <see cref="PostgreSqlTransportSecurity.Validate" />
///     only parses the connection string.
/// </summary>
public sealed class PostgreSqlTransportSecurityTests
{
	[Fact]
	public void A_remote_host_with_prefer_ssl_mode_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=db.example.internal;Database=jobtrack;SSL Mode=Prefer");

		act.Should().Throw<SchemaDeploymentException>().WithMessage("*db.example.internal*");
	}

	[Fact]
	public void A_remote_host_with_verify_full_but_no_root_certificate_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=db.example.internal;Database=jobtrack;SSL Mode=VerifyFull");

		act.Should().Throw<SchemaDeploymentException>().WithMessage("*Root Certificate*");
	}

	[Fact]
	public void A_remote_host_with_verify_full_and_a_root_certificate_is_accepted()
	{
		var act = () => PostgreSqlTransportSecurity.Validate(
			"Host=db.example.internal;Database=jobtrack;SSL Mode=VerifyFull;Root Certificate=/etc/jobtrack/ca.pem");

		act.Should().NotThrow();
	}

	[Fact]
	public void A_remote_host_with_verify_ca_and_a_root_certificate_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate(
			"Host=db.example.internal;Database=jobtrack;SSL Mode=VerifyCA;Root Certificate=/etc/jobtrack/ca.pem");

		act.Should().Throw<SchemaDeploymentException>().WithMessage("*VerifyFull*");
	}

	[Fact]
	public void A_unix_domain_socket_host_is_accepted_regardless_of_ssl_mode()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=/tmp;Port=5432;Database=jobtrack");

		act.Should().NotThrow();
	}

	[Theory]
	[InlineData("localhost")]
	[InlineData("127.0.0.1")]
	[InlineData("::1")]
	public void A_loopback_host_is_accepted_regardless_of_ssl_mode(string host)
	{
		var act = () => PostgreSqlTransportSecurity.Validate($"Host={host};Database=jobtrack;SSL Mode=Disable");

		act.Should().NotThrow();
	}
}
