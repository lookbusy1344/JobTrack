-- Schema version 0001 (SQLite): schema-version tracking table and the
-- stable reference table that has no forward dependency on any other table
-- (achievement_status). See impl plan §6.1/§6.2 item 1, ADR 0001, ADR 0011.
--
-- applied_at is a signed 64-bit UTC tick count (100ns ticks since the Unix
-- epoch), per ADR 0007 — the same encoding as every other instant column.
--
-- The deployment tool inserts this script's own tracking row into
-- schema_version after running the statements below (the table does not
-- exist yet while this script itself is executing).

CREATE TABLE schema_version
(
    version             INTEGER PRIMARY KEY,
    description         TEXT NOT NULL,
    checksum            TEXT NOT NULL,
    applied_by          TEXT NOT NULL,
    application_version TEXT NOT NULL,
    applied_at          INTEGER NOT NULL
) STRICT;

CREATE TABLE achievement_status
(
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE
) STRICT;

-- ADR 0001: canonical Achievement states, in permitted-transition order.
INSERT INTO achievement_status (id, name)
VALUES (1, 'Waiting'),
       (2, 'InProgress'),
       (3, 'Success'),
       (4, 'Cancelled'),
       (5, 'Unsuccessful');
