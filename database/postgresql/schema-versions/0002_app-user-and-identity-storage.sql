-- Schema version 0002 (PostgreSQL): app_user employee-domain profile,
-- ASP.NET Core Identity credential storage (identity_user), Identity role
-- storage (identity_role, identity_user_role), and the 1:1 app_user/
-- identity_user link. See impl plan §6.2 item 2, spec §7.1/§11,
-- spec_claude §6.1/§6.3, ADR 0005, ADR 0006, ADR 0009.
--
-- two_factor_enabled/authenticator_key_protected/two_factor_enabled_at: optional TOTP two-factor
-- authentication (ADR 0037). authenticator_key_protected holds the Data-Protection-encrypted TOTP
-- shared secret, never plaintext.
--
-- Credential/account data is kept separate from the employee profile per
-- spec §6.1: app_user never carries password hashes, stamps, or lockout
-- state, and identity_user never carries employee-domain profile data.

CREATE TABLE app_user
(
    id                  bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    display_name        text NOT NULL,
    iana_time_zone      text NOT NULL,
    default_hourly_rate numeric(19, 6),
    row_version         bigint NOT NULL DEFAULT 1,
    CONSTRAINT app_user_display_name_not_blank CHECK (btrim(display_name) <> ''),
    CONSTRAINT app_user_iana_time_zone_not_blank CHECK (btrim(iana_time_zone) <> ''),
    CONSTRAINT app_user_default_hourly_rate_non_negative CHECK (default_hourly_rate IS NULL OR default_hourly_rate >= 0)
);

CREATE TABLE identity_user
(
    id                        bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    app_user_id               bigint NOT NULL UNIQUE REFERENCES app_user (id) ON DELETE RESTRICT,
    user_name                 text NOT NULL,
    normalized_user_name      text NOT NULL UNIQUE,
    password_hash             text NOT NULL,
    security_stamp            text NOT NULL,
    concurrency_stamp         text NOT NULL,
    requires_password_change  boolean NOT NULL DEFAULT true,
    is_enabled                boolean NOT NULL DEFAULT true,
    lockout_enabled           boolean NOT NULL DEFAULT true,
    lockout_end               timestamptz,
    access_failed_count       integer NOT NULL DEFAULT 0,
    two_factor_enabled        boolean NOT NULL DEFAULT false,
    authenticator_key_protected bytea,
    two_factor_enabled_at     timestamptz,
    CONSTRAINT identity_user_user_name_not_blank CHECK (btrim(user_name) <> ''),
    CONSTRAINT identity_user_normalized_user_name_not_blank CHECK (btrim(normalized_user_name) <> ''),
    CONSTRAINT identity_user_access_failed_count_non_negative CHECK (access_failed_count >= 0),
    CONSTRAINT identity_user_two_factor_key_present_when_enabled CHECK (NOT two_factor_enabled OR authenticator_key_protected IS NOT NULL)
);

-- Defense-in-depth alongside normalized_user_name's own UNIQUE constraint
-- (PostgreSQL column-type remediation plan §3.4): normalized_user_name is
-- expected to already be case-folded by ASP.NET Core Identity's
-- ILookupNormalizer before insert, but nothing at the schema layer enforces
-- that -- a caller bypassing the normalizer (a bug, a raw SQL insert, a
-- future store implementation) could insert "ALICE" and "alice" as two
-- distinct rows. A unique expression index on lower(normalized_user_name)
-- closes that gap without weakening normalized_user_name's own UNIQUE
-- constraint, which stays for ASP.NET Core Identity compatibility. An
-- expression index is used instead of citext to avoid introducing a second
-- text type with its own EF/SQLite equivalence questions for a single
-- column.
CREATE UNIQUE INDEX identity_user_normalized_user_name_lower_idx ON identity_user (lower(normalized_user_name));

CREATE TABLE identity_role
(
    id   smallint PRIMARY KEY,
    name text NOT NULL UNIQUE
);

-- spec_claude §6.3: the six baseline authorization roles, in the order
-- listed there, plus Requester (ADR 0033), the low-privilege client/
-- end-user role added for requester intake.
INSERT INTO identity_role (id, name)
VALUES (1, 'Administrator'),
       (2, 'Job manager'),
       (3, 'Worker'),
       (4, 'Rate manager'),
       (5, 'Cost viewer'),
       (6, 'Auditor'),
       (7, 'Requester');

CREATE TABLE identity_user_role
(
    identity_user_id bigint   NOT NULL REFERENCES identity_user (id) ON DELETE RESTRICT,
    identity_role_id smallint NOT NULL REFERENCES identity_role (id) ON DELETE RESTRICT,
    PRIMARY KEY (identity_user_id, identity_role_id)
);

CREATE INDEX identity_user_role_identity_role_id_idx ON identity_user_role (identity_role_id);
