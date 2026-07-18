# Running a persistent local "live" instance

The README's "Running on a development server" section documents `jobtrack_dev`/
`jobtrack-web-dev.db` — explicitly disposable databases you drop and recreate between manual runs
(README: "delete... between unrelated manual runs"). `samples/JobTrack.UatSeed` layers a
synthetic, non-idempotent demo scenario on top of one of those for testing.

Neither is meant to survive: if you want a single persistent PostgreSQL database for your own
ongoing real use on this machine (not wiped between sessions, not seeded with synthetic demo
data), follow the same three steps against a differently named database that you never drop.

This is the "simple persistent local DB" tier — a single Homebrew PostgreSQL instance, your own OS
login role, no separate `LOGIN` role or secrets file. For an actual multi-user production
deployment, use [`production-deployment.md`](production-deployment.md) instead, which provisions a
dedicated `LOGIN` role scoped to the `jobtrack_application`/`jobtrack_schema_deployer` group roles.

```bash
# 1. Create the database (once).
psql -h /tmp -p 5432 -d postgres -c "CREATE DATABASE jobtrack_live LOCALE_PROVIDER icu ICU_LOCALE 'en-GB' TEMPLATE template0"

# 2. Deploy the schema (once). Also applies database/postgresql/roles/jobtrack-roles-and-grants.sql.
dotnet run --project src/JobTrack.Database -- deploy --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_live" --scripts-root database/postgresql/schema-versions

# 3. Bootstrap the first administrator (once, interactive — run in a real terminal, not piped:
#    password entry uses Console.ReadKey with masking, which requires a TTY).
dotnet run --project src/JobTrack.AdminCli -- bootstrap --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_live"
```

Step 3 prompts for display name, IANA time zone, username, and password. The time zone prompt
defaults to `Europe/London` and the username prompt defaults to your current OS login
(`Environment.UserName`) — press enter on either to accept the default, or type a value to
override it. Display name and password have no default and always need typing.

Bootstrap is a one-time atomic operation (`IInstallationCommands.BootstrapAdministratorAsync`) that
creates both the administrator account and the root job node — there is no separate "seed the root
node" step.

**4. Launch the web app against `jobtrack_live`.** `appsettings.Development.json` ships pointed at
the disposable SQLite dev database (see "Running on a development server" in the README), so
override the provider and connection string for this run via environment variables rather than
editing that shared file — editing it in place would silently repoint the ordinary SQLite dev
workflow too. `scripts/run-web.sh` wraps exactly this:

```bash
./scripts/run-web.sh
```

which runs:

```bash
Database__Provider=PostgreSql ConnectionStrings__JobTrackIdentity="Host=/tmp;Port=5432;Database=jobtrack_live" dotnet run --project src/JobTrack.Web --launch-profile https
```

**Sign-in requires the `https` profile.** `Program.cs` sets `Cookie.SecurePolicy = CookieSecurePolicy.Always`
on the authentication cookie (a deliberate fail-closed default, not a bug) — over plain HTTP the
browser silently discards a `Secure` cookie, so `PasswordSignInAsync` succeeds server-side but the
very next request looks unauthenticated and you bounce straight back to `/Account/Login` with no
visible error. `scripts/run-web.sh` already passes `--launch-profile https`; if you run the
`dotnet run` command directly instead, remember to add it yourself.

This listens on `https://localhost:7174` (plus `http://localhost:5034`, which still won't let you
sign in) and opens a browser automatically. Sign in with the administrator credentials from step 3
— first sign-in forces an immediate password change.

If you do run the plain `http` profile for some other reason, you'll also see a harmless startup
warning that's a symptom of the same thing:

```
warn: Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionMiddleware[3]
      Failed to determine the https port for redirect.
```

`HttpsRedirectionMiddleware` is enabled but the `http` profile has no HTTPS endpoint to redirect
to — another reason to use `--launch-profile https` rather than silencing this.

Do not run `samples/JobTrack.UatSeed` against this database — it is not idempotent and exists to
populate throwaway UAT scenarios, not a database meant to persist.

## Resetting a password

The normal way to reset a password is the Administrator-only page in the web interface. When
that isn't usable — the administrator account itself is locked out, or the web app isn't
reachable — `JobTrack.AdminCli`'s `reset-password` command is the emergency fallback: it talks to
`jobtrack_live` directly (layer 2, in-process), not over HTTP:

```bash
# Emergency reset against jobtrack_live (works even if the administrator account is locked out).
dotnet run --project src/JobTrack.AdminCli -- reset-password --provider postgresql --connection-string "Host=/tmp;Port=5432;Database=jobtrack_live" --username <username>
```

It prints a one-time temporary password to relay to the user out-of-band, forces a password
change at their next sign-in, and revokes every personal access token and session tied to that
account. See `src/JobTrack.AdminCli/EmergencyPasswordReset.cs` for exactly what it does and why.
