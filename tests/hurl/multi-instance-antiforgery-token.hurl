# ADR 0066 Stage 7 helper: captures one antiforgery cookie+token pair for scripts/multi-instance-test.sh's
# raw-curl rate-limit loop, which needs to POST the same account-less /Account/Login form repeatedly.
# Run with --cookie-jar so the caller can hand the resulting jar straight to curl -b/-c afterwards --
# hurl and curl both drive libcurl's cookie engine, so the Netscape-format jar file is interchangeable
# between the two tools.
#
#   hurl --json --header "X-Forwarded-Proto: https" --cookie-jar jar.txt \
#        --variable host=http://localhost:8081 tests/hurl/multi-instance-antiforgery-token.hurl

GET {{host}}/Account/Login
HTTP 200
[Captures]
login_token: xpath "string(//input[@name='__RequestVerificationToken']/@value)"
