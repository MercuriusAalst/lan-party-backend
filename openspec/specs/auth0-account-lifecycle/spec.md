# auth0-account-lifecycle Specification

## Purpose
TBD - created by archiving change auth0-account-lifecycle. Update Purpose after archive.
## Requirements
### Requirement: Durable Auth0 cleanup

Deleting a LAN user MUST preserve its local identifier and historical references, anonymize its profile, detach its active provider subject, and atomically persist a durable cleanup job with the local deletion. The historical row MUST NOT retain the reusable subject. Cleanup MUST target only the exact Auth0 subject stored in the private cleanup row. A 204 or 404 MUST complete cleanup; missing configuration and transient provider failures MUST remain pending and retry through bounded durable delivery. Pending provider subjects MUST be hidden from logs and public/operator responses and erased after completion. Existing deleted rows MUST be backfilled resumably without a raw-subject SQL transform.

#### Scenario: Any deletion route queues cleanup

- WHEN a user is deleted through self-service or any administrator route
- THEN the local anonymization, subject detachment, and exact-subject cleanup job commit atomically
- AND the response is HTTP 202 with deletionPending: true

#### Scenario: Provider cleanup succeeds or is already complete

- WHEN Auth0 returns 204 or 404 for the exact stored subject
- THEN the pending subject is erased and cleanup is marked complete

#### Scenario: Provider cleanup fails

- WHEN configuration is missing or Auth0 returns a retryable error, times out, or cannot be reached
- THEN durable bounded retry remains observable and the API does not claim cleanup completed

#### Scenario: Legacy deleted user is backfilled

- WHEN a deleted historical row still contains an Auth0 subject
- THEN application backfill stores the subject in the private pending cleanup row and detaches the row resumably without logging or exposing the subject

### Requirement: Reused-subject account incarnation

A deleted subject MUST remain fenced by its cleanup history after cleanup. While an unfinished cleanup row exists, registration MUST NOT bind that subject to a new local user. After cleanup completes, the latest completed cleanup row MUST reject tokens whose iat predates the completion-second-plus-one cutoff. A subject with completed cleanup history and a missing or malformed iat MUST be rejected. First-time registration MUST NOT be rejected because its token predates creation of the LAN profile. Local account creation and deletion MUST coordinate through the same database subject lock. Authenticated requests and hub invocations MUST remain bound to the local user incarnation that was authenticated at their start; a later subject lookup MUST NOT substitute a newly registered user.

Completed cleanup rows, including their subject hash and completion time, MUST remain permanent security metadata. Retention or outbox maintenance MUST NOT delete these rows or cascade deletion into them.

#### Scenario: Recreated social subject receives a fresh LAN identity

- WHEN a new Auth0 identity uses a subject from a completed deleted account and presents a token issued at or after the cleanup cutoff
- THEN registration creates a new local user ID with no inherited profile, memberships, or permissions

#### Scenario: Old or malformed token replays a fenced subject

- WHEN an older token, or a fenced-subject token without a valid iat, calls a protected API, opens a hub connection, or invokes a hub method
- THEN authentication is rejected before user-bound work begins

#### Scenario: In-flight request races with deletion and registration

- WHEN an authenticated request or hub invocation began for a prior or missing local identity and deletion/re-registration occurs before a later subject lookup
- THEN identity resolution rejects the changed or newly substituted local account incarnation

### Requirement: Safe cleanup operations

An administrator MUST be able to inspect and retry unfinished cleanup using local identifiers and sanitized status only. Retrying a completed cleanup MUST NOT delete a recreated provider identity. Cleanup MUST compare the provider creation generation observed before deletion with its value on retry where available. The system MUST document that a provider GET followed by DELETE cannot be atomic and retains a time-of-check/time-of-use race.

#### Scenario: Operator inspects cleanup

- WHEN an administrator requests cleanup status
- THEN the response contains local user/job IDs, status, attempts, timestamps, and sanitized errors without the provider subject

#### Scenario: Operator retries cleanup

- WHEN an administrator retries an unfinished dead-lettered cleanup
- THEN existing outbox retry mechanics resume it and a completed job is not dispatched again

