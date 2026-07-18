-- Schema version 0003 (SQLite): the singleton initialised_marker table --
-- the authoritative "has bootstrap happened" flag. See impl plan §6.2
-- item 3, ADR 0015 item 1.
--
-- The permanent-root job_node and its undeletable/un-re-parentable guard
-- (which will reference this table once armed) belong to schema slice 4,
-- not this one -- see ADR 0015's own cross-reference to plan §6.2 item 4.
-- The atomic bootstrap library command that populates this table is a
-- separate, later milestone (M5, ADR 0005) -- no library code is added here.
--
-- initialised_at is a signed 64-bit UTC tick count (100ns ticks since the
-- Unix epoch), per ADR 0007 -- the same encoding as schema_version.applied_at.
--
-- id is constrained to the literal value 1, so the primary key alone makes
-- a second row impossible; triggers below additionally block UPDATE/DELETE,
-- so the one row -- once inserted -- can never be inserted twice, changed,
-- or removed (ADR 0015: "the row itself only ever being insertable once").

CREATE TABLE initialised_marker
(
    id             INTEGER PRIMARY KEY CHECK (id = 1),
    initialised_at INTEGER NOT NULL
) STRICT;

CREATE TRIGGER initialised_marker_no_update
    BEFORE UPDATE
    ON initialised_marker
BEGIN
    SELECT RAISE(ABORT, 'initialised_marker is append-only and cannot be updated or deleted (ADR 0015)');
END;

CREATE TRIGGER initialised_marker_no_delete
    BEFORE DELETE
    ON initialised_marker
BEGIN
    SELECT RAISE(ABORT, 'initialised_marker is append-only and cannot be updated or deleted (ADR 0015)');
END;
