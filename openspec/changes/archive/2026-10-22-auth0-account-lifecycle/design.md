# Design

# Design

## Identity lifecycle

Keep the historical User.Id stable, but remove its active Auth0 mapping during deletion. A unique, non-authenticatable tombstone preserves the existing non-null unique index. The cleanup row stores the exact provider subject in the private application database while deletion is pending and clears it atomically when completion commits.

Pending state is any unfinished cleanup row for the subject hash. The token cutoff is derived from the latest completed cleanup row at its completion second plus one. Completed rows, their hashes, and completion timestamps remain permanent security metadata. The same database subject advisory lock coordinates registration, deletion, and cleanup completion.

The subject hash remains SHA-256 over the exact UTF-8 subject in lowercase hexadecimal. It supports indexed lookups and advisory-lock keys and is not a secrecy mechanism.

## Cleanup delivery

Insert the cleanup row and user-ID-only internal event through the existing leased outbox in the same local transaction as anonymization. Dispatch through the existing outbox handler and retry/dead-letter flow. DELETE only the stored exact Auth0 subject after URL escaping, accept 204/404, and compare provider creation generation before destructive retries. No Auth0 GET/DELETE sequence can eliminate its external time-of-check/time-of-use race.

The initial lifecycle migration only creates the cleanup schema. It does not inspect or transform pre-feature rows, enqueue legacy work, or provide an upgrade path.

## Authentication and registration

Validate a token's subject and iat after JWT validation and before authenticated API execution. Reject missing or malformed iat for a subject with cleanup history and reject values earlier than the derived cutoff. Pin the internal user ID for the request and each hub invocation, and enforce it on later identity resolution so concurrent deletion and registration cannot substitute accounts.

Creation and deletion acquire the same database subject advisory lock and recheck cleanup state and cutoff in the registration transaction. Ordinary first registration does not compare iat to profile creation time.

## Rollout and contract

All four deletion routes return HTTP 202 with { message, deletionPending: true } after the local transaction commits. /users/me retains its 404 onboarding and 410 deleted-session behavior. There are no Auth0 cleanup status/retry HTTP routes.

Operators use documented database SQL against the private database to inspect local cleanup IDs and sanitized outbox state and safely requeue only dead-lettered unfinished jobs. No dedicated operator CLI exists. The database and backup trust boundary protects the temporary exact subject; it is never written to outbox payloads, DTOs, logs, exceptions or telemetry.

The lifecycle schema targets a fresh test environment and starts with the new implementation. There is no data repair, compatibility path, or mixed-version upgrade workflow. Only the repository's documented single API replica is supported.
