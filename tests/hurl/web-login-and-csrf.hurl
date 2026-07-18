# Drives the real cookie + antiforgery flow every Razor Page form uses: GET the login page and
# capture its auto-injected __RequestVerificationToken hidden field, POST credentials, follow the
# mandatory forced-password-change redirect every freshly seeded/bootstrapped account gets
# (JobTrackIdentityUser.RequiresPasswordChange -- see RequiresPasswordChangePageFilter), then use
# the resulting session cookie plus a GET /api/antiforgery-token capture to make one CSRF-protected
# JSON write through the external HTTP API's cookie-authenticated path.
#
# Assumes a freshly seeded account (samples/JobTrack.UatSeed) that has never signed in before --
# this suite is not re-runnable against the same account without reseeding, since the forced
# password change it drives is a one-time transition. See docs/testing/hurl-smoke-tests.md.
#
#   hurl --variable base_url=http://localhost:5034 --variable username=priya.manager \
#        --variable password=Uat-Seed-Battery-42! --variable new_password=Hurl-Smoke-Battery-42! \
#        tests/hurl/web-login-and-csrf.hurl

GET {{base_url}}/Account/Login
HTTP 200
[Captures]
login_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{base_url}}/Account/Login
[FormParams]
__RequestVerificationToken: {{login_token}}
Input.UserName: {{username}}
Input.Password: {{password}}
HTTP 302
[Asserts]
header "Location" contains "ChangePassword"

GET {{base_url}}/Account/ChangePassword
HTTP 200
[Captures]
change_password_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{base_url}}/Account/ChangePassword
[FormParams]
__RequestVerificationToken: {{change_password_token}}
Input.CurrentPassword: {{password}}
Input.NewPassword: {{new_password}}
Input.ConfirmNewPassword: {{new_password}}
HTTP 302
[Asserts]
header "Location" == "/"

# The forced change already refreshed the session cookie in place (SignInManager.RefreshSignInAsync)
# -- no re-login needed. Find the UAT seed's unowned pickup-pool leaf ("Replace failing UPS
# battery"), then claim it: a real CSRF-protected JSON write through the cookie-authenticated path,
# using the antiforgery cookie + X-CSRF-TOKEN header pair every same-origin API write needs.
GET {{base_url}}/api/antiforgery-token
HTTP 200
Content-Type: application/json; charset=utf-8
[Captures]
csrf_token: jsonpath "$.token"

GET {{base_url}}/api/jobs/search?searchText=UPS%20battery
HTTP 200
[Asserts]
jsonpath "$.items" count == 1
[Captures]
pool_leaf_id: jsonpath "$.items[0].id"

POST {{base_url}}/api/jobs/{{pool_leaf_id}}/pickup
X-CSRF-TOKEN: {{csrf_token}}
HTTP 200
Content-Type: application/json; charset=utf-8
[Asserts]
jsonpath "$.id" == {{pool_leaf_id}}
jsonpath "$.ownerUserId" isInteger
