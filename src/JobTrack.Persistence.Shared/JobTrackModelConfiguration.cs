namespace JobTrack.Persistence.Shared;

using Converters;
using Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
///     The EF model configuration shared by both providers (impl plan §7.4, ADR 0010 consequences):
///     table/column names, keys, foreign keys, indexes, concurrency-token flags, strongly typed
///     identifier conversions, and enum conversions for the bootstrap-relevant tables (schema versions
///     0001-0004). Deliberately leaves <c>Instant</c>/<c>Money</c>/<c>HourlyRate</c>-typed properties
///     unconverted — each provider's own <c>OnModelCreating</c> calls <see cref="Configure" /> first,
///     then applies its own provider-specific conversion for those properties (native NodaTime
///     mapping and <c>numeric(19,6)</c> on PostgreSQL; UTC-tick and fixed-point-string conversions on
///     SQLite, per ADR 0007/ADR 0009).
/// </summary>
public static class JobTrackModelConfiguration
{
	/// <summary>Applies every provider-agnostic mapping described in the type summary above.</summary>
	public static void Configure(ModelBuilder modelBuilder)
	{
		ConfigureAppUser(modelBuilder);
		ConfigureIdentityUser(modelBuilder);
		ConfigureIdentityRole(modelBuilder);
		ConfigureIdentityUserRole(modelBuilder);
		ConfigureInitialisedMarker(modelBuilder);
		ConfigureJobNode(modelBuilder);
		ConfigureLeafWork(modelBuilder);
		ConfigureWorkSession(modelBuilder);
		ConfigureJobPrerequisite(modelBuilder);
		ConfigureScheduleVersion(modelBuilder);
		ConfigureScheduleInterval(modelBuilder);
		ConfigureScheduleException(modelBuilder);
		ConfigureUserCostRate(modelBuilder);
		ConfigureNodeRateOverride(modelBuilder);
		ConfigureAuditEvent(modelBuilder);
		ConfigurePersonalAccessToken(modelBuilder);
		ConfigureDepartment(modelBuilder);
		ConfigureAppUserDepartment(modelBuilder);
		ConfigureRequestHoldingArea(modelBuilder);
		ConfigureJobRequest(modelBuilder);
		ConfigureJobRequestNote(modelBuilder);
	}

