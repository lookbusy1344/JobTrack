namespace JobTrack.ArchitectureTests;

using AwesomeAssertions;
using TestSupport;

public sealed class DockerImageArchitectureTests
{
	/// <summary>Cloud Run runs amd64; <c>Dockerfile.postgresql</c> derives this RID from TARGETARCH.</summary>
	private const string PublishedRuntimeIdentifier = "linux-x64";

	private const string TargetFrameworkMoniker = "net10.0";

	private const string PackagesLockFileName = "packages.lock.json";

	private static readonly string SolutionRoot = RepositoryPaths.SolutionRoot();

	[Fact]
	public void Demo_image_seeds_the_published_requester_account_and_its_request_scenario()
	{
		var dockerfile = File.ReadAllText(Path.Combine(SolutionRoot, "Dockerfile"));

		dockerfile.Should().Contain("ARG REQUESTER_USERNAME=requester");
		dockerfile.Should().Contain("ARG DEMO_PASSWORD=demo-jobtrack-1234");
		dockerfile.Should().Contain("ARG REQUESTER_PASSWORD=requester-jobtrack-1234");
		dockerfile.Should().NotContain("--allow-weak-password");
		dockerfile.Should().Contain("--roles Requester --no-force-password-change");
		dockerfile.Should().Contain("/app/uatseed/JobTrack.UatSeed --provider sqlite");
		dockerfile.Should().Contain(
			"--requester-demo --requester-username \"$REQUESTER_USERNAME\" --job-manager-username \"$DEMO_USERNAME\"");
	}

	[Fact]
	public void Persistent_images_pin_and_verify_every_external_build_input()
	{
		var dockerfile = File.ReadAllText(Path.Combine(SolutionRoot, "Dockerfile.postgresql"));

		dockerfile.Should().Contain("mcr.microsoft.com/dotnet/sdk:10.0@sha256:");
		dockerfile.Should().Contain("mcr.microsoft.com/dotnet/aspnet:10.0-noble@sha256:");
		dockerfile.Should().Contain("mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:");
		dockerfile.Should().Contain("Microsoft.Web.LibraryManager.Cli --version 3.0.114");
		dockerfile.Should().Contain("sha256sum --check docker/libman.sha256");
		dockerfile.Should().Contain("dotnet restore src/JobTrack.Web/JobTrack.Web.csproj --locked-mode");
	}

	[Fact]
	public void Persistent_deployment_reconciles_cloud_security_controls()
	{
		var script = ReadDeploymentScript();

		script.Should().Contain("containerscanning.googleapis.com");
		script.Should().Contain("ondemandscanning.googleapis.com");
		script.Should().Contain("--connector-enforcement=REQUIRED");
		script.Should().Contain("--clear-authorized-networks");
		script.Should().Contain("--deletion-protection");
		script.Should().Contain("--retain-backups-on-delete");
		script.Should().Contain("--final-backup");
		script.Should().Contain("--enable-point-in-time-recovery");
		script.Should().Contain("--public-access-prevention");
		script.Should().Contain("--uniform-bucket-level-access");
		script.Should().Contain("--versioning");
		script.Should().NotContain("AllowedHosts=*.run.app");
	}

	[Fact]
	public void Persistent_deployment_never_puts_database_passwords_in_process_arguments()
	{
		var script = ReadDeploymentScript();

		script.Should().NotContain("--root-password=\"$db_admin_password\"");
		script.Should().NotContain("--password=\"$db_admin_password\"");
		script.Should().Contain("--flags-file=\"$database_password_flags\"");
	}

	[Fact]
	public void Persistent_deployment_scans_and_deploys_immutable_image_digests()
	{
		var script = ReadDeploymentScript();

		script.Should().Contain("--immutable-tags");
		script.Should().Contain("--allow-vulnerability-scanning");
		script.Should().Contain("--sbom=true");
		script.Should().Contain("--provenance=mode=max");
		script.Should().Contain("scan_image_for_release");

		// Both scan calls must be non-interactive: gcloud prompts to install its bundled Python runtime on
		// first use, and an unattended deploy answers that with an error rather than a default.
		script.Should().Contain("gcloud artifacts docker images scan \"$image\" --quiet");
		script.Should().Contain("gcloud artifacts docker images list-vulnerabilities \"$scan_name\" --quiet");

		// `scan` returns a long-running *operation*; the scan resource that list-vulnerabilities takes is in
		// its response. Reading `name` yields the operation and makes the severity gate 404 every time.
		script.Should().Contain("--remote --location=europe --format='value(response.scan)'");
		script.Should().NotContain("--remote --location=europe --format='value(name)'");

		script.Should().Contain("serve_image_by_digest");
		script.Should().Contain("provision_image_by_digest");
		script.Should().Contain("--image=\"$serve_image_by_digest\"");
		script.Should().Contain("--image=\"$provision_image_by_digest\"");
	}

