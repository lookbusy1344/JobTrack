namespace JobTrack.ArchitectureTests;

using System.Text.RegularExpressions;
using AwesomeAssertions;
using TestSupport;

public sealed partial class DockerImageArchitectureTests
{
	/// <summary>Cloud Run runs amd64; <c>Dockerfile.postgresql</c> derives this RID from TARGETARCH.</summary>
	private const string PublishedRuntimeIdentifier = "linux-x64";

	private const string TargetFrameworkMoniker = "net10.0";

	private const string PackagesLockFileName = "packages.lock.json";

	private static readonly string SolutionRoot = RepositoryPaths.SolutionRoot();

	/// <summary>
	///     A shell <c>${var:?message}</c> whose message itself contains a closing brace: the expansion ends
	///     at that brace and the remainder becomes literal text appended to the value.
	/// </summary>
	[GeneratedRegex(@"\$\{[^}\n]*:\?[^}\n]*\{[^}\n]*\}")]
	private static partial Regex TruncatedParameterExpansion();

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
		script.Should().NotContain("AllowedHosts=*.run.app");

		// ADR 0066/plan Stage 8: the data-protection key ring lives in PostgreSQL, so no GCS key-ring
		// bucket is created or secured here.
		script.Should().NotContain("gcloud storage buckets create");
		script.Should().NotContain("gs://$key_bucket");

