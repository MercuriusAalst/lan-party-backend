# Design

## Identity lifecycle

Keep the historical User.Id stable, but remove its active Auth0 mapping during deletion. A unique, non-authenticatable tombstone preserves the existing non-null unique index. A persistent SHA-256 subject fence is created under a database-coordinated subject lock. Pending cleanup blocks a new local registration; completion records a strict iat cutoff at the completion second plus one.

Keep the provider subject only in a private, encrypted pending-cleanup payload. Use a durable key shared by deployed instances. Clear the ciphertext on success. Existing deleted rows are migrated into resumable pending cleanup rows, with their existing Auth0 IDs encrypted by the application backfill rather than transformed in SQL.

## Cleanup delivery

Insert the user-ID-only internal event, fence, and encrypted cleanup payload in the same local transaction as anonymization. Dispatch through the existing leased outbox and bounded retry/dead-letter flow. DELETE the stored exact Auth0 subject after URL escaping, accept 204/404, and capture provider creation generation before destructive dispatch; retries compare the observed generation to avoid deleting a newly observed recreation. No Auth0 GET/DELETE sequence can eliminate its external time-of-check/time-of-use race; document it. Operator retry reuses existing dead-letter semantics and never resurrects a completed payload.

## Authentication and registration

Validate a token's subject and iat after JWT validation and before authenticated API execution. Reject missing or malformed iat for a fenced subject and reject values earlier than the durable cutoff. Pin the internal user ID for the request and each hub invocation, and enforce it on later identity resolution so concurrent deletion and registration cannot substitute accounts. Hub connection and invocation checks use the identity-owned scoped context.

Creation and deletion acquire the same database subject advisory lock and recheck the fence and token cutoff in the registration transaction. Ordinary first registration does not compare iat to profile creation time. Existing deletion mutation locks remain in place.

## Rollout and contract

All four deletion routes return HTTP 202 with { message, deletionPending: true } after the local transaction commits. /users/me retains its 404 onboarding and 410 deleted-session behavior. An admin-only status/retry endpoint exposes local IDs, status, counts, times, and sanitized errors only. Deployments must stop/drain binaries that do not enforce the subject fence before activating legacy backfill or allowing new registrations; a database migration alone cannot make mixed-version token checks safe. Only the repository's documented single API replica is supported during rolling deployment.