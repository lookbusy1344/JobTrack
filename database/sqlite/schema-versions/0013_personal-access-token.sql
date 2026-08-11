-- Schema version 0013 (SQLite): personal_access_token, the opaque
-- bearer-token store backing the external HTTP API's non-browser client
-- authentication (ADR 0029, ADR 0030, plan §4.1-4.2). Only a salted hash of
-- the token is ever stored; the plaintext exists solely in the issuance
-- result returned once to the caller. Instants are stored as UTC ticks
-- (ADR 0007), matching every other Instant column in this schema.

CREATE TABLE personal_access_token
(
    id           INTEGER PRIMARY KEY,
    app_user_id  INTEGER NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    token_hash   TEXT    NOT NULL,
    label        TEXT    NOT NULL,
    created_at   INTEGER NOT NULL,
    expires_at   INTEGER NOT NULL,
    revoked_at   INTEGER,
    last_used_at INTEGER,
    CHECK (trim(label) <> ''),
    CHECK (expires_at > created_at)
) STRICT;

CREATE UNIQUE INDEX personal_access_token_token_hash_idx ON personal_access_token (token_hash);
CREATE INDEX personal_access_token_app_user_id_idx ON personal_access_token (app_user_id);