	[Fact]
	public void Persistent_deployment_pins_secret_versions_and_removes_the_privileged_job()
	{
		var script = ReadDeploymentScript();

		script.Should().Contain("secret_version");
		script.Should().NotContain("jobtrack-cs-domain:latest");
		script.Should().NotContain("jobtrack-db-admin-password:latest");
		script.Should().Contain("delete_provision_job");
		script.Should().Contain("trap cleanup EXIT");
		script.Should().Contain("provision_job_may_exist=true\nprovision_access_may_exist=true\ndelete_provision_job\nrevoke_provision_access");
		script.Should().NotContain("\"$admin_username\" \"$admin_password\"");
		script.Should().NotContain("\"$user1_username\" \"$user1_password\"");
		script.Should().NotContain("\"$user2_username\" \"$user2_password\"");
	}

	[Fact]
	public void Persistent_deployment_bounds_privileged_job_access_with_a_hard_expiry()
	{
		var script = ReadDeploymentScript();

		script.Should().Contain("provision_access_expiry");
		script.Should().Contain("request.time < timestamp(\\\"$provision_access_expiry\\\")");
		script.Should().Contain("--condition=\"$provision_access_condition\"");
		script.Should().NotContain(
			"--member=\"serviceAccount:$provision_service_account\" \\\n\t\t\t--role=roles/cloudsql.client --condition=None");

		var finalScan = script.LastIndexOf("scan_image_for_release", StringComparison.Ordinal);
		var temporaryGrant = script.IndexOf("grant_provision_access", StringComparison.Ordinal);
		var jobExecution = script.IndexOf("gcloud run jobs deploy \"$provision_job\"", StringComparison.Ordinal);

		temporaryGrant.Should().BeGreaterThan(finalScan, "privilege must not exist while images build and scan");
		jobExecution.Should().BeGreaterThan(temporaryGrant, "privilege is granted immediately around job execution");
	}

	[Fact]
	public void Persistent_deployment_reconciles_provisioning_grants_leaked_by_an_interrupted_run()
	{
		var script = ReadDeploymentScript();

		// Every provisioning grant carries a per-run condition title, and revoking passes that exact
		// condition. A run killed between grant and revoke (SIGKILL outruns the EXIT trap) therefore leaves a
		// binding no later run can match, because each run builds a different title. The expiry still bounds
		// the privilege, but the dead entries accumulate, so reconcile the whole family before granting.
		script.Should().Contain("provision_access_title_prefix=");
		script.Should().Contain("sweep_stale_provision_bindings");

		// The inline --condition form is comma-separated, so it cannot express a condition whose description
		// or expression contains a comma; the file form matches any condition exactly.
		script.Should().Contain("--condition-from-file=");

		var sweep = script.IndexOf("sweep_stale_provision_bindings \"projects\"", StringComparison.Ordinal);
		var grant = script.IndexOf("grant_provision_access\n", StringComparison.Ordinal);

		sweep.Should().BeGreaterThan(0, "the sweep must actually run, not merely be defined");
		grant.Should().BeGreaterThan(sweep, "leaked bindings are reconciled before this run adds its own");
	}

	[Fact]
	public void Binary_authorization_setup_completes_without_organization_policy_administration()
	{
		var setupScript = ReadBinaryAuthorizationSetupScript();

		// The project-wide fail-closed policy is what gates JobTrack's own releases, and every deployment
		// script passes --binary-authorization=default to have it evaluated. The organization policy is
		// defence in depth against a *different* deployment opting out, and setting it needs a role a project
		// owner does not hold. Failing the whole setup on it reports a configured project as broken.
		setupScript.Should().Contain("if ! gcloud resource-manager org-policies allow");
		setupScript.Should().Contain("roles/orgpolicy.policyAdmin");
		setupScript.Should().Contain("WARNING:");

		// A fresh attestor has no publicKeys at all, and `null | any(...)` aborts jq with "Cannot iterate
		// over null" before the intended check ever runs.
		setupScript.Should().Contain(".userOwnedGrafeasNote.publicKeys // []");
		setupScript.Should().NotContain(".userOwnedGrafeasNote.publicKeys | any(");
	}

