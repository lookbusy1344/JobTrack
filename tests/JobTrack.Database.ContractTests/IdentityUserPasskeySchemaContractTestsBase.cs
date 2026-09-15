namespace JobTrack.Database.ContractTests;

using System.Data.Common;
using System.Globalization;
using Abstractions;
using AwesomeAssertions;
using TestSupport;

/// <summary>
///     Shared TC-DB-AUTHN-001 contract for the <c>identity_user_passkey</c> table and
///     <c>identity_user.passkey_user_handle</c> column (ADR 0071, plan §6), asserted identically
///     against PostgreSQL and SQLite by <see cref="PostgreSqlIdentityUserPasskeySchemaTests" /> and
///     <see cref="SqliteIdentityUserPasskeySchemaTests" />.
/// </summary>
public abstract class IdentityUserPasskeySchemaContractTestsBase : IAsyncLifetime
{
	private const string ApplicationVersion = "1.2.3";
	private const string AppliedBy = "test-runner";

	private readonly IDisposableTestDatabase database;

	protected IdentityUserPasskeySchemaContractTestsBase(IDisposableTestDatabase database) => this.database = database;

	protected abstract SchemaProvider Provider { get; }

	public Task InitializeAsync() => database.InitializeAsync();

	public Task DisposeAsync() => database.DisposeAsync();

	[Fact]
	public async Task Deploying_creates_an_empty_identity_user_passkey_table()
	{
		await using var connection = await OpenDeployedConnectionAsync();

		(await CountRowsAsync(connection)).Should().Be(0);
	}

	[Fact]
	public async Task Inserting_a_passkey_succeeds()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");

		var act = async () => await InsertPasskeyAsync(connection, userId, [1, 2, 3], "Work MacBook", "WORK MACBOOK");