	private static void ConfigureAppUser(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<AppUserEntity>(builder => {
			_ = builder.ToTable("app_user");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.AppUserId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.DisplayName).HasColumnName("display_name").IsRequired();
			_ = builder.Property(e => e.IanaTimeZone).HasColumnName("iana_time_zone").IsRequired();
			_ = builder.Property(e => e.DefaultHourlyRate).HasColumnName("default_hourly_rate");
			_ = builder.Property(e => e.HomeNodeId).HasColumnName("home_node_id").HasConversion(IdValueConverters.NullableJobNodeId);
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.HomeNodeId).OnDelete(DeleteBehavior.SetNull);
		});
	}

	private static void ConfigureIdentityUser(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<IdentityUserEntity>(builder => {
			_ = builder.ToTable("identity_user");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
			_ = builder.Property(e => e.AppUserId).HasColumnName("app_user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.UserName).HasColumnName("user_name").IsRequired();
			_ = builder.Property(e => e.NormalizedUserName).HasColumnName("normalized_user_name").IsRequired();
			_ = builder.Property(e => e.PasswordHash).HasColumnName("password_hash").IsRequired();
			_ = builder.Property(e => e.SecurityStamp).HasColumnName("security_stamp").IsRequired();
			_ = builder.Property(e => e.ConcurrencyStamp).HasColumnName("concurrency_stamp").IsRequired();
			_ = builder.Property(e => e.RequiresPasswordChange).HasColumnName("requires_password_change");
			_ = builder.Property(e => e.IsEnabled).HasColumnName("is_enabled");
			_ = builder.Property(e => e.LockoutEnabled).HasColumnName("lockout_enabled");
			_ = builder.Property(e => e.LockoutEnd).HasColumnName("lockout_end");
			_ = builder.Property(e => e.AccessFailedCount).HasColumnName("access_failed_count");
			_ = builder.Property(e => e.TwoFactorEnabled).HasColumnName("two_factor_enabled");
			_ = builder.Property(e => e.AuthenticatorKeyProtected).HasColumnName("authenticator_key_protected");
			_ = builder.Property(e => e.TwoFactorEnabledAt).HasColumnName("two_factor_enabled_at");

			_ = builder.HasIndex(e => e.AppUserId).IsUnique();
			_ = builder.HasIndex(e => e.NormalizedUserName).IsUnique();

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.AppUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureIdentityRole(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<IdentityRoleEntity>(builder => {
			_ = builder.ToTable("identity_role");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id");
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();

			_ = builder.HasIndex(e => e.Name).IsUnique();
		});
	}

	private static void ConfigureIdentityUserRole(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<IdentityUserRoleEntity>(builder => {
			_ = builder.ToTable("identity_user_role");
			_ = builder.HasKey(e => new { e.IdentityUserId, e.IdentityRoleId });

			_ = builder.Property(e => e.IdentityUserId).HasColumnName("identity_user_id");
			_ = builder.Property(e => e.IdentityRoleId).HasColumnName("identity_role_id");

			_ = builder.HasIndex(e => e.IdentityRoleId).HasDatabaseName("identity_user_role_identity_role_id_idx");

			_ = builder.HasOne<IdentityUserEntity>().WithMany().HasForeignKey(e => e.IdentityUserId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<IdentityRoleEntity>().WithMany().HasForeignKey(e => e.IdentityRoleId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureInitialisedMarker(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<InitialisedMarkerEntity>(builder => {
			_ = builder.ToTable("initialised_marker");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
			_ = builder.Property(e => e.InitialisedAt).HasColumnName("initialised_at");
		});
	}

	private static void ConfigureJobNode(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<JobNodeEntity>(builder => {
			_ = builder.ToTable("job_node");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.JobNodeId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.ParentId).HasColumnName("parent_id").HasConversion(IdValueConverters.NullableJobNodeId);
			_ = builder.Property(e => e.Description).HasColumnName("description").IsRequired();
			_ = builder.Property(e => e.WriteUp).HasColumnName("write_up");
			_ = builder.Property(e => e.PostedByUserId).HasColumnName("posted_by_user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.OwnerUserId).HasColumnName("owner_user_id").HasConversion(IdValueConverters.NullableAppUserId);
			_ = builder.Property(e => e.ExpectedDurationHours).HasColumnName("expected_duration_hours");
			_ = builder.Property(e => e.ExpectedCost).HasColumnName("expected_cost");
			_ = builder.Property(e => e.NeededStart).HasColumnName("needed_start");
			_ = builder.Property(e => e.NeededFinish).HasColumnName("needed_finish");
			_ = builder.Property(e => e.Priority).HasColumnName("priority_id").HasConversion<short>();
			_ = builder.Property(e => e.PostedAt).HasColumnName("posted_at");
			_ = builder.Property(e => e.ArchivedAt).HasColumnName("archived_at");
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => e.ParentId, "job_node_parent_id_idx");
			_ = builder.HasIndex(e => new { e.OwnerUserId, e.ArchivedAt }).HasDatabaseName("job_node_owner_user_id_archived_at_idx");
			_ = builder.HasIndex(e => e.ParentId, "job_node_single_root_idx").IsUnique().HasFilter("parent_id IS NULL");

			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.PostedByUserId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureLeafWork(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<LeafWorkEntity>(builder => {
			_ = builder.ToTable("leaf_work");
			_ = builder.HasKey(e => e.JobNodeId);

			_ = builder.Property(e => e.JobNodeId).HasColumnName("job_node_id").HasConversion(IdValueConverters.JobNodeId)
				.ValueGeneratedNever();
			_ = builder.Property(e => e.Achievement).HasColumnName("achievement_id").HasConversion<short>();
			_ = builder.Property(e => e.PartialCriteria).HasColumnName("partial_criteria");
			_ = builder.Property(e => e.FullCriteria).HasColumnName("full_criteria");
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at");
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasOne<JobNodeEntity>().WithOne().HasForeignKey<LeafWorkEntity>(e => e.JobNodeId)
				.OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureWorkSession(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<WorkSessionEntity>(builder => {
			_ = builder.ToTable("work_session");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.WorkSessionId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.LeafWorkId).HasColumnName("leaf_work_id").HasConversion(IdValueConverters.JobNodeId)
				.IsRequired();
			_ = builder.Property(e => e.WorkedByUserId).HasColumnName("worked_by_user_id").HasConversion(IdValueConverters.AppUserId)
				.IsRequired();
			_ = builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
			_ = builder.Property(e => e.FinishedAt).HasColumnName("finished_at");
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => e.LeafWorkId, "work_session_leaf_work_id_idx");
			_ = builder.HasIndex(e => new { e.WorkedByUserId, e.StartedAt }).HasDatabaseName("work_session_user_started_at_idx");
			_ = builder.HasIndex(e => new { e.WorkedByUserId, e.FinishedAt }).HasDatabaseName("work_session_user_finished_at_idx");

			_ = builder.HasOne<LeafWorkEntity>().WithMany().HasForeignKey(e => e.LeafWorkId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.WorkedByUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureJobPrerequisite(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<JobPrerequisiteEntity>(builder => {
			_ = builder.ToTable("job_prerequisite");
			_ = builder.HasKey(e => new { e.FromId, e.ToId });

			_ = builder.Property(e => e.FromId).HasColumnName("from_id").HasConversion(IdValueConverters.JobNodeId);
			_ = builder.Property(e => e.ToId).HasColumnName("to_id").HasConversion(IdValueConverters.JobNodeId);

			_ = builder.HasIndex(e => e.ToId, "job_prerequisite_to_id_idx");

			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.FromId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.ToId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureScheduleVersion(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<ScheduleVersionEntity>(builder => {
			_ = builder.ToTable("user_schedule_version");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.ScheduleVersionId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.UserId).HasColumnName("user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.EffectiveStart).HasColumnName("effective_start").IsRequired();
			_ = builder.Property(e => e.EffectiveEnd).HasColumnName("effective_end");
			_ = builder.Property(e => e.IanaTimeZone).HasColumnName("iana_time_zone").IsRequired();
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => e.UserId, "user_schedule_version_user_id_idx");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureScheduleInterval(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<ScheduleIntervalEntity>(builder => {
			_ = builder.ToTable("user_schedule_interval");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
			_ = builder.Property(e => e.ScheduleVersionId).HasColumnName("schedule_version_id")
				.HasConversion(IdValueConverters.ScheduleVersionId).IsRequired();
			_ = builder.Property(e => e.DayOfWeek).HasColumnName("day_of_week").HasConversion<short>();
			_ = builder.Property(e => e.StartTime).HasColumnName("start_time").IsRequired();
			_ = builder.Property(e => e.EndTime).HasColumnName("end_time").IsRequired();
			_ = builder.Property(e => e.CrossesMidnight).HasColumnName("crosses_midnight");

			_ = builder.HasIndex(e => e.ScheduleVersionId, "user_schedule_interval_schedule_version_id_idx");

			_ = builder.HasOne<ScheduleVersionEntity>().WithMany().HasForeignKey(e => e.ScheduleVersionId)
				.OnDelete(DeleteBehavior.Cascade);
		});
	}

	private static void ConfigureScheduleException(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<ScheduleExceptionEntity>(builder => {
			_ = builder.ToTable("user_schedule_exception");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.ScheduleExceptionId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.UserId).HasColumnName("user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.StartedAt).HasColumnName("started_at").IsRequired();
			_ = builder.Property(e => e.FinishedAt).HasColumnName("finished_at").IsRequired();
			_ = builder.Property(e => e.ScheduleExceptionEffectId).HasColumnName("effect_id");
			_ = builder.Property(e => e.RateOverride).HasColumnName("rate_override");
			_ = builder.Property(e => e.Reason).HasColumnName("reason").IsRequired();
			_ = builder.Property(e => e.CreatedBy).HasColumnName("created_by").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => new { e.UserId, e.StartedAt }).HasDatabaseName("user_schedule_exception_user_id_started_at_idx");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.CreatedBy).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureUserCostRate(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<UserCostRateEntity>(builder => {
			_ = builder.ToTable("user_cost_rate");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.UserCostRateId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.UserId).HasColumnName("user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.EffectiveStart).HasColumnName("effective_start").IsRequired();
			_ = builder.Property(e => e.EffectiveEnd).HasColumnName("effective_end");
			_ = builder.Property(e => e.Rate).HasColumnName("rate").IsRequired();
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => e.UserId, "user_cost_rate_user_id_idx");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureNodeRateOverride(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<NodeRateOverrideEntity>(builder => {
			_ = builder.ToTable("node_rate_override");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.NodeRateOverrideId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.NodeId).HasColumnName("node_id").HasConversion(IdValueConverters.JobNodeId).IsRequired();
			_ = builder.Property(e => e.UserId).HasColumnName("user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.EffectiveStart).HasColumnName("effective_start").IsRequired();
			_ = builder.Property(e => e.EffectiveEnd).HasColumnName("effective_end");
			_ = builder.Property(e => e.Rate).HasColumnName("rate").IsRequired();
			_ = builder.Property(e => e.ChangedAt).HasColumnName("changed_at").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasIndex(e => new { e.NodeId, e.UserId }).HasDatabaseName("node_rate_override_node_id_user_id_idx");

			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.NodeId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureAuditEvent(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<AuditEventEntity>(builder => {
			_ = builder.ToTable("audit_event");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.AuditEventId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
			_ = builder.Property(e => e.ActorUserId).HasColumnName("actor_user_id").HasConversion(IdValueConverters.NullableAppUserId);
			_ = builder.Property(e => e.Operation).HasColumnName("operation").IsRequired();
			_ = builder.Property(e => e.EntityType).HasColumnName("entity_type").IsRequired();
			_ = builder.Property(e => e.EntityId).HasColumnName("entity_id").IsRequired();
			_ = builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").IsRequired();
			_ = builder.Property(e => e.Reason).HasColumnName("reason");
			_ = builder.Property(e => e.BeforeData).HasColumnName("before_data");
			_ = builder.Property(e => e.AfterData).HasColumnName("after_data");

			_ = builder.HasIndex(e => new { e.EntityType, e.EntityId }).HasDatabaseName("audit_event_entity_type_entity_id_idx");
			_ = builder.HasIndex(e => e.CorrelationId).HasDatabaseName("audit_event_correlation_id_idx");
			_ = builder.HasIndex(e => new { e.ActorUserId, e.OccurredAt }).HasDatabaseName("audit_event_actor_user_id_occurred_at_idx");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigurePersonalAccessToken(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<PersonalAccessTokenEntity>(builder => {
			_ = builder.ToTable("personal_access_token");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.PersonalAccessTokenId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.AppUserId).HasColumnName("app_user_id").HasConversion(IdValueConverters.AppUserId).IsRequired();
			_ = builder.Property(e => e.TokenHash).HasColumnName("token_hash").IsRequired();
			_ = builder.Property(e => e.Label).HasColumnName("label").IsRequired();
			_ = builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
			_ = builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();
			_ = builder.Property(e => e.RevokedAt).HasColumnName("revoked_at");
			_ = builder.Property(e => e.LastUsedAt).HasColumnName("last_used_at");

			_ = builder.HasIndex(e => e.TokenHash, "personal_access_token_token_hash_idx").IsUnique();
			_ = builder.HasIndex(e => e.AppUserId, "personal_access_token_app_user_id_idx");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.AppUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureDepartment(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<DepartmentEntity>(builder => {
			_ = builder.ToTable("department");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.DepartmentId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();
			_ = builder.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();
		});
	}

	private static void ConfigureAppUserDepartment(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<AppUserDepartmentEntity>(builder => {
			_ = builder.ToTable("app_user_department");
			_ = builder.HasKey(e => new { e.AppUserId, e.DepartmentId });

			_ = builder.Property(e => e.AppUserId).HasColumnName("app_user_id").HasConversion(IdValueConverters.AppUserId);
			_ = builder.Property(e => e.DepartmentId).HasColumnName("department_id").HasConversion(IdValueConverters.DepartmentId);
			_ = builder.Property(e => e.IsPrimary).HasColumnName("is_primary");

			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.AppUserId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<DepartmentEntity>().WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureRequestHoldingArea(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<RequestHoldingAreaEntity>(builder => {
			_ = builder.ToTable("request_holding_area");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.RequestHoldingAreaId)
				.ValueGeneratedOnAdd();
			_ = builder.Property(e => e.JobNodeId).HasColumnName("job_node_id").HasConversion(IdValueConverters.JobNodeId).IsRequired();
			_ = builder.Property(e => e.DepartmentId).HasColumnName("department_id").HasConversion(IdValueConverters.NullableDepartmentId);
			_ = builder.Property(e => e.Name).HasColumnName("name").IsRequired();
			_ = builder.Property(e => e.DefaultPriority).HasColumnName("default_priority_id").HasConversion<short>();
			_ = builder.Property(e => e.DefaultOwnerUserId).HasColumnName("default_owner_user_id")
				.HasConversion(IdValueConverters.NullableAppUserId);
			_ = builder.Property(e => e.IsActive).HasColumnName("is_active").IsRequired();
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasOne<JobNodeEntity>().WithMany().HasForeignKey(e => e.JobNodeId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<DepartmentEntity>().WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.DefaultOwnerUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureJobRequest(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<JobRequestEntity>(builder => {
			_ = builder.ToTable("job_request");
			_ = builder.HasKey(e => e.JobNodeId);

			_ = builder.Property(e => e.JobNodeId).HasColumnName("job_node_id").HasConversion(IdValueConverters.JobNodeId);
			_ = builder.Property(e => e.RequesterUserId).HasColumnName("requester_user_id").HasConversion(IdValueConverters.AppUserId)
				.IsRequired();
			_ = builder.Property(e => e.HoldingAreaId).HasColumnName("holding_area_id")
				.HasConversion(IdValueConverters.RequestHoldingAreaId).IsRequired();
			_ = builder.Property(e => e.RequesterReference).HasColumnName("requester_reference");
			_ = builder.Property(e => e.SubmittedAt).HasColumnName("submitted_at").IsRequired();
			_ = builder.Property(e => e.ClosedToRequesterAt).HasColumnName("closed_to_requester_at");
			_ = builder.Property(e => e.AcknowledgedAt).HasColumnName("acknowledged_at");
			_ = builder.Property(e => e.AcknowledgedByUserId).HasColumnName("acknowledged_by_user_id")
				.HasConversion(IdValueConverters.NullableAppUserId);
			_ = builder.Property(e => e.RowVersion).HasColumnName("row_version").IsConcurrencyToken();

			_ = builder.HasOne<JobNodeEntity>().WithOne().HasForeignKey<JobRequestEntity>(e => e.JobNodeId)
				.OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<RequestHoldingAreaEntity>().WithMany().HasForeignKey(e => e.HoldingAreaId)
				.OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.AcknowledgedByUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}

	private static void ConfigureJobRequestNote(ModelBuilder modelBuilder)
	{
		_ = modelBuilder.Entity<JobRequestNoteEntity>(builder => {
			_ = builder.ToTable("job_request_note");
			_ = builder.HasKey(e => e.Id);

			_ = builder.Property(e => e.Id).HasColumnName("id").HasConversion(IdValueConverters.JobRequestNoteId).ValueGeneratedOnAdd();
			_ = builder.Property(e => e.JobNodeId).HasColumnName("job_node_id").HasConversion(IdValueConverters.JobNodeId).IsRequired();
			_ = builder.Property(e => e.AuthorUserId).HasColumnName("author_user_id").HasConversion(IdValueConverters.AppUserId)
				.IsRequired();
			_ = builder.Property(e => e.Content).HasColumnName("content").IsRequired();
			_ = builder.Property(e => e.IsVisibleToRequester).HasColumnName("is_visible_to_requester").IsRequired();
			_ = builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();

			_ = builder.HasOne<JobRequestEntity>().WithMany().HasForeignKey(e => e.JobNodeId).OnDelete(DeleteBehavior.Restrict);
			_ = builder.HasOne<AppUserEntity>().WithMany().HasForeignKey(e => e.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
		});
	}
}
