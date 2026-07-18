# syntax=docker/dockerfile:1
#
# Container image for JobTrack.Web, backed by SQLite (no separate database container/server
# needed — see README's "Dual-provider persistence": SQLite is a fully conforming backend, not a
# reduced-feature fallback). This image is for local smoke-testing/demo use (e.g. under OrbStack),
# not the project's actual production topology: ADR 0014 fixes a single bare-metal/VM server
# behind a locally managed reverse proxy, with containers explicitly out of scope for now. See
# docs/operations/production-deployment.md for the real deployment runbook.
#
# Build context MUST be the monorepo root (VSStuff/, this file's parent's parent), NOT the
# JobTrack/ folder — .editorconfig lives one level above JobTrack/ (JobTrack/CLAUDE.md: "the git
# root is the parent VSStuff/ directory") and the analyzer build below is TreatWarningsAsErrors, so
# a build missing that file's severity overrides (e.g. VSTHRD103 downgraded to none, a duplicate of
# CA1849) fails on rules the real local build never hits. Run from JobTrack/:
#
#   docker build -f Dockerfile -t jobtrack-web ..
#
# The image bundles three apphosts, all framework-dependent/ReadyToRun for the target arch:
#   ./web/JobTrack.Web           — the application itself (default ENTRYPOINT)
#   ./database/JobTrack.Database — one-time SQLite schema deployment (--entrypoint override)
#   ./admincli/JobTrack.AdminCli — one-time administrator bootstrap (--entrypoint override)
#
# First-run sequence against a fresh named volume:
#
#   docker volume create jobtrack-data
#
#   docker run --rm -v jobtrack-data:/app/data --entrypoint ./database/JobTrack.Database \
#     jobtrack-web deploy --provider sqlite --connection-string "Data Source=/app/data/jobtrack.db" \
#     --scripts-root ./schema-versions
#
#   docker run --rm -it -v jobtrack-data:/app/data --entrypoint ./admincli/JobTrack.AdminCli \
#     jobtrack-web bootstrap --provider sqlite --connection-string "Data Source=/app/data/jobtrack.db"
#
#   docker run --rm -p 8443:8443 -v jobtrack-data:/app/data jobtrack-web
#   open https://localhost:8443  (self-signed dev cert baked into the image — see below; the
#   browser will warn, that's expected)
#
# Sign-in requires HTTPS: Program.cs sets the authentication cookie to Secure-only unconditionally
# (docs/operations/local-live-instance.md), so the image listens on HTTPS only, no plaintext HTTP
# port at all.

# ---- build stage -----------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# JobTrack.Web references sibling projects and Directory.Build.props/.editorconfig from the
# solution root — a partial copy is fragile, so copy the whole folder and let .dockerignore prune
# build/test/doc junk not needed to compile. The monorepo root's .editorconfig is copied
# separately, one level up, to mirror its real position relative to JobTrack/ (see header comment)
# so the analyzer directory-walk finds the same severity overrides a local build sees.
COPY .editorconfig ./.editorconfig
COPY JobTrack/ ./JobTrack/
WORKDIR /src/JobTrack

# wwwroot/lib (Bootstrap, Mulish) is restored via LibMan, not committed (house style: "Bootstrap is
# pinned via libman.json ... not vendored ad hoc") — restore it here rather than relying on it
# being present in the build context.
RUN dotnet tool install --tool-path /libman Microsoft.Web.LibraryManager.Cli \
    && (cd src/JobTrack.Web && /libman/libman restore)

# A self-signed HTTPS dev certificate baked into the image. This is deliberately the same
# no-real-secret pattern as `dotnet dev-certs https` for local development — fine for a local/demo
# image explicitly out of scope for production (see header comment), never for a publicly
# reachable deployment.
ARG DEV_CERT_PASSWORD=JobTrackDevOnly1!
RUN mkdir -p /https && dotnet dev-certs https -ep /https/devcert.pfx -p "$DEV_CERT_PASSWORD"

