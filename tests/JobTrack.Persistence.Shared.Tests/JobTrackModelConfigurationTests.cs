namespace JobTrack.Persistence.Shared.Tests;

using Abstractions;
using AwesomeAssertions;
using Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
///     Model-to-schema shape tests (impl plan §7.4, ADR 0010 consequences): asserts the shared EF
///     model configuration produces the exact table/column/key/index shape of database schema
///     versions 0001-0004, for both providers, without opening a database connection. Deep
///     database-backed conformance (real column types via introspection, round-tripping conversions)
///     belongs to each provider's own test suite once its <c>DbContext</c> exists.
/// </summary>
public sealed class JobTrackModelConfigurationTests
{
	private static IEnumerable<DbContext> BothProviders()
	{
		yield return new PostgreSqlModelTestDbContext();
		yield return new SqliteModelTestDbContext();
	}

	[Theory]
	[InlineData(typeof(AppUserEntity), "app_user")]
	[InlineData(typeof(IdentityUserEntity), "identity_user")]
	[InlineData(typeof(IdentityRoleEntity), "identity_role")]
	[InlineData(typeof(IdentityUserRoleEntity), "identity_user_role")]
	[InlineData(typeof(InitialisedMarkerEntity), "initialised_marker")]
	[InlineData(typeof(JobNodeEntity), "job_node")]
	[InlineData(typeof(LeafWorkEntity), "leaf_work")]
	[InlineData(typeof(WorkSessionEntity), "work_session")]
	[InlineData(typeof(JobPrerequisiteEntity), "job_prerequisite")]
	public void Entity_maps_to_expected_table_name_on_both_providers(Type entityType, string expectedTableName)
	{
		foreach (var context in BothProviders()) {
			using (context) {
				var entity = context.Model.FindEntityType(entityType);
				entity.Should().NotBeNull();
				entity!.GetTableName().Should().Be(expectedTableName);
			}
		}
	}

