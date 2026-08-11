-- Schema version 0001 (PostgreSQL): schema-version tracking table and the
-- stable reference table that has no forward dependency on any other table
-- (achievement_status). See impl plan §6.1/§6.2 item 1, ADR 0001, ADR 0011.
--
-- The deployment tool inserts this script's own tracking row into
-- schema_version after running the statements below (the table does not
-- exist yet while this script itself is executing).

CREATE TABLE schema_version
(
    version             integer PRIMARY KEY,
    description         text NOT NULL,
    checksum            text NOT NULL,
    applied_by          text NOT NULL,
    application_version text NOT NULL,
    applied_at          timestamptz NOT NULL
);

CREATE TABLE achievement_status
(
    id   smallint PRIMARY KEY,
    name text NOT NULL UNIQUE
);

-- ADR 0001: canonical Achievement states, in permitted-transition order.
INSERT INTO achievement_status (id, name)
VALUES (1, 'Waiting'),
       (2, 'InProgress'),
       (3, 'Success'),
       (4, 'Cancelled'),
       (5, 'Unsuccessful');
