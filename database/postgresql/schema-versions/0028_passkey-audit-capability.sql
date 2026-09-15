-- Schema version 0028 (PostgreSQL): the passkey credential-transition and passkey-sign-in audit
-- operation names join the credential-administration capability set (ADR 0071 §7/§9, threat model
-- row 23). Only jobtrack_credential_administration (add/rename/remove/reset) or
-- jobtrack_emergency_reset (CLI reset) may append them; a compromised domain credential cannot
-- fabricate passkey audit evidence. Replaces 0025's function body in place (CREATE OR REPLACE);
-- the trigger binding is unchanged.

CREATE OR REPLACE FUNCTION reject_unauthorized_credential_audit_event() RETURNS trigger AS
$$
BEGIN
    IF NEW.operation IN (
        'create-employee',
        'assign-employee-role',
        'revoke-employee-role',
        'set-employee-enabled',
        'reset-employee-password',
        'reset-employee-two-factor',
        'reset-employee-passkeys',
        'authentication.login-success',
        'authentication.login-failed',
        'authentication.lockout',
        'authentication.logout',
        'authentication.password-change',
        'authentication.two-factor-enabled',
        'authentication.two-factor-disabled',
        'authentication.two-factor-failed',
        'authentication.passkey-added',
        'authentication.passkey-renamed',
        'authentication.passkey-removed',
        'authentication.passkey-sign-in-success',
        'authentication.passkey-sign-in-failed')
       AND NOT pg_has_role(current_user, 'jobtrack_credential_administration', 'member')
       AND NOT pg_has_role(current_user, 'jobtrack_emergency_reset', 'member') THEN
        RAISE EXCEPTION 'credential-administration audit operation requires its database capability'
            USING ERRCODE = '42501';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
