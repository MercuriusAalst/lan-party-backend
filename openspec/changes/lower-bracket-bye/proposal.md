# Propagate and recover lower bracket byes

Lower-bracket matches that automatically advance a lone participant currently do not propagate that winner to the next match. Add propagation for generated brackets and an administrator-only, guarded operation to recover affected tournaments that already exist.

## Scope

- Automatically advancing a participant through a BYE MUST continue winner propagation through subsequent BYE matches.
- An administrator MAY request recovery with `POST /v1/lan/tournaments/{tournamentId}/matches/recover-bye-advancement`. Recovery MUST be transactional and idempotent, only fill empty slots in unplayed matches, and fail without saving when any eligible downstream slot is occupied by a different participant or has a result.
- Recovery MUST NOT regenerate the bracket or alter played matches or results. It MUST return HTTP 200 with `{ "matchesUpdated": number }`, where zero means no changes were needed. A conflict MUST return HTTP 409 without saving any changes.

## Non-goals

- No automatic production-data changes, migration, or frontend work.

## Compatibility and safety

The endpoint is additive and admin-only. Recovery loads one tournament's existing match graph and saves only new participant/result assignments in one database transaction. Optimistic concurrency conflicts fail the request without retrying stale state.
