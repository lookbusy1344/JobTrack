# Post-deploy smoke check for the Razor Pages web interface: the anonymous pages render, and every
# protected page correctly bounces a signed-out visitor to sign-in rather than 500ing or leaking
# content. No seed data, credentials, or database state required -- safe to run against any
# environment right after deploy. See docs/testing/hurl-smoke-tests.md.
#
#   hurl --variable base_url=http://localhost:5034 tests/hurl/web-smoke.hurl

GET {{base_url}}/Account/Login
HTTP 200
Content-Type: text/html; charset=utf-8
[Asserts]
xpath "string(//h1)" == "Sign in"
xpath "//input[@name='__RequestVerificationToken']" count == 1

# The landing page carries no content of its own for a signed-out visitor -- it redirects straight
# to sign-in (JobTrack.Web/Pages/Index.cshtml.cs).
GET {{base_url}}/
HTTP 302
[Asserts]
header "Location" contains "/Account/Login"

# A deep-linked protected page redirects to sign-in with a return URL rather than exposing any job
# content or erroring.
GET {{base_url}}/Jobs/Browse
HTTP 302
[Asserts]
header "Location" contains "/Account/Login"

# Security headers (spec §16 / remediation plan) are present on every response, not only API ones.
GET {{base_url}}/Account/Login
HTTP 200
[Asserts]
header "X-Content-Type-Options" == "nosniff"
header "X-Frame-Options" exists
header "Content-Security-Policy" exists