		await act.Should().NotThrowAsync();
		(await CountRowsAsync(connection)).Should().Be(1);
	}

	[Fact]
	public async Task Inserting_a_passkey_with_a_blank_name_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");

		var act = async () => await InsertPasskeyAsync(connection, userId, [1], "   ", "   ");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_passkey_whose_name_exceeds_one_hundred_code_points_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");
		var tooLong = new string('a', 101);

		var act = async () => await InsertPasskeyAsync(connection, userId, [1], tooLong, tooLong.ToUpperInvariant());

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_passkey_that_is_not_user_verified_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");

		var act = async () => await InsertPasskeyAsync(connection, userId, [1], "Work MacBook", "WORK MACBOOK", isUserVerified: false);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_passkey_with_a_sign_count_above_the_unsigned_thirty_two_bit_range_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");

		var act = async () => await InsertPasskeyAsync(connection, userId, [1], "Work MacBook", "WORK MACBOOK", 4_294_967_296L);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_a_passkey_whose_aaguid_is_not_sixteen_bytes_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");

		var act = async () => await InsertPasskeyAsync(connection, userId, [1], "Work MacBook", "WORK MACBOOK", aaguid: [0, 1, 2, 3]);

		await act.Should().ThrowAsync<DbException>();
	}

	[Theory]
	[InlineData("credential-id")]
	[InlineData("public-key")]
	[InlineData("transports")]
	[InlineData("attestation")]
	[InlineData("client-data")]
	public async Task Inserting_oversized_passkey_material_is_rejected(string field)
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "bounded");
		var credentialId = field == "credential-id"
			? new byte[PasskeyPolicy.MaximumCredentialIdByteLength + 1]
			: new byte[] {
				1,
			};
		var publicKey = field == "public-key"
			? new byte[PasskeyPolicy.MaximumPublicKeyByteLength + 1]
			: new byte[] {
				1,
			};
		var transports = field == "transports" ? new string('x', PasskeyPolicy.MaximumTransportsLength + 1) : null;
		var attestation = field == "attestation"
			? new byte[PasskeyPolicy.MaximumAttestationObjectByteLength + 1]
			: new byte[] {
				1,
			};
		var clientData = field == "client-data"
			? new byte[PasskeyPolicy.MaximumClientDataJsonByteLength + 1]
			: new byte[] {
				1,
			};

		var act = async () => await InsertPasskeyAsync(
			connection, userId, credentialId, "Bounded", "BOUNDED", publicKey: publicKey,
			transports: transports, attestationObject: attestation, clientDataJson: clientData);

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Setting_a_user_handle_with_the_wrong_encoded_length_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "handle-length");

		var act = async () => await SetPasskeyUserHandleAsync(connection, userId, "too-short");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_two_passkeys_with_the_same_credential_id_is_rejected_even_across_accounts()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var alice = await SeedIdentityUserAsync(connection, "alice");
		var bob = await SeedIdentityUserAsync(connection, "bob");
		await InsertPasskeyAsync(connection, alice, [9, 9, 9], "Alice key", "ALICE KEY");

		var act = async () => await InsertPasskeyAsync(connection, bob, [9, 9, 9], "Bob key", "BOB KEY");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task Inserting_two_passkeys_with_the_same_normalized_name_for_one_account_is_rejected()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var userId = await SeedIdentityUserAsync(connection, "alice");
		await InsertPasskeyAsync(connection, userId, [1], "Work MacBook", "WORK MACBOOK");

		var act = async () => await InsertPasskeyAsync(connection, userId, [2], "work macbook", "WORK MACBOOK");

		await act.Should().ThrowAsync<DbException>();
	}

	[Fact]
	public async Task The_same_normalized_name_is_allowed_for_two_different_accounts()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var alice = await SeedIdentityUserAsync(connection, "alice");
		var bob = await SeedIdentityUserAsync(connection, "bob");
		await InsertPasskeyAsync(connection, alice, [1], "Laptop", "LAPTOP");

		var act = async () => await InsertPasskeyAsync(connection, bob, [2], "Laptop", "LAPTOP");

		await act.Should().NotThrowAsync();
	}

	[Fact]
	public async Task Two_accounts_cannot_share_a_passkey_user_handle()
	{
		await using var connection = await OpenDeployedConnectionAsync();
		var alice = await SeedIdentityUserAsync(connection, "alice");
		const string sharedHandle = "DrJ9h667yZ-DAlq0kUrjdacbo-95j_Rn04XwuW6O1Yw";
		await SetPasskeyUserHandleAsync(connection, alice, sharedHandle);
		var bob = await SeedIdentityUserAsync(connection, "bob");

		var act = async () => await SetPasskeyUserHandleAsync(connection, bob, sharedHandle);

		await act.Should().ThrowAsync<DbException>();
	}

	protected abstract DbConnection CreateConnection(string connectionString);

	protected abstract ISchemaVersionStore CreateStore();

	protected abstract IDeploymentLockStrategy CreateLockStrategy();

	protected abstract Task PrepareConnectionAsync(DbConnection connection);

	/// <summary>Provider-neutral defaults for every column the test does not vary.</summary>
	protected abstract Task InsertPasskeyAsync(
		DbConnection connection,
		long identityUserId,
		byte[] credentialId,
		string name,
		string normalizedName,
		long signCount = 0,
		bool isUserVerified = true,
		byte[]? aaguid = null,
		byte[]? publicKey = null,
		string? transports = null,
		byte[]? attestationObject = null,
		byte[]? clientDataJson = null);

	private async Task<DbConnection> OpenDeployedConnectionAsync()
	{
		var connection = CreateConnection(database.ConnectionString);
		await connection.OpenAsync();
		await PrepareConnectionAsync(connection);

		var scripts = SchemaVersionScriptLoader.Load(RepositoryPaths.SchemaVersionsDirectory(Provider));
		var deployer = new SchemaDeployer(connection, CreateStore(), CreateLockStrategy(), ApplicationVersion, AppliedBy);
		await deployer.DeployAsync(scripts, CancellationToken.None);

		return connection;
	}

	private static async Task<long> SeedIdentityUserAsync(DbConnection connection, string userName)
	{
		await using var appUserCommand = connection.CreateCommand();
		appUserCommand.CommandText = """
									 INSERT INTO app_user (display_name, iana_time_zone)
									 VALUES (@displayName, 'Europe/London')
									 RETURNING id;
									 """;
		AddParameter(appUserCommand, "@displayName", userName);
		var appUserId = Convert.ToInt64(await appUserCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

		await using var identityCommand = connection.CreateCommand();
		identityCommand.CommandText = """
									  INSERT INTO identity_user
									      (app_user_id, user_name, normalized_user_name, password_hash, security_stamp, concurrency_stamp)
									  VALUES (@appUserId, @userName, @normalized, 'hash', 'stamp', 'concurrency')
									  RETURNING id;
									  """;
		AddParameter(identityCommand, "@appUserId", appUserId);
		AddParameter(identityCommand, "@userName", userName);
		AddParameter(identityCommand, "@normalized", userName.ToUpperInvariant());
		return Convert.ToInt64(await identityCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	private static async Task SetPasskeyUserHandleAsync(DbConnection connection, long identityUserId, string handle)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "UPDATE identity_user SET passkey_user_handle = @handle WHERE id = @id;";
		AddParameter(command, "@handle", handle);
		AddParameter(command, "@id", identityUserId);
		_ = await command.ExecuteNonQueryAsync();
	}

	private static async Task<long> CountRowsAsync(DbConnection connection)
	{
		await using var command = connection.CreateCommand();
		command.CommandText = "SELECT COUNT(*) FROM identity_user_passkey;";
		return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
	}

	protected static void AddParameter(DbCommand command, string name, object value)
	{
		var parameter = command.CreateParameter();
		parameter.ParameterName = name;
		parameter.Value = value;
		command.Parameters.Add(parameter);
	}
}