	[Fact]
	public void AppUserEntity_maps_expected_columns_on_both_providers()
	{
		string[] expectedColumns = [
			"id", "display_name", "iana_time_zone", "default_hourly_rate", "home_node_id", "row_version",
		];

		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<AppUserEntity>(context, expectedColumns);

				var rowVersion = GetProperty<AppUserEntity>(context, nameof(AppUserEntity.RowVersion));
				rowVersion.IsConcurrencyToken.Should().BeTrue();

				var displayName = GetProperty<AppUserEntity>(context, nameof(AppUserEntity.DisplayName));
				displayName.IsNullable.Should().BeFalse();

				var defaultHourlyRate = GetProperty<AppUserEntity>(context, nameof(AppUserEntity.DefaultHourlyRate));
				defaultHourlyRate.IsNullable.Should().BeTrue();

				var homeNodeId = GetProperty<AppUserEntity>(context, nameof(AppUserEntity.HomeNodeId));
				homeNodeId.IsNullable.Should().BeTrue();
			}
		}
	}

	[Fact]
	public void IdentityUserEntity_maps_expected_columns_and_one_to_one_link_on_both_providers()
	{
		string[] expectedColumns = [
			"id", "app_user_id", "user_name", "normalized_user_name", "password_hash", "security_stamp",
			"concurrency_stamp", "requires_password_change", "is_enabled", "lockout_enabled", "lockout_end",
			"access_failed_count", "two_factor_enabled", "authenticator_key_protected", "two_factor_enabled_at",
		];

		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<IdentityUserEntity>(context, expectedColumns);

				var entity = context.Model.FindEntityType(typeof(IdentityUserEntity))!;
				var appUserIdIndex = entity.GetIndexes().SingleOrDefault(i => i.Properties.Count == 1 &&
																			  i.Properties[0].Name == nameof(IdentityUserEntity.AppUserId));
				appUserIdIndex.Should().NotBeNull();
				appUserIdIndex!.IsUnique.Should().BeTrue();

				var normalizedUserNameIndex = entity.GetIndexes().SingleOrDefault(i => i.Properties.Count == 1 &&
																					   i.Properties[0].Name ==
																					   nameof(IdentityUserEntity.NormalizedUserName));
				normalizedUserNameIndex.Should().NotBeNull();
				normalizedUserNameIndex!.IsUnique.Should().BeTrue();

				var foreignKey = entity.GetForeignKeys().SingleOrDefault(fk =>
					fk.PrincipalEntityType.ClrType == typeof(AppUserEntity));
				foreignKey.Should().NotBeNull();
				foreignKey!.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
			}
		}
	}

	[Fact]
	public void IdentityRoleEntity_and_IdentityUserRoleEntity_map_expected_shape_on_both_providers()
	{
		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<IdentityRoleEntity>(context, ["id", "name"]);
				AssertColumnNames<IdentityUserRoleEntity>(context, ["identity_user_id", "identity_role_id"]);

				var joinEntity = context.Model.FindEntityType(typeof(IdentityUserRoleEntity))!;
				joinEntity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().BeEquivalentTo(
					nameof(IdentityUserRoleEntity.IdentityUserId), nameof(IdentityUserRoleEntity.IdentityRoleId));

				var roleIndex = joinEntity.GetIndexes().SingleOrDefault(i => i.Properties.Count == 1 &&
																			 i.Properties[0].Name == nameof(IdentityUserRoleEntity.IdentityRoleId));
				roleIndex.Should().NotBeNull();
				roleIndex!.GetDatabaseName().Should().Be("identity_user_role_identity_role_id_idx");

				joinEntity.GetForeignKeys().Should().HaveCount(2);
			}
		}
	}

	[Fact]
	public void InitialisedMarkerEntity_maps_expected_columns_with_no_concurrency_token_on_both_providers()
	{
		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<InitialisedMarkerEntity>(context, ["id", "initialised_at"]);

				var entity = context.Model.FindEntityType(typeof(InitialisedMarkerEntity))!;
				entity.GetProperties().Should().NotContain(p => p.IsConcurrencyToken);
			}
		}
	}

	[Fact]
	public void JobNodeEntity_maps_expected_columns_indexes_and_foreign_keys_on_both_providers()
	{
		string[] expectedColumns = [
			"id", "parent_id", "description", "write_up", "posted_by_user_id", "owner_user_id",
			"expected_duration_hours", "expected_cost", "needed_start", "needed_finish", "priority_id",
			"posted_at", "archived_at", "row_version",
		];

		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<JobNodeEntity>(context, expectedColumns);

				var entity = context.Model.FindEntityType(typeof(JobNodeEntity))!;

				var rowVersion = entity.GetProperties().Single(p => p.Name == nameof(JobNodeEntity.RowVersion));
				rowVersion.IsConcurrencyToken.Should().BeTrue();

				entity.GetForeignKeys().Should().HaveCount(3, "self-reference plus posted-by and owner FKs to app_user");
				entity.GetForeignKeys().Count(fk => fk.PrincipalEntityType.ClrType == typeof(AppUserEntity)).Should().Be(2);
				entity.GetForeignKeys().Count(fk => fk.PrincipalEntityType.ClrType == typeof(JobNodeEntity)).Should().Be(1);

				var parentIdIndex = entity.GetIndexes().SingleOrDefault(i =>
					i.GetDatabaseName() == "job_node_parent_id_idx");
				parentIdIndex.Should().NotBeNull();

				var ownerArchivedIndex = entity.GetIndexes().SingleOrDefault(i =>
					i.GetDatabaseName() == "job_node_owner_user_id_archived_at_idx");
				ownerArchivedIndex.Should().NotBeNull();
				ownerArchivedIndex!.Properties.Select(p => p.Name).Should().Equal(
					nameof(JobNodeEntity.OwnerUserId), nameof(JobNodeEntity.ArchivedAt));

				var singleRootIndex = entity.GetIndexes().SingleOrDefault(i =>
					i.GetDatabaseName() == "job_node_single_root_idx");
				singleRootIndex.Should().NotBeNull();
				singleRootIndex!.IsUnique.Should().BeTrue();
				singleRootIndex.GetFilter().Should().Be("parent_id IS NULL");

				var priorityId = entity.GetProperties().Single(p => p.Name == nameof(JobNodeEntity.Priority));
				priorityId.GetColumnName().Should().Be("priority_id");
				priorityId.ClrType.Should().Be<Priority>();
			}
		}
	}

	[Fact]
	public void Database_generated_keys_are_configured_correctly_on_both_providers()
	{
		foreach (var context in BothProviders()) {
			using (context) {
				GetProperty<AppUserEntity>(context, nameof(AppUserEntity.Id)).ValueGenerated.Should().Be(ValueGenerated.OnAdd);
				GetProperty<IdentityUserEntity>(context, nameof(IdentityUserEntity.Id)).ValueGenerated.Should().Be(ValueGenerated.OnAdd);
				GetProperty<JobNodeEntity>(context, nameof(JobNodeEntity.Id)).ValueGenerated.Should().Be(ValueGenerated.OnAdd);
				GetProperty<InitialisedMarkerEntity>(context, nameof(InitialisedMarkerEntity.Id)).ValueGenerated.Should().Be(ValueGenerated.Never);
				GetProperty<LeafWorkEntity>(context, nameof(LeafWorkEntity.JobNodeId)).ValueGenerated.Should().Be(ValueGenerated.Never);
				GetProperty<WorkSessionEntity>(context, nameof(WorkSessionEntity.Id)).ValueGenerated.Should().Be(ValueGenerated.OnAdd);
			}
		}
	}

	[Fact]
	public void LeafWorkEntity_maps_expected_columns_key_and_foreign_key_on_both_providers()
	{
		string[] expectedColumns = ["job_node_id", "achievement_id", "partial_criteria", "full_criteria", "changed_at", "row_version"];

		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<LeafWorkEntity>(context, expectedColumns);

				var entity = context.Model.FindEntityType(typeof(LeafWorkEntity))!;

				entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(nameof(LeafWorkEntity.JobNodeId));

				var rowVersion = GetProperty<LeafWorkEntity>(context, nameof(LeafWorkEntity.RowVersion));
				rowVersion.IsConcurrencyToken.Should().BeTrue();

				var foreignKey = entity.GetForeignKeys().Single();
				foreignKey.PrincipalEntityType.ClrType.Should().Be<JobNodeEntity>();
				foreignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

				var achievementId = GetProperty<LeafWorkEntity>(context, nameof(LeafWorkEntity.Achievement));
				achievementId.GetColumnName().Should().Be("achievement_id");
				achievementId.ClrType.Should().Be<Achievement>();
			}
		}
	}

	[Fact]
	public void WorkSessionEntity_maps_expected_columns_indexes_and_foreign_keys_on_both_providers()
	{
		string[] expectedColumns = [
			"id", "leaf_work_id", "worked_by_user_id", "started_at", "finished_at", "changed_at", "row_version",
		];

		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<WorkSessionEntity>(context, expectedColumns);

				var entity = context.Model.FindEntityType(typeof(WorkSessionEntity))!;

				var rowVersion = GetProperty<WorkSessionEntity>(context, nameof(WorkSessionEntity.RowVersion));
				rowVersion.IsConcurrencyToken.Should().BeTrue();

				entity.GetForeignKeys().Should().HaveCount(2, "leaf-work and worked-by-user FKs");
				entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(LeafWorkEntity))
					.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
				entity.GetForeignKeys().Single(fk => fk.PrincipalEntityType.ClrType == typeof(AppUserEntity))
					.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);

				var leafWorkIdIndex = entity.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == "work_session_leaf_work_id_idx");
				leafWorkIdIndex.Should().NotBeNull();

				var userStartedIndex = entity.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == "work_session_user_started_at_idx");
				userStartedIndex.Should().NotBeNull();
				userStartedIndex!.Properties.Select(p => p.Name).Should().Equal(
					nameof(WorkSessionEntity.WorkedByUserId), nameof(WorkSessionEntity.StartedAt));

				var userFinishedIndex = entity.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == "work_session_user_finished_at_idx");
				userFinishedIndex.Should().NotBeNull();
				userFinishedIndex!.Properties.Select(p => p.Name).Should().Equal(
					nameof(WorkSessionEntity.WorkedByUserId), nameof(WorkSessionEntity.FinishedAt));
			}
		}
	}

	[Fact]
	public void JobPrerequisiteEntity_maps_expected_columns_key_index_and_foreign_keys_on_both_providers()
	{
		foreach (var context in BothProviders()) {
			using (context) {
				AssertColumnNames<JobPrerequisiteEntity>(context, ["from_id", "to_id"]);

				var entity = context.Model.FindEntityType(typeof(JobPrerequisiteEntity))!;

				entity.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal(
					nameof(JobPrerequisiteEntity.FromId), nameof(JobPrerequisiteEntity.ToId));

				entity.GetForeignKeys().Should().HaveCount(2, "from-id and to-id FKs to job_node");
				entity.GetForeignKeys().Should().OnlyContain(fk =>
					fk.PrincipalEntityType.ClrType == typeof(JobNodeEntity) && fk.DeleteBehavior == DeleteBehavior.Restrict);

				var toIdIndex = entity.GetIndexes().SingleOrDefault(i => i.GetDatabaseName() == "job_prerequisite_to_id_idx");
				toIdIndex.Should().NotBeNull();
			}
		}
	}

	private static void AssertColumnNames<TEntity>(DbContext context, IReadOnlyCollection<string> expectedColumns)
		where TEntity : class
	{
		var entity = context.Model.FindEntityType(typeof(TEntity));
		entity.Should().NotBeNull();
		entity!.GetProperties().Select(p => p.GetColumnName()).Should().BeEquivalentTo(expectedColumns);
	}

	private static IProperty GetProperty<TEntity>(DbContext context, string propertyName)
		where TEntity : class
	{
		var entity = context.Model.FindEntityType(typeof(TEntity))!;
		return entity.GetProperties().Single(p => p.Name == propertyName);
	}
}
