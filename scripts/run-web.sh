#!/usr/bin/env bash
#
# Watch, build, and launch JobTrack.Web against the persistent local "live"
# PostgreSQL instance (jobtrack_live) for manual testing.
# Rebuilds and restarts automatically whenever a source file changes.
#
# appsettings.Development.json ships pointed at the disposable SQLite dev
# database. The "https (jobtrack_live)" launch profile carries the PostgreSQL
# provider/connection-string overrides, so this script and a Rider debug session
# run against identical settings — see docs/operations/local-live-instance.md.
#
# That profile is https-only for a reason: Program.cs sets
# Cookie.SecurePolicy = CookieSecurePolicy.Always, so over plain HTTP the
# browser silently discards the auth cookie and every request bounces back
# to /Account/Login with no visible error.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/.." && pwd)"
project="$repo/src/JobTrack.Web/JobTrack.Web.csproj"

readonly Port=7174
url="https://localhost:$Port"

readonly BrowserPollSeconds=60

# 25 x 0.2s = a 5s budget for dotnet watch to honour each shutdown signal in turn.
readonly ShutdownPollIntervalSeconds=0.2
readonly ShutdownPollAttempts=25

# `launchBrowser: true` in launchSettings.json is not enough: dotnet watch decides when to open
# the browser by scraping "Now listening on: <url>" out of the app's own stdout, and
# appsettings.Development.json pins Microsoft.Hosting.Lifetime at Warning — so that line is never
# written and watch skips the launch with no diagnostic. Open it here instead, off the port
# actually answering, which holds whatever the log levels are set to. Suppress watch's own attempt
# so raising that level later yields one tab, not two.
export DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1

open_when_listening() {
	for _ in $(seq 1 "$BrowserPollSeconds"); do
		if curl -sk -o /dev/null --max-time 1 "$url"; then
			open "$url"
			return
		fi
		sleep 1
	done
	echo "==> $url did not answer within ${BrowserPollSeconds}s; not opening a browser" >&2
}

# A leftover watcher from a previous run still owning the port is the one failure that looks like a
# bug in this script: the poll above gets its answer from the *stale* app and opens a browser, then
# the freshly built one cannot bind, throws out of Run(), and the runtime aborts — "Exited with
# error code 134", with the bind failure buried above the browser noise. Refuse to start instead.
assert_port_free() {
	local holders
	holders="$(lsof -nP -tiTCP:"$Port" -sTCP:LISTEN || true)"
	if [[ -n "$holders" ]]; then
		echo "==> port $Port is already in use by PID(s): $(tr '\n' ' ' <<<"$holders")" >&2
		echo "    Most likely an orphaned 'dotnet watch' from a closed terminal. Stop it with:" >&2
		echo "        kill -9 $(tr '\n' ' ' <<<"$holders")" >&2
		exit 1
	fi
}

# dotnet watch survives both Ctrl-C and the terminal window closing, leaving an app child holding
# the port. `set -m` puts it in its own process group so the traps below can take the whole tree
# down. Job control also means the job keeps the terminal as its stdin and is stopped by SIGTTIN the
# moment it reads — silently, with no output at all — so it is launched with stdin detached.
#
# Being in its own process group, it never sees the terminal's own Ctrl-C: this function has to
# deliver the signal. SIGINT is the only one watch treats as "quit" — under SIGTERM it kills the app
# child but reads that as a crash and settles into "Waiting for a file to change before restarting",
# so it is escalation, not the opening move.
signal_watch_group() {
	kill -"$1" -"$watch_pid" 2>/dev/null || true
	for _ in $(seq 1 "$ShutdownPollAttempts"); do
		kill -0 "$watch_pid" 2>/dev/null || return 0
		sleep "$ShutdownPollIntervalSeconds"
	done
	return 1
}

shutdown_watch() {
	# Ignore, rather than clear: a second Ctrl-C landing mid-cleanup must not abort it before the
	# SIGKILL, or it strands the orphan this whole script exists to prevent.
	trap '' INT TERM HUP
	trap - EXIT
	kill "$browser_watcher" 2>/dev/null || true
	[[ -n "$watch_pid" ]] || return 0
	echo "==> stopping dotnet watch..." >&2
	signal_watch_group INT && return 0
	signal_watch_group TERM && return 0
	kill -KILL -"$watch_pid" 2>/dev/null || true
}

assert_port_free

dotnet build-server shutdown

echo "==> watching JobTrack.Web against jobtrack_live on $url (rebuilds on change)"

open_when_listening &
browser_watcher=$!

watch_pid=""
trap shutdown_watch EXIT INT TERM HUP

set -m
dotnet watch run --project "$project" --launch-profile "https (jobtrack_live)" --non-interactive </dev/null &
watch_pid=$!
set +m

status=0
wait "$watch_pid" || status=$?
exit "$status"
