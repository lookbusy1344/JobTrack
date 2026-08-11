# Requester user guide

A Requester is someone who can submit work for the operational team and follow its progress, but
cannot be assigned technical work or record work sessions. Requester accounts are created by an
administrator; there is no public registration.

## Signing in

Sign in with the username and password supplied by an administrator. After signing in, a Requester
is taken directly to **My requests**. The authenticated header gives a Requester these links:

- **My requests** — submit and track requests;
- **API tokens** — create or revoke tokens for the requester HTTP API;
- **Two-factor** — configure authenticator-app sign-in; and
- **Sign out**.

## Submitting a request

The **My requests** page contains a **New request** form:

1. Describe the problem or required work.
2. Choose where to send it from the available holding areas.
3. Select **Submit request**.

The description and holding area are required. The form does not currently accept attachments,
priority, deadlines, or a preferred assignee. Staff triage the submitted request and decide how the
technical work should be organised and assigned.

Only active holding areas for which the account is eligible appear in the list. If none are
available, the page says to contact an administrator.

After a successful submission, the request appears under **Your requests**. This list shows the
description and submission time, newest first. Times are displayed in the Requester's configured
time zone.

## Following progress

Select a request's description to open its detail page. The page shows:

- the requester's display name and login username;
- the request's current public status;
- when it was submitted and, once staff accept it, when it was acknowledged;
- a read-only **Progress** tree with time worked; and
- requester-visible **Notes**.

Select **Back** to return to **My requests**.

Staff may split one request into several technical jobs. The Progress table draws these as a folder
and leaf tree, matching the structure staff use in Browse. Each row places its public-status icon
immediately after the job name, followed by **Time worked** and last-updated time. Time is displayed
to one decimal place. A leaf's time is the work recorded on that subsection; a folder's time is the
total of the leaves below it. Concurrent work is allocated fairly rather than double-counted.

Time worked is an aggregate only. Internal ownership, individual work sessions, worker identities,
rates, costs, schedules, and audit information are not shown.

The public statuses are:

| Status | Meaning |
|---|---|
| **Submitted** | The request has been received but not yet acknowledged by staff. |
| **Accepted** | Staff have acknowledged it, but actionable work has not started. |
| **Waiting** | Actionable work exists but has not started. |
| **In Progress** | At least one part of the request has work under way. |
| **Completed** | Every part of the request completed successfully. |
| **Cancelled** | Every part ended without a successful outcome. |

## Notes and questions

Use **Add note** on the detail page to provide clarification or ask for an update. A note written by
a Requester is visible to that Requester and to staff.

Staff can publish notes for the Requester or keep operational notes private. The requester page
shows only published requester-visible notes, marked **Public** beside the time. Staff also see
operational notes, marked **Private**. The page does not currently show the author's name.

## Access boundaries

A Requester can see only requests submitted by their own account. They cannot:

- open another Requester's request;
- browse the operational job tree;
- edit, move, decompose, assign, pick up, or perform technical work;
- view individual work sessions, employees, schedules, rates, costs, or audit records; or
- edit or delete a request after submitting it.

There is not currently a requester-facing cancellation or closure action. Add a note to ask staff
to cancel or close a request.
