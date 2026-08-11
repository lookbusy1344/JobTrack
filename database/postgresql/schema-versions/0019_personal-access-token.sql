-- Schema version 0019 (PostgreSQL): personal_access_token, the opaque
-- bearer-token store backing the external HTTP API's non-browser client
-- authentication (ADR 0029, ADR 0030, plan §4.1-4.2). Only a salted hash of
-- the token is ever stored; the plaintext exists solely in the issuance
-- result returned once to the caller.

CREATE TABLE personal_access_token
(
    id           bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    app_user_id  bigint      NOT NULL REFERENCES app_user (id) ON DELETE RESTRICT,
    token_hash   text        NOT NULL,
    label        text        NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT now(),
    expires_at   timestamptz NOT NULL,
    revoked_at   timestamptz,
    last_used_at timestamptz,
    CONSTRAINT personal_access_token_label_not_blank CHECK (btrim(label) <> ''),
    CONSTRAINT personal_access_token_expires_after_created CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX personal_access_token_token_hash_idx ON personal_access_token (token_hash);
CREATE INDEX personal_access_token_app_user_id_idx ON personal_access_token (app_user_id);
