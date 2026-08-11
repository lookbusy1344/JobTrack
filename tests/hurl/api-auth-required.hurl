# Confirms every /api/* route (and the gated OpenAPI document) rejects an unauthenticated or
# bearer-garbage caller with the exact 401 problem+json shape the cookie and bearer schemes both
# promise (JobTrackApi.AuthenticationProblemType) -- see docs/testing/hurl-smoke-tests.md.
#
# No seed data or running credentials required: run against any deployed JobTrack.Web host.
#
#   hurl --variable base_url=http://localhost:5034 tests/hurl/api-auth-required.hurl

GET {{base_url}}/api/jobs/root
HTTP 401
Content-Type: application/problem+json
[Asserts]
jsonpath "$.type" == "/problems/authentication-required"
jsonpath "$.status" == 401

# /openapi/*, unlike /api/*, is outside JobTrackApi's IsApiRequest check, so an anonymous browser
# navigation redirects to sign-in rather than getting problem+json -- a bearer-shaped request still
# gets the JSON shape, proven below.
GET {{base_url}}/openapi/v1.json
HTTP 302
[Asserts]
header "Location" contains "/Account/Login"

GET {{base_url}}/openapi/v1.json
Authorization: Bearer not-a-real-token
HTTP 401
Content-Type: application/problem+json
[Asserts]
jsonpath "$.type" == "/problems/authentication-required"

GET {{base_url}}/api/jobs/root
Authorization: Bearer not-a-real-token
HTTP 401
Content-Type: application/problem+json
[Asserts]
jsonpath "$.type" == "/problems/authentication-required"

GET {{base_url}}/api/antiforgery-token
HTTP 401
Content-Type: application/problem+json
[Asserts]
jsonpath "$.type" == "/problems/authentication-required"
