# Web application threat model and abuse-case test plan

**Closes:** Implementation plan §8.2 ("produce a threat model and abuse-case test plan"), spec
§16 ("threat modelling shall cover at least...").

This is the gate document required before any `JobTrack.Identity` or `JobTrack.Web` authentication
code is written (plan §8.2, §8.5 slice 1). It does not itself implement mitigations; it fixes what
each abuse case means for this system, which control closes it, and which test ID is authoritative
evidence that the control holds. Every row's test-ID column is filled in with the real fully
qualified test name as the corresponding slice (§8.5) is implemented, per the traceability rule in
`docs/traceability/test-catalogue.md` §1 — a row is added to that catalogue's §3 table at the same
time a row is added here, never after the fact.

## 1. System context

JobTrack is a single-organisation, employee-only, single-server deployment (ADR 0014). There is no
public registration and no external identity provider (spec §7.1). The threat actors in scope are:

- an unauthenticated network attacker (internet- or LAN-adjacent);
- an authenticated employee attempting to exceed their granted role/subtree scope;
- a former employee whose account has been disabled but who retains a live session or cached
  credential; and
- an operator or process with database access but without an application login (covers the
  database-credential-compromise and emergency-reset cases).

Out of scope for this document: physical security of the host, supply-chain compromise of NuGet
dependencies (covered by the dependency/security scan budget in §8.7), and attachments/user-authored
HTML (spec §16 lists this conditionally — the initial content model is plain text only, so it is not
assessed here and must be re-opened if attachments are ever introduced).

## 2. Assets

