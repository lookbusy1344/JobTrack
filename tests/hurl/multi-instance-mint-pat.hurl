# ADR 0066 Stage 7 helper: signs in one already-provisioned account (completing its forced
# first-sign-in password change, ADR 0023) against one host and mints one bearer PAT, printing only
# the plaintext token on the last line. Invoked twice by scripts/multi-instance-test.sh, once per
# race actor, each run getting its own fresh cookie jar -- unlike
# tests/hurl/multi-instance-scenario.hurl this never needs to carry a cookie across hosts, so it
# takes a single `host` variable rather than `host_a`/`host_b`.
#
#   hurl --test --json --header "X-Forwarded-Proto: https" \
#        --variable host=http://localhost:8081 --variable username=... --variable password=... \
#        --variable new_password=... --variable label=... \
#        tests/hurl/multi-instance-mint-pat.hurl

GET {{host}}/Account/Login
HTTP 200
[Captures]
login_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{host}}/Account/Login
[FormParams]
__RequestVerificationToken: {{login_token}}
Input.UserName: {{username}}
Input.Password: {{password}}
HTTP 302
[Asserts]
header "Location" contains "ChangePassword"

GET {{host}}/Account/ChangePassword
HTTP 200
[Captures]
change_password_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"

POST {{host}}/Account/ChangePassword
[FormParams]
__RequestVerificationToken: {{change_password_token}}
Input.CurrentPassword: {{password}}
Input.NewPassword: {{new_password}}
Input.ConfirmNewPassword: {{new_password}}
HTTP 302
[Asserts]
header "Location" == "/"

GET {{host}}/Account/PersonalAccessTokens
HTTP 200
[Captures]
issue_token: xpath "string(//form[@action='/Account/PersonalAccessTokens?handler=Issue']//input[@name='__RequestVerificationToken']/@value)"

POST {{host}}/Account/PersonalAccessTokens?handler=Issue
[FormParams]
__RequestVerificationToken: {{issue_token}}
Issue.Label: {{label}}
Issue.LifetimeDays: 1
HTTP 302
[Captures]
issued_pat_location: header "Location"

GET {{host}}{{issued_pat_location}}
HTTP 200
[Captures]
pat_token: xpath "string(//code/text())"
[Asserts]
xpath "string(//code/text())" matches "^\\S+$"
