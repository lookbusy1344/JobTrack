# ADR 0022: `JobTrack.Identity` owns an independent EF mapping of the credential tables

**Status:** Accepted
**Closes:** Implementation plan §8.2 ("this adapter owns the production Identity `DbContext`,
stores... and provider registration")

## Decision

`JobTrack.Identity` implements ASP.NET Core Identity's store contracts (`IUserStore<TUser>`,
`IUserPasswordStore<TUser>`, `IUserSecurityStampStore<TUser>`, `IUserLockoutStore<TUser>`) by hand
against its own `DbContext` and its own `JobTrackIdentityUser` entity, both defined inside
`JobTrack.Identity`. It does **not**:

- reference `JobTrack.Persistence.Shared`, `JobTrack.Persistence.PostgreSql`, or
  `JobTrack.Persistence.Sqlite` (`IdentityUserEntity` there is `internal` to that assembly, and ADR
  0005 already reserves that mapping for the one-time bootstrap credential write only); or
- derive from `Microsoft.AspNetCore.Identity.EntityFrameworkCore`'s generic
  `IdentityDbContext<TUser, TRole, TKey, ...>`/`UserStore<...>`.

`JobTrack.Identity` keeps its only `JobTrack.*` project reference to `JobTrack.Abstractions`
(shared identifier/value types), per the project layout in `CLAUDE.md`.

### Why not the generic EF Core Identity store

`Microsoft.AspNetCore.Identity.EntityFrameworkCore`'s generic `UserStore<TUser, TRole, TContext,
TKey, TUserClaim, TUserRole, TUserLogin, TUserToken, TRoleClaim>` requires a single `TKey` shared by
both the user and role join entity (`IdentityUserRole<TKey>`). This schema (schema version 0002,
`database/*/schema-versions/0002_app-user-and-identity-storage.sql`) uses `bigint` for
`identity_user.id` and `smallint` for the fixed six-row `identity_role.id`, and has no `email`/
`phone_number` columns (spec §7.1: plain-text, no-email employee accounts) — mapping that
mismatched, narrower shape onto the generic store's fixed conventions would need more override
surface than a direct hand-written store, for a library type this project does not otherwise depend
on. A hand-written store is also the natural fit for the "EF-first" house style: it is ordinary
LINQ/EF authored against a purpose-built model, not a generic type parameterised around assumptions
this schema does not share.

### Why a second, independent mapping of the same tables rather than reusing `IdentityUserEntity`

ADR 0005 already draws this line for the bootstrap write path: the library-owned credential-write
port "is not a general Identity store and must not be reused for ongoing credential persistence
(that remains `JobTrack.Identity`'s job, §8.2)." `IdentityUserEntity` (`JobTrack.Persistence.Shared`)
is `internal`, so `JobTrack.Identity` cannot reference it without weakening that assembly boundary
with an `InternalsVisibleTo` grant to a non-test assembly — something this project does not do
elsewhere. Two independent EF mappings of the same physical table, owned by different layers for
different purposes (one-time atomic bootstrap vs. ongoing authentication), is the direct consequence
of ADR 0005's boundary, not a new decision being made here.

### Scope of this increment

This ADR and the accompanying implementation cover only what §8.5 slice 1 (sign-in) needs:
`IUserStore`, `IUserPasswordStore`, `IUserSecurityStampStore`, and `IUserLockoutStore`. Role storage
(`IUserRoleStore`, `identity_role`/`identity_user_role`) is deferred to the authorization slice
(§8.3, §8.5 item 3+) and is a separate ADR-free extension of the same store class when it lands —
no new architectural decision, just more interfaces on the same type.

## Consequences

- `JobTrack.Identity` takes `PackageReference`s on `Microsoft.EntityFrameworkCore`,
  `Npgsql.EntityFrameworkCore.PostgreSQL`, and `Microsoft.EntityFrameworkCore.Sqlite` in addition to
  the existing `Microsoft.Extensions.Identity.Core` (ADR 0005) — all NuGet package references, not
  `JobTrack.*` project references, so the project-layout boundary in `CLAUDE.md` is unaffected.
- `Microsoft.AspNetCore.Identity`'s store interfaces, `UserManager<TUser>`, `PasswordHasher<TUser>`,
  `IdentityOptions`, `IdentityError`, and `IdentityResult` all live in
  `Microsoft.Extensions.Identity.Core` (already referenced) — no `FrameworkReference` to
  `Microsoft.AspNetCore.App` is needed for this slice. `SignInManager<TUser>` and cookie
  authentication middleware, which do require that shared framework, remain `JobTrack.Web`'s
  responsibility to compose.
- Both `JobTrack.Identity`'s mapping and `JobTrack.Persistence.Shared`'s `IdentityUserEntity`
  mapping must be kept in sync by hand against schema-version changes to `identity_user`; there is
  no shared source of truth between them by design (see above). A schema change to that table
  touches both.
- Provider selection (`AddJobTrackIdentityPostgreSql`/`AddJobTrackIdentitySqlite` extension methods
  on `IServiceCollection`) is owned by `JobTrack.Identity`, composed by `JobTrack.Web` and
  `JobTrack.AdminCli` — matching plan §8.2's "provider registration" clause.
