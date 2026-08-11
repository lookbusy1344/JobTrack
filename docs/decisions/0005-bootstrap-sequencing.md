# ADR 0005: Atomic bootstrap sequencing (first administrator, permanent root, initialised marker)

**Status:** Accepted
**Closes:** Implementation plan §5.1 item 2

## Decision

The atomic first-administrator/permanent-root/initialised-marker bootstrap is a single-transaction **application-library** command (M5, §7.3 step 1), not a front-end (`JobTrack.Identity`) operation. In one transaction it writes:

- the `app_user` profile;
- the Identity credential row (username, password hash, security stamp, force-change flag);
- the permanent root `job_node`; and
- the initialised marker.

It depends only on:

- `IPasswordHasher<T>` from `Microsoft.Extensions.Identity.Core` — which carries no ASP.NET Core dependency; and
- a narrow library-owned credential-write port against the Identity tables established in the database phase (§6.2.2).

It does **not** depend on the front-end-phase `JobTrack.Identity` adapter. `JobTrack.Identity` later supplies only the runtime `UserManager`/store wiring for ordinary authentication — it never re-implements or wraps the bootstrap transaction; the CLI and any other caller *invoke* the library command (§8.6) and supply the concrete `IPasswordHasher<T>` at composition.

### Why not defer bootstrap to the front-end phase

Sequencing the reusable library after the database but before the web front end (plan §1) would otherwise force a same-phase dependency on ASP.NET Core Identity to exist before M5 database ports are provable — the two constraints conflict unless the credential write is deliberately narrowed to `Microsoft.Extensions.Identity.Core` only. Doing so keeps the bootstrap fully unit- and provider-conformance-testable in the library phase, without needing a hosted `UserManager` or DI container.

## Consequences

- `JobTrack.Application` (or a narrowly scoped internal component within it) takes a package reference on `Microsoft.Extensions.Identity.Core` only, never `Microsoft.AspNetCore.Identity` — enforced by an architecture test (§7.5).
- The credential-write port is a persistence-port abstraction implemented by both `JobTrack.Persistence.PostgreSql` and `JobTrack.Persistence.Sqlite`, scoped narrowly to the Identity tables needed for bootstrap — it is not a general Identity store and must not be reused for ongoing credential persistence (that remains `JobTrack.Identity`'s job, §8.2).
- See ADR 0013 (`0013-bootstrap-state-machine.md`) for the provider-specific atomicity/guard mechanics (partial unique index plus transaction ordering) that make this command safe under concurrent bootstrap attempts.
- The AdminCli bootstrap command (§8.6) is a thin wrapper: it collects the protected interactive secret and invokes this library command; it must not duplicate the transaction logic.
