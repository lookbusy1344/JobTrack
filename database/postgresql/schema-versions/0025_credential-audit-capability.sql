-- Schema version 0025 (PostgreSQL): credential-administration audit events may be appended only by
-- the dedicated credential capability or the deliberately separate emergency-reset capability.
-- jobtrack_domain retains INSERT for the domain mutations it genuinely serves, but cannot fabricate
-- the fixed operation names used as evidence for login, credential, account-state or role changes.

CREATE FUNCTION reject_unauthorized_credential_audit_event() RETURNS trigger AS
$$
BEGIN
    IF NEW.operation IN (
        'create-employee',
        'assign-employee-role',
        'revoke-employee-role',
        'set-employee-enabled',
        'reset-employee-password',
        'reset-employee-two-factor',
        'authentication.login-success',
        'authentication.login-failed',
        'authentication.lockout',
        'authentication.logout',
        'authentication.password-change',
        'authentication.two-factor-enabled',
        'authentication.two-factor-disabled',
        'authentication.two-factor-failed')
       AND NOT pg_has_role(current_user, 'jobtrack_credential_administration', 'member')
       AND NOT pg_has_role(current_user, 'jobtrack_emergency_reset', 'member') THEN
        RAISE EXCEPTION 'credential-administration audit operation requires its database capability'
            USING ERRCODE = '42501';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER audit_event_credential_capability
    BEFORE INSERT ON audit_event
    FOR EACH ROW
EXECUTE FUNCTION reject_unauthorized_credential_audit_event();