	[Fact]
	public void Persistent_deployment_validates_a_candidate_before_promoting_traffic()
	{
		var script = ReadDeploymentScript();

		script.Should().Contain("for required_command in curl docker gcloud git jq openssl");
		script.Should().Contain("--no-traffic");
		script.Should().Contain("--tag=\"$candidate_tag\"");
		script.Should().Contain("deploy_candidate \"$preliminary_allowed_hosts\" false");
		script.Should().Contain("deploy_candidate \"$allowed_hosts\" true");
		script.Should().Contain("smoke_test_candidate");
		script.Should().Contain("gcloud run services update-traffic \"$service\"");

		var candidateDeployment = script.IndexOf("--no-traffic", StringComparison.Ordinal);
		var schemaDeployment = script.IndexOf("gcloud run jobs deploy \"$provision_job\"", StringComparison.Ordinal);
		var candidateSmokeTest = script.LastIndexOf("smoke_test_candidate", StringComparison.Ordinal);
		var trafficPromotion = script.IndexOf("gcloud run services update-traffic \"$service\"", StringComparison.Ordinal);

		schemaDeployment.Should().BeGreaterThan(
			candidateDeployment,
			"the candidate must prove it can start before the schema changes");
		candidateSmokeTest.Should().BeGreaterThan(
			schemaDeployment,
			"the candidate must be exercised against the upgraded schema");
		trafficPromotion.Should().BeGreaterThan(
			candidateSmokeTest,
			"only a successfully validated candidate may receive production traffic");

		// Cloud Run builds a tag URL as <tag>---<status.url host>, i.e. on the legacy
		// <service>-<hash>-<regioncode>.a.run.app name -- never on the <service>-<project-number> one. The
		// smoke test requests the URL the API reports, so deriving AllowedHosts from the wrong base makes
		// the host filter reject the candidate with 400 and fails every deployment after the schema ran.
		script.Should().Contain("candidate_host=\"$candidate_tag---$service_host\"");
		script.Should().NotContain("candidate_host=\"$candidate_tag---$alternate_service_host\"");
	}

	[Fact]
	public void Persistent_deployment_requires_a_signed_release_attestation()
	{
		var script = ReadDeploymentScript();
		var setupScript = ReadBinaryAuthorizationSetupScript();

		script.Should().Contain("binaryauthorization.googleapis.com");
		script.Should().Contain("containeranalysis.googleapis.com");
		script.Should().Contain("attest_image_for_release \"$provision_image_by_digest\"");
		script.Should().Contain("attest_image_for_release \"$serve_image_by_digest\"");

		// Building with SBOM and provenance publishes an OCI *index*: the platform image plus an
		// attestation manifest. Cloud Run resolves that index to the platform-specific child manifest and
		// Binary Authorization evaluates the child's digest, so attesting only the index digest is denied
		// with "No attestations found that were valid and signed by a key trusted by the attestor".
		script.Should().Contain("vnd.docker.reference.type");
		script.Should().Contain("attestation-manifest");
		script.Should().Contain("attest_digest");
		script.Should().Contain("--binary-authorization=default");

		setupScript.Should().Contain("existing policy has custom admission rules");
		setupScript.Should().Contain(".defaultAdmissionRule.evaluationMode == \"ALWAYS_ALLOW\"");
		setupScript.Should().Contain("REQUIRE_ATTESTATION");
		setupScript.Should().Contain("ENFORCED_BLOCK_AND_AUDIT_LOG");
		setupScript.Should().Contain("roles/binaryauthorization.attestorsVerifier");
		setupScript.Should().Contain("roles/containeranalysis.notes.occurrences.viewer");
		setupScript.Should().Contain("--purpose=asymmetric-signing");
		setupScript.Should().Contain("run.allowedBinaryAuthorizationPolicies");
	}

