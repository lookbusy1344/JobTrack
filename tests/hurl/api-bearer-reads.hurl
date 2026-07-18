# Exercises the read side of the external HTTP API's job-context surface with a real bearer PAT --
# every one of these routes only requires JobTrackPolicyNames.AnyEmployee, so any employee's token
# works regardless of role. See docs/testing/hurl-smoke-tests.md for how {{token}} is minted
# (JobTrack.AdminCli's `issue-token` command, run against an account whose forced first-sign-in
# password change has already completed -- see web-login-and-csrf.hurl).
#
#   hurl --variable base_url=http://localhost:5034 --variable token=<pat> tests/hurl/api-bearer-reads.hurl

GET {{base_url}}/api/jobs/root
Authorization: Bearer {{token}}
HTTP 200
Content-Type: application/json; charset=utf-8
[Asserts]
jsonpath "$.node.id" exists
jsonpath "$.node.parentId" == null
jsonpath "$.node.kind" == "Root"
jsonpath "$.ancestors" count == 0
[Captures]
root_id: jsonpath "$.node.id"

GET {{base_url}}/api/jobs/{{root_id}}
Authorization: Bearer {{token}}
HTTP 200
[Asserts]
jsonpath "$.node.id" == {{root_id}}

GET {{base_url}}/api/jobs/{{root_id}}/children
Authorization: Bearer {{token}}
HTTP 200
[Asserts]
jsonpath "$.orderedBy" exists
jsonpath "$.items" isCollection

GET {{base_url}}/api/jobs/{{root_id}}/readiness
Authorization: Bearer {{token}}
HTTP 200
[Asserts]
jsonpath "$.isReady" exists

GET {{base_url}}/api/jobs/search?searchText=e
Authorization: Bearer {{token}}
HTTP 200
[Asserts]
jsonpath "$.items" isCollection
jsonpath "$.orderedBy" exists

# A nonexistent node still authenticates fine with the same token -- proves the 404 is a genuine
# not-found, not an authentication failure in disguise.
GET {{base_url}}/api/jobs/999999999
Authorization: Bearer {{token}}
HTTP 404
Content-Type: application/problem+json
[Asserts]
jsonpath "$.type" == "/problems/entity-not-found"