| Asset | Sensitivity | Where it lives |
|---|---|---|
| Password hash, security stamp, reset-grant hash | Secret — never leaves `JobTrack.Identity`'s credential store | Identity tables, isolated from `app_user` (spec §7.1) |
| Authentication cookie | Secret — bearer of an authenticated session | Browser only; server holds only the security-stamp-validated session claim |
| Rate/cost data | Sensitive — gated by Cost viewer/Rate manager permission | `JobTrack.Application` queries, `JobTrack.Web` HTTP API |
| Job hierarchy, work sessions, schedules | Internal — gated by role and subtree ownership | `JobTrack.Application` commands/queries |
| Audit log | Integrity-critical — append-only, secret-free (spec §16) | `JobTrack.Persistence.*` audit tables |
| Personal access token (PAT) hash, label, expiry/revocation state | Secret — bearer of an authenticated API session, equivalent sensitivity to the authentication cookie (ADR 0029) | New PAT store (`JobTrack.Persistence.*`); only a one-way hash of the high-entropy random token is ever persisted (unsalted SHA-256 — see ADR 0029's rationale) |

## 3. Abuse cases, mitigations, and test coverage

Each row is one of the abuse cases enumerated in plan §8.2 / spec §16. "Mitigation" cites the
control from plan §8.2's implementation list. "Test ID" is the catalogue identifier that will carry
the negative test (`docs/traceability/test-catalogue.md` §1 scheme); IDs already reserved there are
marked `(reserved)`, new IDs allocated by this document are marked `(new)`.

| # | Abuse case | Attack vector | Mitigation (plan §8.2) | Test ID |
|---|---|---|---|---|
| 1 | Credential stuffing | Scripted repeated login attempts against known/guessed username lists | Partitioned login/two-factor rate limiting keyed by remote address plus normalized username, with bounded lockout and generic failure responses | `TC-WEB-AUTHN-003` (new) |
| 2 | Account enumeration | Comparing login/reset response timing or content for existing vs. non-existing usernames | Generic failure messages that do not reveal account existence (spec §7.1) | `TC-WEB-AUTHN-004` (new) |
| 3 | Session theft | Cookie exfiltration (XSS, network capture, shared-device residue) reused after logout/disablement/password change | `Secure`/`HttpOnly`/`SameSite` cookies with bounded lifetime; security-stamp revocation on disablement, reset, password change, logout, and self-service 2FA changes | `TC-WEB-AUTHN-005` (new) |
| 4 | Authentication auditability | Authentication, logout, lockout, password-change, and 2FA events are missing from the append-only audit trail or leak secrets into it | Append-only authentication audit rows written for successful/failed login, lockout, logout, password change, and 2FA enable/disable/sign-in events; no password, TOTP, or secret material in the payload | `TC-WEB-AUTHN-008` (new) |
| 5 | Self-service 2FA consistency | Self-service 2FA enable/disable weakens protection while leaving bearer credentials active | 2FA enable/disable routes through the same revocation and audit posture as other credential-sensitive transitions; live PATs are revoked on both enable and disable | `TC-WEB-AUTHN-009` (new) |
| 6 | Cross-site request forgery | Third-party page submits a state-changing request using the victim's ambient cookie | Antiforgery tokens required on all state-changing browser requests | `TC-WEB-AUTHN-006` (new) |
| 7 | Cross-site scripting | Injecting script through any text field (job names, schedule notes, audit reasons) that is later rendered | Plain-text content model (no user-authored HTML); output encoding; restrictive CSP | `TC-WEB-AUTHN-007` (new) |
| 8 | Authorization bypass | Direct crafted HTTP request to an endpoint whose only enforcement is a hidden UI control | Named default-deny policy on every endpoint; direct-request negative tests, not UI-control tests (spec §7.3, plan §8.3) | `TC-WEB-AUTHZ-001` (new) |
| 9 | Insecure direct object reference | Substituting another user's/job's opaque identifier into an otherwise-authorized request | Opaque route binding to strongly typed identifiers; authorization re-evaluated against the resolved entity, not the caller's own default scope | `TC-WEB-AUTHZ-002` (new) |
| 10 | Subtree-scope confusion | A Worker who owns subtree A submits a request naming a node in sibling/ancestor subtree B | Every command reloads authoritative ownership/subtree scope inside the operation (plan §8.3), not from a cached or client-supplied scope claim | `TC-WEB-AUTHZ-003` (new) |
| 11 | Mass assignment | Extra/unexpected fields in a POST body attempting to set a field the endpoint does not expose (e.g. role, rate, achievement state) | Allow-listed, command-specific request models — no direct binding to domain/persistence entities | `TC-WEB-AUTHZ-004` (new) |
| 12 | Sensitive logging | Secrets or rate/cost data reaching logs, exception details, or audit before/after payloads | Password hashes, security stamps, and reset-grant hashes excluded from every log/telemetry/audit payload by construction (spec §16) | `TC-WEB-AUDIT-002` (new) |
| 13 | Database credential compromise | An actor with the application's own database login attempts to read Identity secret columns or bypass audit append-only enforcement directly | Least-privileged application role; secrets isolated from ordinary employee queries (spec §7.1); append-only audit enforcement (spec §16) | `TC-DB-ROLES-002` (new; extends `TC-DB-ROLES-001` scope to Identity secret columns) |
| 14 | Emergency reset abuse | The narrowly scoped emergency reset CLI/role (§8.6) used outside its intended single-use, audited, least-privileged path | Separate least-privileged operational database role; single-use short-lived grant; forced password change; session/prior-grant revocation; audited without printing the secret | `TC-WEB-IDENT-003` (new) |

Rows 1–7 and 14 are exercised as HTTP/browser tests once §8.5 slice 1 (sign-in, forced password
change, logout, access-denied) is implemented. Row 12 is a static/log-inspection check runnable as
soon as any authentication code exists. Row 13 is a database-role test alongside `TC-DB-ROLES-001`.

## 4. Traceability catalogue update

The rows above are appended to `docs/traceability/test-catalogue.md` §3 (new IDs marked `pending`
until each slice implements the test) at the same time as this document, per that catalogue's
maintenance rule (§5).

## 5. External HTTP API (non-browser client) abuse cases

Added per `docs/plans/2026-07-09-external-http-api-plan.md` §4.1, triggered by §6's original review
trigger ("a non-browser API consumer is introduced"). The consumer is the first-party remote CLI
authenticated with opaque bearer PATs (ADR 0029); scope is job browsing, work sessions,
prerequisites/achievement, and cost reports (ADR 0030). Rows 13–16 are assessed as in scope for this
release; rows 17–18 are explicitly out of scope until their triggering feature exists, matching this
document's existing pattern (§1) of naming an out-of-scope item rather than silently omitting it.

| # | Abuse case | Attack vector | Mitigation (plan §4.1, ADR 0029/0030) | Test ID |
|---|---|---|---|---|
| 13 | Token theft | A PAT is exfiltrated from a compromised CLI host, shell history, log file, or committed config | Only a one-way hash of the token is stored server-side (unsalted SHA-256; the token's own 256 bits of entropy make a per-token salt redundant — ADR 0029); the plaintext is shown once at issuance and never logged; the owner or an administrator can revoke a specific token on suspicion without affecting others | `TC-WEB-TOKEN-001` (new) |
| 14 | Token replay | A captured token, or an intercepted request, is reused after the legitimate call completes | Bearer tokens are only ever transmitted over the TLS connection terminated at the reverse proxy (plan §9.1); bounded maximum lifetime at issuance (ADR 0029) limits the replay window; last-used tracking lets an owner notice use they didn't make | `TC-WEB-TOKEN-002` (new) |
| 15 | Confused-deputy access via a PAT | A PAT is presented expecting it to carry broader or different capability than the issuing user's own authorization scope | A PAT authenticates strictly as its issuing user; the bearer scheme builds the same `CommandContext` construction the cookie scheme does, so role/ownership/subtree/data-sensitivity checks run identically regardless of which scheme authenticated the caller (ADR 0029) — there is no separate service-identity or elevated-scope grant a token can carry | `TC-WEB-TOKEN-003` (new) |
| 16 | Credential stuffing against token issuance | Scripted attempts to obtain a PAT by guessing/stuffing credentials against the issuance flow, bypassing existing login throttling | PAT issuance is itself a cookie-authenticated, antiforgery-protected action reached only after an existing successful sign-in — it inherits the login rate-limiting and lockout already covered by row 1, not a separate, less-protected issuance endpoint | `TC-WEB-TOKEN-004` (new) |
| 17 | Excessive polling / API abuse with a valid token | A valid token is used to send a high-volume or sustained request rate intended to exhaust resources or scrape data in bulk | Per-client/per-user rate limiting distinct from browser login throttling (plan §4.4), partitioned by the authenticated caller's own identity rather than a shared budget | `TC-WEB-TOKEN-005` = `JobTrack.Web.IntegrationTests.ApiOperationalQualitiesTests.Exceeding_the_per_user_api_rate_limit_returns_429_with_problem_details` and `.Two_different_users_each_get_their_own_rate_limit_budget` |
| 18 | Rate/cost data exfiltration over the API | A valid token belonging to a user without Cost viewer/Rate manager permission is used to attempt bulk retrieval of other users' cost or rate detail | Cost/rate queries reachable from the API enforce the same permission and same foreign-session non-exposure (ADR 0017) as the Razor Pages path; no additional discovery surface (e.g. a cross-user listing endpoint) is added by this release (ADR 0030) | `TC-WEB-TOKEN-006` (new) |
| 19 | Unauthenticated route/schema reconnaissance via OpenAPI | An anonymous network caller fetches `/openapi/v1.json` to enumerate every route, parameter, and schema without needing valid credentials first | `/openapi/v1.json` requires the same `AnyEmployee` policy as every other endpoint (cookie or bearer); an anonymous request is redirected to sign-in exactly like any other unauthenticated page request (security review remediation §2.5) | `TC-WEB-TOKEN-014` = `JobTrack.Web.IntegrationTests.OpenApiContractTests.An_unauthenticated_request_for_the_openapi_document_is_denied` |

Insecure direct object reference and subtree-scope confusion (rows 7–8) are not repeated here as new
rows: ADR 0029 establishes that bearer-authenticated requests are re-evaluated through the identical
`CommandContext`/authorization path as cookie-authenticated ones, so rows 7–8's existing test IDs are
re-run against bearer-authenticated requests as each API slice (§4.3) is implemented, not duplicated
under new IDs.

**Out of scope for this release** (re-open when the triggering feature is introduced, per §1's
pattern):

- **Audit scraping** — no non-browser consumer has audit-browsing access; that workflow stays
  Razor-Pages-only (ADR 0030). Re-open this row if audit browsing is ever added to the API surface.
- **Mobile device loss** — the named consumer for this release is a first-party CLI, not a mobile
  app (ADR 0029); there is no device-loss/local-storage-compromise scenario to assess yet. Re-open
  this row if a mobile client is introduced.

## 6. Traceability catalogue update (external API)

Rows 13–18 above are appended to `docs/traceability/test-catalogue.md` §3 (new `TOKEN` area tag,
IDs marked `pending`/`(new)` per row) at the same time as this document, per that catalogue's
maintenance rule (§5).

## 7. Requester intake abuse cases (ADR 0033)

Triggered by §9's own review trigger ("a new role or permission is added"): `EmployeeRole.Requester`
(ADR 0033) is the first role able to create job data without any of the six baseline roles'
operational authority, and the first role whose account holder may be an external-facing client
rather than technical staff. Rows 20–25 are assessed as in scope for
`docs/plans/2026-07-11-client-requester-intake-plan.md`'s implemented slice (Stages 1–7); the
deferred requester-notes/subtree-detail surface (plan status) is out of scope until that slice lands,
per §1's pattern of naming a deferral rather than silently omitting it.

| # | Abuse case | Attack vector | Mitigation (ADR 0033) | Test ID |
|---|---|---|---|---|
| 20 | Requester data isolation | A Requester lists or reads another requester's submitted request | `GetMyRequestsAsync`/`GET /api/requests` are unconditionally scoped to `job_request.requester_user_id = context.Actor` inside the port, not a client-supplied filter; there is no "list all requests" or "get request by id" surface reachable by a Requester at all | `TC-DB-REQ-007` = `JobRequestCommandPortContractTestsBase.A_requester_sees_only_their_own_submitted_requests_most_recent_first`; `TC-WEB-REQ-002` = `RequestsPageTests.A_requester_does_not_see_another_requesters_submitted_request` |
| 21 | Cross-role privilege escalation via Requester | A Requester account attempts to pick up a node, manage job structure, record a work session, or reach rate/cost/audit surfaces | `EmployeeRole.Requester` is absent from every allow-list `JobPickupPolicy`/`JobNodeAccessPolicy`/`WorkSessionAccessPolicy`/rate/cost/audit policies enumerate (excluded by construction, not a later carve-out — ADR 0033 Consequences); `AnyEmployee`'s role list is left unchanged rather than extended to include `Requester`, and the new `RequesterAccess`/`AnyAuthenticatedUser` policies are additive, not a relaxation of any existing policy | `TC-APP-REQ-001`-style policy tests in `tests/JobTrack.Domain.Tests/Authorization/*PolicyTests.cs` (`A_requester_may_never_manage_a_node...`, `.A_requester_may_never_manage_a_session...`, `.A_requester_may_not_view_costs`, `.A_requester_may_not_manage_rates`, `.A_requester_may_not_search_audit_history`); `TC-WEB-REQ-002` = `RequestsPageTests.A_requester_cannot_reach_the_operational_job_browse_page`; `TC-WEB-REQ-003` = `RequestsApiTests.A_requester_cannot_call_the_operational_job_root_endpoint`, `.A_worker_cannot_call_the_requests_endpoints` |
| 22 | Mass assignment via request submission | A caller adds `ownerUserId`, `parentId`, `kind`, or `priority` fields to the submit body, attempting to bypass the holding area's configured defaults | `SubmitJobRequestRequest`/`SubmitRequestBody` are allow-listed (`Description`, `HoldingAreaId`, optional `WriteUp`/`RequesterReference` only); the port itself derives parent/owner/kind/priority from the holding area's own row, never from the request body, so an extra JSON field has no binding target to land on | `TC-WEB-REQ-003` = `RequestsApiTests.Extra_fields_in_the_submit_body_have_no_effect_beyond_the_allow_listed_fields` |
| 23 | Department-scoped holding-area bypass | A Requester submits into (or lists as eligible) a holding area scoped to a department they do not belong to | `RequesterAccessPolicy.CanSubmit`/`GetEligibleHoldingAreasAsync` evaluate `department_id IS NULL OR EXISTS (app_user_department membership)` authoritatively inside the transaction/query, not from a client-supplied department claim | `TC-DB-REQ-002` = `JobRequestCommandPortContractTestsBase.A_requester_cannot_submit_into_a_holding_area_scoped_to_a_department_they_do_not_belong_to`; `TC-DB-REQ-006` = `.Eligible_holding_areas_include_globally_eligible_ones_and_exclude_inactive_or_unrelated_department_ones` |
| 24 | Cross-site request forgery on request submission | A third-party page submits `POST /Requests` or `POST /api/requests` using the victim Requester's ambient cookie | Identical antiforgery enforcement as every other mutation (Razor Pages' built-in form-field token; `AntiforgeryValidationFilter` on the API route) — no bespoke exemption was added for the requester surface; `GET /api/antiforgery-token` moved to `AnyAuthenticatedUser` specifically so a Requester can still obtain a token to satisfy this same check (ADR 0033 Consequences), not to weaken it | Covered by the same mechanism as row 4 (`TC-WEB-AUTHN-006`); `RequestsApiTests.A_requester_can_submit_a_request_via_a_bearer_token_without_an_antiforgery_token` proves the bearer path is correctly exempt for the same reason row 16's PAT-issuance analysis applies (no ambient credential to forge) |
| 25 | Cross-site scripting in request text | Injecting script markup through the requester-supplied `Description`, later rendered on `/Requests` | Plain-text content model unchanged for requester input; Razor's default output encoding applies identically to requester-authored text as to any other job description (row 5) | `TC-WEB-REQ-002` = `RequestsPageTests.A_description_containing_script_markup_is_rendered_html_encoded_not_as_live_markup` |

Rate limiting (row 17) and cost/rate exfiltration (row 18) are not repeated as new rows: `/api/requests`
and `/api/request-holding-areas` sit inside the same `api` route group and inherit the identical
per-caller `RequireRateLimiting(RateLimiterPolicyName)` policy every other endpoint uses, and the
requester surface exposes no rate/cost data at all (`RequestResponse`/`HoldingAreaResponse` carry no
monetary or rate field), so row 18's exfiltration vector does not apply to it by construction.

**Out of scope for this release** (re-open when the triggering feature is introduced, per §1's
pattern):

- **Requester-visible staff notes / read-only subtree** — no notes/comments schema exists yet (plan
  status); re-open this row when that slice adds a write surface for staff-authored text a Requester
  can read, since that introduces a new author/reader trust boundary this document has not assessed.
- **Attachments on a request** — explicitly out of scope for the whole plan (plan §11); re-open per
  this document's existing attachments deferral (§1) if ever introduced.

## 8. Traceability catalogue update (requester intake)

Rows 20–25 above correspond to rows already present in `docs/traceability/test-catalogue.md` §3 under
the `REQ` area tag (added alongside ADR 0033); this section records the abuse-case mapping for those
existing rows rather than allocating new test IDs, per this document's own maintenance rule (§4/§6).

## 9. Review trigger

Re-review this document whenever: a new role or permission is added (§8.3) — most recently satisfied
by §7/§8's `Requester` review — the content model stops being plain text (attachments/rich text), a
new non-browser client class or authentication mechanism is introduced beyond the first-party
CLI/PAT scope covered by §5 (e.g. OAuth 2.1, mobile, M2M), the API's exposure scope changes
(structural commands, audit browsing, or account administration added per a new ADR), the deferred
requester-notes/subtree-detail surface (§7's out-of-scope note) is implemented, or an abuse case is
found in review that is not covered by a row above.
