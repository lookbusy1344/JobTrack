namespace JobTrack.Identity.Tests;

using AwesomeAssertions;
using Identity;

/// <summary>
///     TC-DB-TRANSPORT-001: validator-level coverage for security review remediation §2.9. No live
///     PostgreSQL connection needed -- <see cref="PostgreSqlTransportSecurity.Validate" /> only
///     parses the connection string.
/// </summary>
public sealed class PostgreSqlTransportSecurityTests
{
	[Fact]
	public void A_remote_host_with_prefer_ssl_mode_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=db.example.internal;Database=jobtrack;SSL Mode=Prefer");

		act.Should().Throw<InvalidOperationException>().WithMessage("*db.example.internal*");
	}

	[Fact]
	public void A_remote_host_with_no_ssl_mode_specified_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=db.example.internal;Database=jobtrack");

		act.Should().Throw<InvalidOperationException>();
	}

	[Fact]
	public void A_remote_host_with_verify_full_but_no_root_certificate_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=db.example.internal;Database=jobtrack;SSL Mode=VerifyFull");

		act.Should().Throw<InvalidOperationException>().WithMessage("*Root Certificate*");
	}

	[Fact]
	public void A_remote_host_with_trust_server_certificate_and_no_verify_ssl_mode_is_rejected()
	{
		var act = () => PostgreSqlTransportSecurity.Validate(
			"Host=db.example.internal;Database=jobtrack;SSL Mode=Require;Trust Server Certificate=true");

		act.Should().Throw<InvalidOperationException>();
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

		act.Should().Throw<InvalidOperationException>().WithMessage("*VerifyFull*");
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

	[Fact]
	public void A_unix_domain_socket_host_is_accepted_regardless_of_ssl_mode()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Host=/tmp;Port=5432;Database=jobtrack");

		act.Should().NotThrow();
	}

	[Fact]
	public void An_unspecified_host_is_treated_as_local()
	{
		var act = () => PostgreSqlTransportSecurity.Validate("Database=jobtrack");

		act.Should().NotThrow();
	}
}
