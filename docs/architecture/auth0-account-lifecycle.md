# Auth0 Account Deletion Operations

LAN account deletion commits locally first. It keeps the historical LAN user ID and related references, anonymizes the profile, detaches the Auth0 subject, and queues provider cleanup. The delete endpoints return HTTP 202 with `deletionPending: true`; this confirms that local deletion and durable cleanup scheduling succeeded, not that Auth0 has already removed the account.

## Configuration and Auth0 permissions

Configure the Auth0 Management API authority, audience, client ID, and client secret, with `read:users` and `delete:users` grants. Missing Management API configuration does not mark cleanup complete: jobs remain pending and retry.

The exact provider subject is stored only in the private `identity.auth0_account_cleanups` row while cleanup is pending. It is never returned by public or operator JSON and is cleared atomically when cleanup completes. Database access, backups, and replica credentials therefore form the protection boundary for this temporary value.

## Deployment and migration

The lifecycle migration is unreleased and is amended to the final one-table schema rather than layered over a shared deployment. During rollout, drain and stop every old API binary before applying/activating the new version and its legacy backfill. An old binary does not enforce cleanup-history cutoffs, so a mixed-version deployment could accept an old token during subject reuse. Start the new binary after the Management API configuration is available. The backfill processes at most 100 legacy deleted users per batch, detaches their old Auth0 subject under the same database locks used by account creation/deletion, and resumes after restart; it retries failures in the background and exits after a successful empty batch. Until a legacy row is detached, old tokens for it are still rejected, and registration remains blocked.

After a user has been detached or a new incarnation has been registered, roll forward only. Do not roll back to a binary that lacks cleanup-history enforcement or remove permanent cleanup history: an old JWT could otherwise resolve to a replacement account. This deployment follows the repository's single-API-replica policy in [single-instance-deployment.md](single-instance-deployment.md).

## Permanent lifecycle metadata

Completed rows in `identity.auth0_account_cleanups` are permanent security metadata. Their `SubjectHash` and `CompletedAtUtc` values are the durable source for the subject token cutoff, while an unfinished row is the durable pending marker. Outbox retention changes must not delete these rows or cascade from an outbox record into cleanup history. The `SubjectHash` index remains required for lifecycle reads.

## Monitoring and retry

Use the admin-only endpoints:

- `GET /v1/lan/users/auth0-cleanups?page=1&pageSize=50` reports counts and pages cleanup jobs, prioritizing unfinished jobs. It includes local user/job IDs, status, retry attempts, timestamps, and sanitized error/configuration information; it never returns the provider subject. `legacyPendingCount` identifies deleted historical rows not yet detached.
- `POST /v1/lan/users/auth0-cleanups/{cleanupId}/retry` resets an eligible dead-lettered outbox message for dispatch. It returns 202 when queued and 404 when the job is missing, already complete, or not currently retryable.

The shared outbox retries failures up to five attempts with exponential delays starting at 5 seconds and capped at 5 minutes, then dead-letters the message. Provider outages, missing permissions/configuration, or a missing encryption key should be corrected before retrying. Check status again after retry; do not use raw subjects in support tickets or logs. Auth0 HTTP instrumentation suppresses spans for these requests because provider user URLs contain the subject.

## Provider generation limitation

Cleanup reads Auth0's `created_at` before deleting and records the observed generation. On a retry, a different generation is treated as a recreated account and preserved. A missing Auth0 user is treated as already cleaned up. Auth0 does not provide an atomic conditional delete by `created_at`, so another party can recreate the same subject between our GET and DELETE; that external time-of-check/time-of-use race cannot be eliminated by this service. The permanent cleanup history still prevents stale LAN tokens from inheriting the replacement profile, memberships, or roles.
