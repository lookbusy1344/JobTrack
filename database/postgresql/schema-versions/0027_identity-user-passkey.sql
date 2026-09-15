-- Schema version 0027 (PostgreSQL): passkey (WebAuthn) credential storage
-- (ADR 0071, plan §6). Passkeys are an optional primary sign-in method
-- alongside the retained password. Credential material lives under the
-- credential boundary, isolated from the employee profile and never exposed
-- to reporting or ordinary domain queries (spec §7.1).
--
-- passkey_user_handle: a stable, random, non-PII WebAuthn user handle (32
-- cryptographically random bytes, base64url-encoded as text). Nullable for
-- accounts that predate passkeys until first enrolment; populated at
-- new-account creation. Never derived from username, display name, email, or
-- a hash of guessable PII. UNIQUE so it is a durable per-account namespace.

ALTER TABLE identity_user
    ADD COLUMN passkey_user_handle text UNIQUE
        CONSTRAINT identity_user_passkey_user_handle_not_blank CHECK (passkey_user_handle IS NULL OR btrim(passkey_user_handle) <> '');

-- identity_user_passkey: one row per enrolled WebAuthn credential.
--   credential_id      -- bounded binary WebAuthn credential ID; globally
--                         unique (primary key), so one credential cannot
--                         attach to two accounts.
--   name/normalized_name -- required friendly name (1-100 Unicode code
--                         points), unique per account. normalized_name is
--                         derived once with invariant ToUpperInvariant in the
--                         application; the unique constraint compares that
--                         stored value exactly and never relies on lower(),
--                         NOCASE, or database locale (ADR 0071 §3).
--   sign_count         -- unsigned 32-bit logical range in a provider-neutral
--                         bigint; never lowered below a non-zero value.
--   is_user_verified   -- registration rejects false (userVerification =
--                         required); enforced by a CHECK as defense in depth.
--   aaguid             -- fixed 16-byte authenticator identifier; never an
--                         authorization input.
CREATE TABLE identity_user_passkey
(
    credential_id      bytea       NOT NULL PRIMARY KEY,
    identity_user_id   bigint      NOT NULL REFERENCES identity_user (id) ON DELETE RESTRICT,
    name               text        NOT NULL,
    normalized_name    text        NOT NULL,
    public_key         bytea       NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now(),
    sign_count         bigint      NOT NULL,
    transports         text,
    is_user_verified   boolean     NOT NULL,
    is_backup_eligible boolean     NOT NULL,
    is_backed_up       boolean     NOT NULL,
    aaguid             bytea       NOT NULL,
    attestation_object bytea       NOT NULL,
    client_data_json   bytea       NOT NULL,
    row_version        bigint      NOT NULL DEFAULT 1,
    CONSTRAINT identity_user_passkey_credential_id_not_empty CHECK (octet_length(credential_id) > 0),
    CONSTRAINT identity_user_passkey_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT identity_user_passkey_name_length CHECK (char_length(name) BETWEEN 1 AND 100),
    CONSTRAINT identity_user_passkey_normalized_name_not_blank CHECK (btrim(normalized_name) <> ''),
    CONSTRAINT identity_user_passkey_public_key_not_empty CHECK (octet_length(public_key) > 0),
    CONSTRAINT identity_user_passkey_sign_count_unsigned_32bit CHECK (sign_count BETWEEN 0 AND 4294967295),
    CONSTRAINT identity_user_passkey_is_user_verified CHECK (is_user_verified),
    CONSTRAINT identity_user_passkey_aaguid_length CHECK (octet_length(aaguid) = 16),
    CONSTRAINT identity_user_passkey_attestation_object_not_empty CHECK (octet_length(attestation_object) > 0),
    CONSTRAINT identity_user_passkey_client_data_json_not_empty CHECK (octet_length(client_data_json) > 0)
);

CREATE UNIQUE INDEX identity_user_passkey_user_normalized_name_idx
    ON identity_user_passkey (identity_user_id, normalized_name);
CREATE INDEX identity_user_passkey_identity_user_id_idx
    ON identity_user_passkey (identity_user_id);
