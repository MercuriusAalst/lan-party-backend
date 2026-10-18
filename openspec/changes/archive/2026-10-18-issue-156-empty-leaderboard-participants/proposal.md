## Why

Deleting a participant's final leaderboard attempt currently leaves an empty participant persisted. This keeps the participant in the admin attempt-history response and prevents a linked LAN user from being recorded again because linked identities are unique per tournament.

## What Changes

When an admin removes a participant's final attempt, remove the participant from the tournament aggregate as part of the same existing transactional mutation. Existing cascade relationships delete its removed attempt and preserve placement cleanup behavior. Non-final removals retain the participant and remaining attempts. Public leaderboard filtering, response shapes, routes, and concurrency handling stay unchanged.

## Scope

- Remove a participant only after its final attempt is successfully removed.
- Preserve validation, optimistic concurrency, transaction and leaderboard revision behavior.
- Verify persisted admin/public projections, linked-user re-recording, guest removal, non-final removal, and stale writes.
- Make no API or schema changes and do not bulk-clean pre-existing empty participants.
