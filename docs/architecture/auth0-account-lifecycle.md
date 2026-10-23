# Account Lifecycle Operations

LAN account deletion commits locally first. It keeps the historical LAN user ID and related references, anonymizes the profile, detaches the active provider subject, and queues provider cleanup. The delete endpoints return HTTP 202 with `deletionPending`, which is true while provider cleanup remains unfinished. It can be false after cleanup has already completed or on an idempotent repeat; the response confirms local handling.

## Provider boundary

Lifecycle orchestration, cleanup persistence, token freshness, events and the cleanup handler are provider-neutral. They depend on `IIdentityProviderAccountService`, a narrow port with account-creation-generation lookup and exact-subject account deletion. `Auth0ManagementService` implements the port as the current adapter through its existing typed HTTP client. Auth0 login, profile, role, verification-email and password-reset operations remain Auth0-specific and are outside this boundary.

Configure the current Auth0 adapter with Management API authority, audience, client ID, and client secret, with `read:users` and `delete:users` grants. Missing Management API configuration does not mark cleanup complete: jobs remain pending and retry through the existing outbox.

## Configuration

The exact provider subject is stored only in the private `identity.account_cleanups` row while cleanup is pending. It is cleared atomically when cleanup completes and is never written to outbox payloads, public or administrator responses, logs, exceptions, or telemetry. Database access, backups, and replica credentials form the protection boundary for this temporary value, matching the existing active-identity data boundary.

Completed cleanup rows are permanent security metadata. Their subject hash and completion time drive provider cleanup state and token rejection, while the exact subject is empty. Outbox retention must not delete these rows or cascade into cleanup history.

## Schema

This lifecycle implementation targets a fresh test environment and starts with the new cleanup schema. The initial migration creates the single `identity.account_cleanups` table; it does not repair pre-existing rows, enqueue legacy work, or provide an upgrade path. Do not reset a database as part of this code change.

## Database operator runbook

There is no cleanup status or retry HTTP API and no dedicated cleanup CLI. Operators with authorized access to the private application database use SQL; database access is an explicit operational trust assumption.

This status query returns local identifiers, lifecycle timestamps, sanitized outbox state, and retry timing. It never selects the exact subject, subject hash, outbox payload, or provider URL.

```sql
SELECT
    c."Id" AS cleanup_id,
    c."UserId" AS user_id,
    c."RequestedAtUtc",
    c."CompletedAtUtc",
    CASE
        WHEN c."CompletedAtUtc" IS NOT NULL THEN 'completed'
        WHEN m.id IS NULL THEN 'missing-outbox'
        WHEN m.processed_at_utc IS NOT NULL THEN 'processed-without-completion'
        WHEN m.dead_lettered_at_utc IS NOT NULL THEN 'dead-lettered'
        WHEN m.claim_expires_at_utc > now() THEN 'claimed'
        WHEN m.next_attempt_at_utc > now() THEN 'scheduled'
        ELSE 'pending'
    END AS status,
    m.retry_count,
    m.next_attempt_at_utc,
    COALESCE(c."SanitizedOutcome", m.last_error) AS sanitized_outcome
FROM identity.account_cleanups AS c
LEFT JOIN platform.outbox_messages AS m ON m.id = c."OutboxMessageId"
ORDER BY c."CompletedAtUtc" IS NOT NULL, c."RequestedAtUtc" DESC, c."Id"
LIMIT 100;
```

The shared outbox retries failures with exponential delays and then dead-letters the event; the existing `mercurius.outbox.dead_lettered` metric remains the alerting signal. Repair provider outage, permissions, or configuration before retrying. Set `cleanup_id` to a local row ID from the status query. One returned ID means the dead-lettered unfinished job was requeued; zero means the row is missing, complete, actively claimed, processed, or otherwise ineligible.

For example, in `psql`: `\set cleanup_id 'UUID_FROM_STATUS_QUERY'`.

<!-- cleanup-retry-sql:start -->
```sql
BEGIN;
SET LOCAL lock_timeout = '5s';
WITH eligible AS MATERIALIZED (
    SELECT m.id
    FROM platform.outbox_messages AS m
    JOIN identity.account_cleanups AS c ON c."OutboxMessageId" = m.id
    WHERE c."Id" = :'cleanup_id'::uuid
      AND c."CompletedAtUtc" IS NULL
      AND m.event_type = 'Mercurius.Modules.Identity.Contracts.AccountCleanupRequestedIntegrationEvent'
      AND m.processed_at_utc IS NULL
      AND m.dead_lettered_at_utc IS NOT NULL
      AND (m.claim_expires_at_utc IS NULL OR m.claim_expires_at_utc <= now())
    FOR UPDATE OF m, c
)
UPDATE platform.outbox_messages AS m
SET retry_count = 0,
    last_attempt_at_utc = NULL,
    next_attempt_at_utc = now(),
    dead_lettered_at_utc = NULL,
    last_error = NULL
FROM eligible AS e
WHERE m.id = e.id
RETURNING m.id;
COMMIT;
```
<!-- cleanup-retry-sql:end -->

Never reset processed or actively claimed work, inbox markers, or cleanup completion timestamps. Never delete lifecycle history. Do not include raw subjects in tickets or logs.

## Provider generation limitation

Cleanup reads the provider account creation time before deleting and records the observed generation. On a retry, a different generation is treated as a recreated account and preserved. A missing account is treated as already cleaned up. The current Auth0 adapter cannot make its GET and DELETE atomic, so another party can recreate the same subject between them; that external time-of-check/time-of-use race cannot be eliminated by this service. Permanent cleanup history still prevents stale LAN tokens from inheriting the replacement profile, memberships, or roles.
