# Remove deleted users from live team and tournament participation

Issue #159. Deleting (anonymizing) a user account leaves the anonymized user in `team_members`, as a team captain, as the recipient of pending invites and on scheduled tournament rosters. A deleted captain blocks the whole team: nobody can transfer captainship, invite, register or manage the roster, and the deleted user still counts as an actionable participant. Nothing prevents a deleted user from being made captain.

## Scope

- Teams and Tournament each consume the existing durable `UserDeletedIntegrationEvent` from the outbox. Identity keeps writing only its own tables.
- Teams removes the deleted user from every live team, cancels the user's pending invites (keeping the invite history) and, when the deleted user was captain, automatically appoints the longest-serving eligible member or leaves the team without a captain when nobody is eligible. Teams publishes an additive durable `TeamCaptainSucceededIntegrationEvent` for each automatic succession.
- Team membership gets a nullable join timestamp (`JoinedAtUtc`) recorded at team creation and invite acceptance. Rows that existed before the migration stay `null` (legacy) and are ordered by account creation time. The timestamp is never exposed in a DTO.
- Every write path that can add a user to a team, make a user captain, or add a user to a tournament registration re-reads current identity state under the user's identity mutation lock and rejects deleted users; deleted actors are rejected.
- Live team projections, Teams contract snapshots and eligibility exclude deleted members and deleted captains. Historical tournament, match and placement views keep the anonymized "Deleted user" placeholder.
- Tournament policy: Scheduled tournaments drop the deleted user's individual registration and roster rows; a roster led by the deleted captain is re-pointed to the successor (registration back to pending confirmation) or removed when the team has no successor. In-progress tournaments keep registrations, rosters and matches for bracket integrity. Completed and canceled tournaments are untouched.
- The team realtime authorizer denies deleted members and captains.
- An idempotent, single-claim startup reconciliation repairs data left behind by deletions that happened before this change.

## Non-goals

- No frontend change, admin captain-reassignment flow, hard delete of users or history, change to `IIdentityModule.GetUsersByIdsAsync` filtering, invitation-history reconstruction or join-date backfill.

## Rolling deployment and migration

The migration only adds a nullable column (`teams.team_members.JoinedAtUtc`). Route and DTO shapes are unchanged. Cleanup is driven by the existing outbox and is idempotent, so an old instance that does not consume the event is harmless; the reconciliation repairs anything an old instance writes during the rollout. The reconciliation takes a PostgreSQL advisory lock, so only one replica runs it at a time.
