-- Schema version 0002 (SQLite): app_user employee-domain profile, ASP.NET
-- Core Identity credential storage (identity_user), Identity role storage
-- (identity_role, identity_user_role), and the 1:1 app_user/identity_user
-- link. See impl plan §6.2 item 2, spec §7.1/§11, spec_claude §6.1/§6.3,
-- ADR 0005, ADR 0006, ADR 0007, ADR 0009.
--
-- lockout_end is a signed 64-bit UTC tick count (100ns ticks since the Unix
-- epoch), per ADR 0007 -- the same encoding as schema_version.applied_at.
-- default_hourly_rate is a canonical fixed-point decimal string (SQLite has
-- no native fixed-precision numeric type; precision is application-enforced
-- per ADR 0009).
--
-- two_factor_enabled/authenticator_key_protected/two_factor_enabled_at: optional TOTP two-factor
-- authentication (ADR 0037). authenticator_key_protected holds the Data-Protection-encrypted TOTP
-- shared secret, never plaintext; two_factor_enabled_at uses the same tick encoding as lockout_end.
--
-- Credential/account data is kept separate from the employee profile per
-- spec §6.1: app_user never carries password hashes, stamps, or lockout
-- state, and identity_user never carries employee-domain profile data.

CREATE TABLE app_user
(
    id                  INTEGER PRIMARY KEY,
    display_name        TEXT NOT NULL CHECK (trim(display_name) <> ''),
    iana_time_zone      TEXT NOT NULL CHECK (trim(iana_time_zone) <> ''),
    default_hourly_rate TEXT CHECK (default_hourly_rate IS NULL OR CAST(default_hourly_rate AS REAL) >= 0),
    row_version         INTEGER NOT NULL DEFAULT 1
) STRICT;

CREATE TABLE identity_user
(
    id                        INTEGER PRIMARY KEY,
    app_user_id               INTEGER NOT NULL UNIQUE REFERENCES app_user (id) ON DELETE RESTRICT,
    user_name                 TEXT NOT NULL CHECK (trim(user_name) <> ''),
    normalized_user_name      TEXT NOT NULL UNIQUE CHECK (trim(normalized_user_name) <> ''),
    password_hash             TEXT NOT NULL,
    security_stamp            TEXT NOT NULL,
    concurrency_stamp         TEXT NOT NULL,
    requires_password_change  INTEGER NOT NULL DEFAULT 1 CHECK (requires_password_change IN (0, 1)),
    is_enabled                INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1)),
    lockout_enabled           INTEGER NOT NULL DEFAULT 1 CHECK (lockout_enabled IN (0, 1)),
    lockout_end               INTEGER,
    access_failed_count       INTEGER NOT NULL DEFAULT 0 CHECK (access_failed_count >= 0),
    two_factor_enabled        INTEGER NOT NULL DEFAULT 0 CHECK (two_factor_enabled IN (0, 1)),
    authenticator_key_protected BLOB,
    two_factor_enabled_at     INTEGER,
    CHECK (two_factor_enabled = 0 OR authenticator_key_protected IS NOT NULL)
) STRICT;

-- Defense-in-depth alongside normalized_user_name's own UNIQUE constraint
-- (PostgreSQL column-type remediation plan §3.4, mirrored here for
-- provider parity for Identity-normalized ASCII names): SQLite's lower()
-- ASCII-folds, which is sufficient for the normalized usernames this schema
-- accepts through the Identity store. This is not intended to claim general
-- Unicode/collation equivalence with PostgreSQL lower().
CREATE UNIQUE INDEX identity_user_normalized_user_name_lower_idx ON identity_user (lower(normalized_user_name));

CREATE TABLE identity_role
(
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
) STRICT;

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
    identity_user_id INTEGER NOT NULL REFERENCES identity_user (id) ON DELETE RESTRICT,
    identity_role_id INTEGER NOT NULL REFERENCES identity_role (id) ON DELETE RESTRICT,
    PRIMARY KEY (identity_user_id, identity_role_id)
) STRICT, WITHOUT ROWID;

CREATE INDEX identity_user_role_identity_role_id_idx ON identity_user_role (identity_role_id);
