# ADR 0066 Stage 7: the cookie/session-shaped half of the multi-instance black-box scenario
# (docker/compose.multi-instance.yml, scripts/multi-instance-test.sh) -- everything that needs one
# continuing cookie jar carried across host_a and host_b. The concurrency-race and rate-limit halves
# need N parallel requests / loop-until-429, which hurl cannot express, so those live in
# scripts/multi-instance-test.sh as plain curl.
#
# hurl's cookie jar is keyed by domain only (RFC 6265; libcurl, like every real browser, ignores the
# port when matching a cookie's Domain), so a cookie set by host_a's response is sent automatically
# on the very next request to host_b as long as both resolve to the same hostname ("localhost") on
# different ports -- this is what makes a single hurl run capable of proving cross-host correctness
# with no manual Cookie-header bookkeeping.
#
# Run via --header "X-Forwarded-Proto: https" (scripts/multi-instance-test.sh) so Program.cs's
# ForwardedHeadersMiddleware treats every direct-host request as already secure -- otherwise
# UseHttpsRedirection() would 307 a plain-HTTP direct request to a host that has no HTTPS listener
# at all (Dockerfile.postgresql: ASPNETCORE_HTTP_PORTS=8080 only, matching Cloud Run's own
# front-end-terminates-TLS shape).
#
#   hurl --test --header "X-Forwarded-Proto: https" \
#        --variable host_a=http://localhost:8081 --variable host_b=http://localhost:8082 \
#        --variable admin_username=... --variable admin_password=... --variable admin_new_password=... \
#        tests/hurl/multi-instance-scenario.hurl

# ---- 1. sign in on host A; the forced first-sign-in password change is a real cross-cutting flow,
# not scenario noise -- every provisioned account carries it (docker/provision.sh, ADR 0023).
GET {{host_a}}/Account/Login
HTTP 200
[Captures]
login_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{host_a}}/Account/Login
[FormParams]
__RequestVerificationToken: {{login_token}}
Input.UserName: {{admin_username}}
Input.Password: {{admin_password}}
HTTP 302
[Asserts]
header "Location" contains "ChangePassword"

GET {{host_a}}/Account/ChangePassword
HTTP 200
[Captures]
change_password_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{host_a}}/Account/ChangePassword
[FormParams]
__RequestVerificationToken: {{change_password_token}}
Input.CurrentPassword: {{admin_password}}
Input.NewPassword: {{admin_new_password}}
Input.ConfirmNewPassword: {{admin_new_password}}
HTTP 302
[Asserts]
header "Location" == "/"

# ---- 2. cross-host authentication: the session cookie minted on A (PostgreSQL data-protection key
# ring, ADR 0066 Stage 2) authenticates a request that never touches A again.
GET {{host_b}}/Jobs/Browse
HTTP 200

# ---- 3. cross-host antiforgery + write: capture the root node id, then GET the Create form on A and
# POST it on B -- the antiforgery cookie+token pair minted on A must validate on B (same PostgreSQL
# key ring), and the write itself commits through the one shared database regardless of which host
# received the POST (plan §2.1). The freshly bootstrapped root carries no leaf work yet, so it is a
# valid Create parent but not a valid Decompose target (Decompose requires an already-worked leaf,
# ADR-unrelated domain rule); Create produces one new unassigned leaf directly, reused below for the
# concurrent-pickup race.
GET {{host_a}}/api/jobs/root
HTTP 200
[Captures]
root_id: jsonpath "$.node.id"

GET {{host_a}}/Jobs/Create?ParentId={{root_id}}
HTTP 200
[Captures]
create_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{host_b}}/Jobs/Create
[FormParams]
__RequestVerificationToken: {{create_token}}
ParentId: {{root_id}}
Input.Description: Race leaf
Input.OwnerUserId:
Input.Priority: Medium
HTTP 302
[Asserts]
header "Location" contains "/Jobs/Browse"
[Captures]
race_leaf_id: header "Location" regex "nodeId=(\\d+)"

# ---- 4. filter recall: set ExcludeBlocked on A, read it back on B with no query string at all
# (CookieFilterMemoryStore, ADR 0066 Stage 3).
GET {{host_a}}/Jobs/AwaitingProgress?ExcludeBlocked=true
HTTP 200

GET {{host_b}}/Jobs/AwaitingProgress
HTTP 200
[Asserts]
xpath "string(//input[@id='ExcludeBlocked']/@checked)" == "checked"

# ---- 5. PAT delivery: issue on A, the redirected reveal GET lands on B and shows the plaintext
# exactly once (PendingPatDeliveryCookie, ADR 0066 Stage 4) -- a repeat GET on B shows nothing.
GET {{host_a}}/Account/PersonalAccessTokens
HTTP 200
[Captures]
issue_token: xpath "string(//form[@action='/Account/PersonalAccessTokens?handler=Issue']//input[@name='__RequestVerificationToken']/@value)"

POST {{host_a}}/Account/PersonalAccessTokens?handler=Issue
[FormParams]
__RequestVerificationToken: {{issue_token}}
Issue.Label: multi-instance-proof
Issue.LifetimeDays: 1
HTTP 302
[Captures]
issued_pat_location: header "Location"

GET {{host_b}}{{issued_pat_location}}
HTTP 200
[Asserts]
xpath "count(//code)" == 1
xpath "string(//code/text())" matches "^\\S+$"

GET {{host_b}}{{issued_pat_location}}
HTTP 200
[Asserts]
xpath "count(//code)" == 0

# ---- 6. a second PAT, for scripts/multi-instance-test.sh's own API-rate-limit loop -- minted here
# rather than through a fresh login (mint-pat.hurl assumes an account still has ADR 0023's forced
# first-sign-in password change pending, which admin no longer does once step 1 above completed it).
GET {{host_a}}/Account/PersonalAccessTokens
HTTP 200
[Captures]
rate_limit_issue_token: xpath "string(//form[@action='/Account/PersonalAccessTokens?handler=Issue']//input[@name='__RequestVerificationToken']/@value)"

POST {{host_a}}/Account/PersonalAccessTokens?handler=Issue
[FormParams]
__RequestVerificationToken: {{rate_limit_issue_token}}
Issue.Label: api-rate-limit-probe
Issue.LifetimeDays: 1
HTTP 302
[Captures]
rate_limit_pat_location: header "Location"

GET {{host_a}}{{rate_limit_pat_location}}
HTTP 200
[Captures]
api_rate_limit_token: xpath "string(//code/text())"
[Asserts]
xpath "string(//code/text())" matches "^\\S+$"