# ReadyToRun pre-compiles IL to native code at publish time, taking JIT work off the cold-start
# path. It is RID-specific, so derive the RID from the build's target platform. PublishReadyToRun
# must be set on *restore* too, or the crossgen2 package is absent and publish fails with
# NETSDK1094.
ARG TARGETARCH
RUN RID=linux-$(echo "${TARGETARCH:-amd64}" | sed 's/amd64/x64/') && echo "$RID" > /rid \
    && dotnet restore src/JobTrack.Web/JobTrack.Web.csproj -r "$RID" -p:PublishReadyToRun=true \
    && dotnet restore src/JobTrack.Database/JobTrack.Database.csproj -r "$RID" -p:PublishReadyToRun=true \
    && dotnet restore src/JobTrack.AdminCli/JobTrack.AdminCli.csproj -r "$RID" -p:PublishReadyToRun=true

# Framework-dependent publish; the aspnet runtime image supplies the framework (itself already
# R2R-compiled), so only each app's own assemblies need crossgen here.
RUN RID="$(cat /rid)" \
    && dotnet publish src/JobTrack.Web/JobTrack.Web.csproj \
         -c Release -o /app/web --no-restore -r "$RID" --self-contained false -p:PublishReadyToRun=true \
    && dotnet publish src/JobTrack.Database/JobTrack.Database.csproj \
         -c Release -o /app/database --no-restore -r "$RID" --self-contained false -p:PublishReadyToRun=true \
    && dotnet publish src/JobTrack.AdminCli/JobTrack.AdminCli.csproj \
         -c Release -o /app/admincli --no-restore -r "$RID" --self-contained false -p:PublishReadyToRun=true

