-- Schema version 0021 (PostgreSQL): data_protection_key, ASP.NET Core Data Protection's EF Core key
-- repository table (multi-instance plan, ADR 0066 Stage 2,
-- docs/plans/2026-07-26-multi-instance-web-deployment-plan.md §2.2). Replaces the GCS-mounted
-- filesystem key ring for multi-instance deployments so every host shares one key set; certificate
-- encryption at rest is unchanged -- Program.cs's ProtectKeysWithCertificate still wraps every key's
-- xml column regardless of which repository stores it. Column/table names and shapes are this
-- project's own convention (JobTrackIdentityDbContext), not the framework's PascalCase default.

CREATE TABLE data_protection_key
(
    id            integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    friendly_name text,
    xml           text
);