		// `gcloud run deploy` merges inherited volumes. Clear both collections before recreating current
		// secret mounts, removing the retired key-ring mount and stale generated secret volumes together.
		script.Should().Contain("--clear-volume-mounts");
		script.Should().Contain("--clear-volumes");
		script.Should().NotContain("--add-volume=");
	}

	[Fact]
	public void Persistent_deployment_enables_the_multi_instance_topology()
	{
		var script = ReadDeploymentScript();

		// ADR 0066 §10: service-level scaling is shared across every traffic-serving revision. The
		// similarly named --min-instances/--max-instances flags are revision-level and would allow old
		// and new revisions to consume the full database budget independently during a rollout.
		script.Should().Contain("min_instances=0");
		script.Should().Contain("max_instances=2");
		script.Should().Contain("container_concurrency=80");
		script.Should().Contain("--min=\"$min_instances\"");
		script.Should().Contain("--max=\"$max_instances\"");
		script.Should().Contain("--concurrency=\"$container_concurrency\"");
		script.Should().NotContain("--min-instances=\"$min_instances\"");
		script.Should().NotContain("--max-instances=\"$max_instances\"");
		script.Should().NotContain("--max-instances=1");
		script.Should().Contain("deployed_max_instances");
		script.Should().Contain("deployed_container_concurrency");
		script.Should().Contain("deployed_revision_max_instances");

		script.Should().Contain("Deployment__Topology=MultiInstance");
		script.Should().Contain("DataProtection__Store=PostgreSql");
		script.Should().Contain("RateLimiting__Store=PostgreSql");

		// Plan §2.1/ADR 0066 §2: correctness never relies on session affinity. Passed explicitly at
		// deploy time and re-checked against the live service afterward, so drift or a future edit
		// fails the deploy rather than silently reintroducing it.
		script.Should().Contain("--no-session-affinity");
		script.Should().Contain("session_affinity_enabled");
		script.Should().Contain("[.. | .sessionAffinity? // empty] | any");

		// Plan Stage 8 item 4: a TCP startup probe, and deliberately no liveness or readiness probe.
		// Cloud Run probes carry a Host header that AllowedHosts rejects with 400 before routing sees
		// the path, so any httpGet probe makes every revision fail to become ready. Only --startup-probe
		// accepts tcpSocket.port; --liveness-probe accepts httpGet/grpc alone and so has no satisfiable
		// form here. Asserted so a later edit does not "improve" these back to httpGet, or add a
		// liveness probe, and rediscover both the hard way.
		//
		// This pins intent, not CLI correctness: an assertion can only compare the script against a
		// string written here, so it cannot tell a real gcloud flag from an invented one, nor a key one
		// probe accepts from a key it rejects. Earlier revisions of this file asserted
		// `--set-startup-probe` and then a tcpSocket liveness probe -- gcloud rejects both. Only running
		// the script proves the CLI contract, and shelling out to gcloud from a unit test would make it
		// an environment test, which this repository does not do.
		// Matched with the trailing '=' so these assert what the script *passes*, not what its comments
		// explain -- the surrounding commentary names both flags precisely to stop someone re-adding them.
		script.Should().Contain("--startup-probe=\"tcpSocket.port=8080");
		script.Should().NotContain("--readiness-probe=");
		script.Should().NotContain("probe=\"httpGet", "Cloud Run's probe Host header does not satisfy AllowedHosts");

		// Explicitly cleared, not merely absent. `gcloud run deploy` merges, so omitting the flag leaves
		// an inherited probe in place -- an httpGet liveness probe from an earlier revision survived that
		// way, failed host filtering on every check, and had Cloud Run killing an instance roughly every
		// 90 seconds until it was cleared.
		script.Should().Contain("--liveness-probe=\"\"", "an inherited probe is only removed by passing an empty value");

		// The tagged candidate must prove both dependency readiness and an ordinary Razor response
		// before promotion. /Account/Login alone does not touch the domain connection.
		script.Should().Contain("candidate_smoke_paths=(/health/ready /Account/Login)");
	}

	[Fact]
	public void Persistent_deployment_resolves_every_resource_against_the_project_it_was_given()
	{
		var script = ReadDeploymentScript();

		// The target project is this script's own argument. Left to gcloud's ambient configuration, a
		// call that omits --project resolves against whatever `gcloud config set project` the operator
		// last ran -- which is how a deploy of this project once searched the SQLite demo's registry for
		// its freshly pushed images. The export makes the argument authoritative for every call.
		script.Should().Contain("export CLOUDSDK_CORE_PROJECT=\"$project\"");

		var export = script.IndexOf("export CLOUDSDK_CORE_PROJECT", StringComparison.Ordinal);
		var firstGcloudCall = script.IndexOf("gcloud services enable", StringComparison.Ordinal);
		export.Should().BeLessThan(firstGcloudCall, "the backstop must be set before the first gcloud call");

		// The digest lookups are the specific pair that silently crossed projects; they now also name it
		// explicitly, so the intent survives someone removing the export.
		script.Should().Contain(
			"gcloud artifacts docker images describe --project=\"$project\" \"$provision_image\"");
		script.Should().Contain(
			"gcloud artifacts docker images describe --project=\"$project\" \"$serve_image\"");
	}

	[Fact]
	public void Persistent_deployment_reconciles_the_database_tier_the_connection_budget_assumes()
	{
		var script = ReadDeploymentScript();

		// The budget below is derived from db-custom-1-3840's memory bracket. Setting --tier only in the
		// `create` branch leaves an instance from an earlier run on the old shared-core tier, whose
		// max_connections is a fraction of what the four pools are sized for -- so the patch call, which
		// reconciles every other setting for exactly this reason, must carry it too.
		var patch = script.IndexOf("gcloud sql instances patch", StringComparison.Ordinal);
		patch.Should().BeGreaterThan(0, "an existing instance is reconciled, not only a newly created one");

		var patchArguments = script[patch..script.IndexOf("--quiet", patch, StringComparison.Ordinal)];
		patchArguments.Should().Contain("--tier=\"$sql_tier\"", "the tier the connection budget assumes must be applied to an existing instance");
	}

	[Fact]
	public void Persistent_deployment_sets_an_explicit_connection_pool_budget_within_the_calculated_ceiling()
	{
		var script = ReadDeploymentScript();

		// ADR 0066 §11's formula, reproduced with named values rather than a bare literal per pool.
		script.Should().Contain("planned_peak_hosts=$((max_instances + overshoot_hosts + tagged_candidate_hosts))");
		script.Should().Contain("usable_database_connections=$((database_max_connections - operator_and_deployment_reserve))");
		script.Should().Contain("host_budget=$((usable_database_connections / planned_peak_hosts))");
		script.Should().Contain("if ((pool_budget_total > host_budget)); then");

		script.Should().Contain("Maximum Pool Size=%s");
		script.Should().Contain("\"$domain_pool_max_size\"");
		script.Should().Contain("\"$identity_pool_max_size\"");
		script.Should().Contain("\"$pat_management_pool_max_size\"");
		script.Should().Contain("\"$pat_authentication_pool_max_size\"");
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

		// --sbom/--provenance are only producible by the docker-container driver; the default `docker`
		// driver fails with "Attestation is not supported for the docker driver" partway through a run
		// that has already patched Cloud SQL. Every build therefore names its builder rather than
		// inheriting whichever one the machine happens to have selected, and preflight proves that
		// builder exists with the right driver before any resource is created.
		script.Should().Contain("ensure_buildx_builder");
		script.Should().Contain("buildx_required_driver=\"docker-container\"");
		foreach (var build in script.Split("docker buildx build").Skip(1)) {
			build.Should().StartWith(" --builder=\"$buildx_builder\"", "every image build must pin the attestation-capable builder");
		}

		var preflight = script.IndexOf("ensure_buildx_builder\n", StringComparison.Ordinal);
		var firstMutation = script.IndexOf("gcloud services enable", StringComparison.Ordinal);
		preflight.Should().BeGreaterThan(0, "the builder check must actually run, not merely be defined");
		preflight.Should().BeLessThan(firstMutation, "a local-machine fault must stop the run before it changes anything");
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
	public void Persistent_deployment_cannot_report_success_without_reaching_the_end()
	{
		var script = ReadDeploymentScript();

		// macOS ships bash 3.2, which resets $? to 0 before the EXIT trap when `set -u` aborts on an
		// unbound variable -- a run killed that way deployed nothing and still exited 0. Ordinary
		// failures keep their own status; this sentinel covers the ones that do not.
		script.Should().Contain("deployment_completed=false");
		script.Should().Contain("deployment_completed=true");
		script.Should().Contain("if [[ $deployment_completed != true && $status -eq 0 ]]; then");

		var sentinelSet = script.LastIndexOf("deployment_completed=true", StringComparison.Ordinal);
		var promotion = script.IndexOf("update-traffic", StringComparison.Ordinal);
		sentinelSet.Should().BeGreaterThan(promotion, "the sentinel must be the last thing the script does, after promotion");
	}

	[Fact]
	public void Persistent_deployment_replaces_inherited_volumes_before_mounting_current_secrets()
	{
		var script = ReadDeploymentScript();

		var clearVolumeMounts = script.IndexOf("--clear-volume-mounts", StringComparison.Ordinal);
		var clearVolumes = script.IndexOf("--clear-volumes", StringComparison.Ordinal);
		var setSecrets = script.IndexOf("--set-secrets=", clearVolumes, StringComparison.Ordinal);

		clearVolumeMounts.Should().BeGreaterThan(0);
		clearVolumes.Should().BeGreaterThan(clearVolumeMounts);
		setSecrets.Should().BeGreaterThan(clearVolumes,
			"inherited mounts and volumes must be cleared before the two current secret mounts are rebuilt");
		script.Should().NotContain("legacy_key_volume_flags");
	}

	[Fact]
	public void Provisioning_refuses_to_deploy_over_an_unencrypted_data_protection_key()
	{
		var provisionScript = File.ReadAllText(Path.Combine(SolutionRoot, "docker", "provision.sh"));

		// The service once ran before its certificate secret existed and persisted a plaintext key;
		// adding the certificate later encrypts only subsequent keys, because a key's stored form is
		// fixed at creation. Nothing detected it for five days. Asserting encryption at rest on every
		// deployment is the check that would have.
		provisionScript.Should().Contain("data_protection_key");
		provisionScript.Should().Contain("EncryptedData");
		provisionScript.Should().Contain("unencrypted_key_count");

		// Must run after the schema deploy, or the table it queries does not exist yet.
		var schemaDeploy = provisionScript.IndexOf("JobTrack.Database deploy", StringComparison.Ordinal);
		var keyCheck = provisionScript.IndexOf("verifying data-protection keys", StringComparison.Ordinal);
		keyCheck.Should().BeGreaterThan(schemaDeploy, "the table only exists once the schema is deployed");

		// Counts only: the column holds key material, so it must never reach a log.
		provisionScript.Should().NotContain("SELECT xml");
		provisionScript.Should().NotContain("$unencrypted_key_xml");
	}

	[Fact]
	public void Emergency_recovery_argument_checks_are_not_truncated_by_their_own_usage_message()
	{
		var containerScript = File.ReadAllText(Path.Combine(SolutionRoot, "docker", "emergency-reset.sh"));

		// A '}' inside ${var:?message} closes the expansion at that character, so
		// `${1:?Usage: $0 {password|two-factor} <username>}` evaluated to $1 with a literal ' <username>}'
		// appended. Every invocation reached the mode check as "two-factor <username>}" and was rejected,
		// meaning this recovery path had never once run successfully -- and it is only ever reached when
		// someone is already locked out, so nothing routine would have surfaced it.
		// Executable lines only: the comment above the fix quotes the broken form deliberately, and the
		// character class must exclude newlines or it spans lines and matches almost anything.
		var offendingLines = containerScript
			.Split('\n')
			.Where(line => !line.TrimStart().StartsWith('#'))
			.Where(line => TruncatedParameterExpansion().IsMatch(line))
			.ToArray();

		offendingLines.Should().BeEmpty(
			"a closing brace inside a default-or-error parameter expansion truncates it and corrupts the value; use angle brackets in usage text");
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
