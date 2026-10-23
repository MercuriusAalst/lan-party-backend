# Account Lifecycle Specification

## Purpose
TBD - created by archiving change auth0-account-lifecycle. Update Purpose after archive.
## Requirements
### Requirement: Durable account cleanup

Deleting a LAN user MUST preserve its local identifier and historical references, anonymize its profile, detach its active provider subject, and atomically persist a durable cleanup job with the local deletion. The historical row MUST NOT retain the reusable subject. Cleanup MUST target only the exact provider subject stored in the private cleanup row. A 204 or 404 MUST complete cleanup; missing configuration and transient provider failures MUST remain pending and retry through bounded durable delivery. Pending provider subjects MUST be hidden from logs and public/operator responses and erased after completion.

Lifecycle orchestration MUST depend on a narrow provider-neutral account port for reading account creation generation and deleting an exact subject. The configured adapter MUST implement that port without exposing provider-specific request exceptions or interfaces to the cleanup handler.

#### Scenario: Any deletion route queues cleanup

- WHEN a user is deleted through self-service or any administrator route
- THEN the local anonymization, subject detachment, and exact-subject cleanup job commit atomically
- AND the response is HTTP 202 with deletionPending: true

#### Scenario: Provider cleanup succeeds or is already complete

- WHEN the configured provider returns 204 or 404 for the exact stored subject
- THEN the pending subject is erased and cleanup is marked complete

#### Scenario: Provider cleanup fails

- WHEN configuration is missing or the configured provider returns a retryable error, times out, or cannot be reached
- THEN durable bounded retry remains observable and the API does not claim cleanup completed

### Requirement: Reused-subject account incarnation

A deleted subject MUST remain fenced by its cleanup history after cleanup. While an unfinished cleanup row exists, registration MUST NOT bind that subject to a new local user. After cleanup completes, the latest completed cleanup row MUST reject tokens whose iat predates the completion-second-plus-one cutoff. A subject with completed cleanup history and a missing or malformed iat MUST be rejected. First-time registration MUST NOT be rejected because its token predates creation of the LAN profile. Local account creation and deletion MUST coordinate through the same database subject lock. Authenticated requests and hub invocations MUST remain bound to the local user incarnation that was authenticated at their start; a later subject lookup MUST NOT substitute a newly registered user.

Completed cleanup rows, including their subject hash and completion time, MUST remain permanent security metadata. Retention or outbox maintenance MUST NOT delete these rows or cascade deletion into them.

#### Scenario: Recreated social subject receives a fresh LAN identity

- WHEN a new external identity uses a subject from a completed deleted account and presents a token issued at or after the cleanup cutoff
- THEN registration creates a new local user ID with no inherited profile, memberships, or permissions

#### Scenario: Old or malformed token replays a fenced subject

- WHEN an older token, or a fenced-subject token without a valid iat, calls a protected API, opens a hub connection, or invokes a hub method
- THEN authentication is rejected before user-bound work begins

#### Scenario: In-flight request races with deletion and registration

- WHEN an authenticated request or hub invocation began for a prior or missing local identity and deletion/re-registration occurs before a later subject lookup
- THEN identity resolution rejects the changed or newly substituted local account incarnation

### Requirement: Safe cleanup operations

An administrator with access to the private application database MUST be able to inspect cleanup and safely retry only an unfinished dead-lettered job using documented SQL and local identifiers. The system MUST NOT require a dedicated cleanup API or CLI. Retrying a completed or actively claimed cleanup MUST NOT delete or duplicate provider work. Cleanup MUST compare the provider creation generation observed before deletion with its value on retry where available. The system MUST document that a provider GET followed by DELETE cannot be atomic and retains a time-of-check/time-of-use race.

#### Scenario: Operator inspects cleanup

- WHEN an administrator runs the documented database status query
- THEN it returns local user/job IDs, status, attempts, timestamps, and sanitized errors without the provider subject or hash

#### Scenario: Operator retries cleanup

- WHEN an administrator runs the documented database retry statement for an unfinished dead-lettered cleanup
- THEN existing outbox retry mechanics resume it and a completed job is not dispatched again

