-- Schema version 0029 (PostgreSQL): finish the bounded passkey storage contract
-- from ADR 0071. The application uses ASP.NET Core's verified UserPasskeyInfo,
-- which does not expose the authenticator AAGUID; represent that value as
-- unknown instead of persisting a fabricated all-zero identifier.

ALTER TABLE identity_user
    DROP CONSTRAINT identity_user_passkey_user_handle_not_blank,
    ADD CONSTRAINT identity_user_passkey_user_handle_length
        CHECK (passkey_user_handle IS NULL OR char_length(passkey_user_handle) = 43);

ALTER TABLE identity_user_passkey
    ALTER COLUMN aaguid DROP NOT NULL,
    ADD CONSTRAINT identity_user_passkey_credential_id_length
        CHECK (octet_length(credential_id) BETWEEN 1 AND 1023),
    ADD CONSTRAINT identity_user_passkey_public_key_length
        CHECK (octet_length(public_key) BETWEEN 1 AND 4096),
    ADD CONSTRAINT identity_user_passkey_transports_length
        CHECK (transports IS NULL OR char_length(transports) <= 512),
    ADD CONSTRAINT identity_user_passkey_attestation_object_length
        CHECK (octet_length(attestation_object) BETWEEN 1 AND 16384),
    ADD CONSTRAINT identity_user_passkey_client_data_json_length
        CHECK (octet_length(client_data_json) BETWEEN 1 AND 4096);

UPDATE identity_user_passkey
SET aaguid = NULL
WHERE aaguid = decode(repeat('00', 16), 'hex');