# Data directory (SQLite file + data-protection key ring). Built here rather than in the runtime
# stage because chiseled has no shell/mkdir/chown, and so its ownership can be set on COPY below.
#
# The demo database is seeded at build time so `docker run` is immediately usable with no manual
# setup. A named volume mounted at /app/data is empty on first use and Docker populates it from this
# image content, so the seeded accounts and trees survive into the volume; an existing volume is
# left untouched. Three steps, in order:
#
#   1. Deploy the schema.
#   2. Bootstrap the ADMINISTRATOR (privileged account: owns the root node and can manage accounts).
#      The admin password is ALWAYS random -- there is no known default. If ADMIN_PASSWORD is passed
#      it is used (the Cloud Run script generates and prints one, scripts/deploy-cloudrun.sh);
#      otherwise a random one is generated here and recorded nowhere, which is fine because a local
#      `docker run` signs in as `demo`, and the admin is a privileged escape hatch you re-provision
#      (docker exec the AdminCli) or rebuild with an explicit --build-arg ADMIN_PASSWORD if needed.
#      --no-force-password-change: the baked-in password resets to the same value on every recycle,
#      so forcing a change that immediately reverts is pointless friction.
#   3. Create the DEMO account -- a normal (non-admin) JobManager+Worker employee whose published
#      credential is deliberately reusable: --no-force-password-change clears the ADR 0023 default
#      so demo/demo1234 keeps working without a forced change on first sign-in (any live change is
#      wiped back to this seed on a Cloud Run recycle -- see docs/operations/docker-image.md).
#   4. Import the sample job trees as the DEMO account, so demo -- not the admin -- owns them. Each
#      import lands a subtree under the root (import-tree's default --parent-id 1).
#
# Bootstrap still prompts interactively for display name / time zone / username (only the password
# has a --password flag), so those three lines are piped on stdin; --password removes the need for
# the `script` pty the old single-account seed used. create-employee and import-tree are fully
# non-interactive (all flags), so they need no stdin.
#
# The only known credential baked in is the published DEMO one, tolerable solely because this image
# is a local demo artifact (see the header comment) -- which is precisely why it must never be
# exposed to a network with a reachable demo account. The admin password is never a known default.
ARG ADMIN_USERNAME=admin
# Empty by default -- a random password is generated in the RUN below when this is not supplied, so
# the image never carries a known admin credential. Pass --build-arg ADMIN_PASSWORD to pin one.
ARG ADMIN_PASSWORD=
ARG ADMIN_DISPLAY_NAME="Administrator"
ARG DEMO_USERNAME=demo
ARG DEMO_PASSWORD=demo1234
ARG DEMO_DISPLAY_NAME="Demo User"
ARG SEED_TIME_ZONE=Europe/London
RUN mkdir -p /appdata/keys \
    && admin_password="${ADMIN_PASSWORD:-$(head -c 18 /dev/urandom | base64 | tr -d '/+=' | cut -c1-24)}" \
    && /app/database/JobTrack.Database deploy --provider sqlite \
         --connection-string "Data Source=/appdata/jobtrack.db" \
         --scripts-root database/sqlite/schema-versions \
    && printf '%s\n%s\n%s\n' "$ADMIN_DISPLAY_NAME" "$SEED_TIME_ZONE" "$ADMIN_USERNAME" \
       | /app/admincli/JobTrack.AdminCli bootstrap --provider sqlite \
           --connection-string "Data Source=/appdata/jobtrack.db" --password "$admin_password" \
           --no-force-password-change \
    && /app/admincli/JobTrack.AdminCli create-employee --provider sqlite \
         --connection-string "Data Source=/appdata/jobtrack.db" \
         --actor "$ADMIN_USERNAME" --username "$DEMO_USERNAME" --password "$DEMO_PASSWORD" \
         --display-name "$DEMO_DISPLAY_NAME" --iana-time-zone "$SEED_TIME_ZONE" \
         --roles JobManager,Worker --no-force-password-change \
    && for tree in samples/job-tree-imports/*.json; do \
         /app/admincli/JobTrack.AdminCli import-tree --provider sqlite \
           --connection-string "Data Source=/appdata/jobtrack.db" \
           --username "$DEMO_USERNAME" --file "$tree" || exit 1; \
       done \
    && test -f /appdata/jobtrack.db

# ---- runtime stage ---------------------------------------------------------
# Chiseled: half the size of the Debian default tag, much smaller attack surface, no shell/package
# manager. It ships no ICU and so defaults to DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true — fine
# here, as the codebase uses CultureInfo.InvariantCulture exclusively and Noda Time's TZDB data is
# bundled in NodaTime.TimeZones rather than relying on system ICU/tzdata.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app/web ./web
COPY --from=build /app/database ./database
COPY --from=build /app/admincli ./admincli
COPY --from=build /src/JobTrack/database/sqlite/schema-versions ./schema-versions
# --chown is required, not cosmetic: dev-certs writes the .pfx 0600/root, which the non-root
# APP_UID below cannot read, and Kestrel fails to bind at startup.
COPY --from=build --chown=$APP_UID:$APP_UID /https/devcert.pfx ./certs/devcert.pfx
COPY --from=build --chown=$APP_UID:$APP_UID /appdata ./data

# No plaintext HTTP endpoint at all (see header comment on Secure-only cookies) — clear the
# chiseled base image's default ASPNETCORE_HTTP_PORTS=8080 rather than leave an unused listener.
ENV ASPNETCORE_HTTP_PORTS=
ENV ASPNETCORE_ENVIRONMENT=Production
# Required because the three apphosts live in sibling subdirectories under WORKDIR /app rather
# than at /app itself: the content root otherwise defaults to the process working directory
# (/app), so WebRootPath resolves to the non-existent /app/wwwroot. MapStaticAssets still matches
# its routes (the endpoint manifest is found next to the assembly) but serves every asset as an
# empty 200 — a silent, unstyled site, not a startup error. Keep this in step with the web
# apphost's own directory.
ENV ASPNETCORE_CONTENTROOT=/app/web
ENV Kestrel__Endpoints__Https__Url=https://+:8443
ENV Kestrel__Endpoints__Https__Certificate__Path=/app/certs/devcert.pfx
ENV Kestrel__Endpoints__Https__Certificate__Password=JobTrackDevOnly1!
ENV Database__Provider=Sqlite
ENV ConnectionStrings__JobTrackIdentity="Data Source=/app/data/jobtrack.db"
# Outside Development, Program.cs fails startup closed unless this is set (docs/operations/
# web-host-security.md) — an absolute path outside the app directory, writable by the container
# user, backed by the /app/data named volume so a re-created container keeps the same key ring.
ENV DataProtection__KeyPath=/app/data/keys
# Also required outside Development. Kestrel here is reached directly (no reverse proxy in front,
# unlike the real single-server topology), so no request will ever actually arrive at this address
# with forwarded headers to trust — it exists only to satisfy the fail-closed check, not to name a
# real trusted proxy.
ENV ForwardedHeaders__KnownProxies__0=127.0.0.1

EXPOSE 8443
VOLUME /app/data

# Run as the non-root user baked into the .NET runtime image.
USER $APP_UID

# A RID-targeted publish emits an apphost, so invoke it directly rather than through the `dotnet`
# muxer (the chiseled image ships no shell to resolve one anyway).
ENTRYPOINT ["./web/JobTrack.Web"]
