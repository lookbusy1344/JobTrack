namespace JobTrack.Application.Tests;

using System.Diagnostics;
using Abstractions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Identity;

public sealed class InstallationCommandsTests
{
	private static BootstrapAdministratorRequest CreateRequest() => new() {
		DisplayName = "Ada Lovelace",
		IanaTimeZone = "Europe/London",
		UserName = "ada",
		Password = "correct horse battery staple",
		CorrelationId = Guid.NewGuid(),
	};

	private static InstallationCommands CreateSut(FakeInstallationBootstrapPort port) =>
		new(port, new PasswordHasher<BootstrapCredentialSubject>());

	[Fact]
	public async Task Bootstrapping_returns_the_persisted_administrator_and_root_identifiers()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);

		var result = await sut.BootstrapAdministratorAsync(CreateRequest());

		result.AdministratorId.Should().Be(new AppUserId(1));
		result.AdministratorVersion.Should().Be(1);
		result.RootJobNodeId.Should().Be(new JobNodeId(1));
		result.RootVersion.Should().Be(1);
		result.InitializedAt.Should().Be(port.InitializedAtToReturn);
	}

	[Fact]
	public async Task Bootstrapping_hashes_the_password_before_it_reaches_the_port()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);
		var request = CreateRequest();

		await sut.BootstrapAdministratorAsync(request);

		port.LastRequest.Should().NotBeNull();
		port.LastRequest!.PasswordHash.Should().NotBe(request.Password);
		port.LastRequest.PasswordHash.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task Bootstrapping_generates_a_non_empty_security_stamp()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);

		await sut.BootstrapAdministratorAsync(CreateRequest());

		port.LastRequest!.SecurityStamp.Should().NotBeNullOrWhiteSpace();
	}

	[Fact]
	public async Task Bootstrapping_emits_one_bounded_activity_with_the_correlation_id_and_no_sensitive_tags()
	{
		var stopped = new List<Activity>();
		using var listener = new ActivityListener {
			ShouldListenTo = source => source.Name == JobTrackDiagnostics.ActivitySourceName,
			Sample = static (ref _) => ActivitySamplingResult.AllData,
			ActivityStopped = stopped.Add,
		};
		ActivitySource.AddActivityListener(listener);
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);
		var request = CreateRequest();

		_ = await sut.BootstrapAdministratorAsync(request);

		var operation = stopped.Should()
			.ContainSingle(activity => activity.OperationName == "installation.bootstrap-administrator")
			.Which;
		operation.Status.Should().Be(ActivityStatusCode.Ok);
		operation.GetTagItem("jobtrack.correlation_id").Should().Be(request.CorrelationId.ToString("D"));
		operation.GetTagItem("jobtrack.user_name").Should().BeNull();
		operation.GetTagItem("jobtrack.password").Should().BeNull();
	}

	[Fact]
	public async Task Bootstrapping_forwards_profile_fields_unchanged()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);
		var request = CreateRequest();

		await sut.BootstrapAdministratorAsync(request);

		port.LastRequest!.DisplayName.Should().Be(request.DisplayName);
		port.LastRequest.IanaTimeZone.Should().Be(request.IanaTimeZone);
		port.LastRequest.DefaultHourlyRate.Should().Be(new HourlyRate(20m));
		port.LastRequest.UserName.Should().Be(request.UserName);
	}

	[Fact]
	public async Task Bootstrapping_preserves_an_explicit_default_hourly_rate()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);
		var request = CreateRequest() with { DefaultHourlyRate = new HourlyRate(25m) };

		await sut.BootstrapAdministratorAsync(request);

		port.LastRequest!.DefaultHourlyRate.Should().Be(new HourlyRate(25m));
	}

	[Fact]
	public async Task Bootstrapping_twice_throws_and_leaves_the_first_result_untouched()
	{
		var port = new FakeInstallationBootstrapPort();
		var sut = CreateSut(port);
		await sut.BootstrapAdministratorAsync(CreateRequest());

		var act = () => sut.BootstrapAdministratorAsync(CreateRequest());

		(await act.Should().ThrowAsync<InvariantViolationException>())
			.Which.ConstraintId.Should().Be("installation-already-initialised");
	}

	[Fact]
	public void Constructor_rejects_a_null_port()
	{
		var act = () => new InstallationCommands(null!, new PasswordHasher<BootstrapCredentialSubject>());

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public void Constructor_rejects_a_null_password_hasher()
	{
		var act = () => new InstallationCommands(new FakeInstallationBootstrapPort(), null!);

		act.Should().Throw<ArgumentNullException>();
	}

	[Fact]
	public async Task BootstrapAdministratorAsync_rejects_a_null_request()
	{
		var sut = CreateSut(new());

		Func<Task> act = () => sut.BootstrapAdministratorAsync(null!);

		await act.Should().ThrowAsync<ArgumentNullException>();
	}

	[Fact]
	public void BootstrapAdministratorAsync_throws_synchronously_for_a_null_request()
	{
		var sut = CreateSut(new());

		Action act = () => _ = sut.BootstrapAdministratorAsync(null!);

		act.Should().Throw<ArgumentNullException>();
	}
}