	[Fact]
	public void Persistent_deployment_encrypts_the_data_protection_key_ring()
	{
		var dockerfile = File.ReadAllText(Path.Combine(SolutionRoot, "Dockerfile.postgresql"));
		var script = ReadDeploymentScript();

		dockerfile.Should().Contain("DataProtection__CertificatePath");
		dockerfile.Should().Contain("DataProtection__CertificatePasswordPath");
		script.Should().Contain("jobtrack-data-protection-certificate");
		script.Should().Contain("jobtrack-data-protection-certificate-password");
		script.Should().Contain("if [[ $certificate_exists != \"$password_exists\" ]]");
		script.Should().Contain("openssl pkcs12 -in \"$archive\"");

		// Cloud Run mounts each secret as its own directory-backed volume, so two different secrets sharing
		// one directory is rejected outright ("a different secret is already mounted in the same
		// directory"). The archive and its password are separate secrets and need separate directories.
		var certificatePath = ShellAssignment(script, "certificate_mount_path");
		var passwordPath = ShellAssignment(script, "certificate_password_mount_path");

		Path.GetDirectoryName(certificatePath).Should().NotBe(
			Path.GetDirectoryName(passwordPath),
			"Cloud Run refuses two different secrets mounted into one directory");

		// The container reads these paths from baked-in ENV, so the script must mount where it looks.
		dockerfile.Should().Contain($"ENV DataProtection__CertificatePath={certificatePath}");
		dockerfile.Should().Contain($"ENV DataProtection__CertificatePasswordPath={passwordPath}");
	}

	/// <summary>Reads a plain <c>name=value</c> assignment out of the deployment script.</summary>
	private static string ShellAssignment(string script, string name)
	{
		var prefix = $"{name}=";
		var line = script.Split('\n').SingleOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.Ordinal));

		line.Should().NotBeNull($"{name} must be assigned exactly once at the top of the deployment script");

		return line![prefix.Length..].Trim('"');
	}

	[Fact]
	public void Emergency_recovery_uses_an_ephemeral_least_privilege_job()
	{
		var script = File.ReadAllText(Path.Combine(SolutionRoot, "scripts", "emergency-reset-cloudrun-postgresql.sh"));

		script.Should().Contain("jobtrack-emergency-reset@");
		script.Should().Contain("jobtrack-role-password-emergency-reset");
		script.Should().Contain("provision_image_by_digest");
		script.Should().Contain("trap cleanup EXIT");
		script.Should().Contain("gcloud run jobs delete");
		script.Should().Contain("--binary-authorization=default");
		script.Should().Contain("emergency_access_expiry");
		script.Should().Contain("request.time < timestamp(\\\"$emergency_access_expiry\\\")");
		script.Should().Contain("--role=roles/cloudsql.client --condition=\"$emergency_access_condition\"");
		script.Should().NotContain("jobtrack-db-admin-password");
		script.Should().NotContain("jobtrack-provision-sa@");
	}

	[Fact]
	public void Every_committed_lock_file_carries_the_runtime_identifier_the_image_restores_against()
	{
		var lockFiles = Directory.GetFiles(
			Path.Combine(SolutionRoot, "src"),
			PackagesLockFileName,
			SearchOption.AllDirectories);

		lockFiles.Should().NotBeEmpty();

		foreach (var lockFile in lockFiles) {
			// `Dockerfile.postgresql` restores with `--locked-mode -r linux-x64`, which NuGet satisfies only
			// from a RID-specific section. A plain RID-less restore rewrites these files without one, so the
			// projects must declare the RID (see src/Directory.Build.props) or the image build fails NU1004.
			File.ReadAllText(lockFile).Should().Contain(
				$"{TargetFrameworkMoniker}/{PublishedRuntimeIdentifier}",
				$"{lockFile} must resolve the {PublishedRuntimeIdentifier} graph the container build restores");
		}
	}

	private static string ReadDeploymentScript() =>
		File.ReadAllText(Path.Combine(SolutionRoot, "scripts", "deploy-cloudrun-postgresql.sh"));

	private static string ReadBinaryAuthorizationSetupScript() =>
		File.ReadAllText(Path.Combine(SolutionRoot, "scripts", "configure-cloudrun-binary-authorization.sh"));
}
