# Require an assigned admin and route match resolution to them

Every tournament currently may be created without an assigned admin, and a match whose opponent never reports a score is silently decided in favour of the first reporter once the five-minute window expires. That lets one side win a match without an administrator ever seeing it. This change makes the assigned admin mandatory and sends every match that needs a human decision to that admin.

## Scope

- Tournament create MUST supply an assigned administrator; update MUST NOT clear it. Legacy tournaments without one keep working, and the next update MUST supply one. The database column stays nullable and no backfill is performed.
- A single unconfirmed report is no longer auto-accepted. When its five-minute window expires the match enters `AdminResolutionRequired`, keeping the reported score as the first side's claim, never as a final result.
- `MatchResolutionRequiredIntegrationEvent` gains an additive `Reason` (`Disputed` or `MissingOpponentReport`) and is published once per transition into `AdminResolutionRequired`, for both reasons.
- New admin-only `GET /lan/matches/pending-resolution` returns the matches in `AdminResolutionRequired` that the calling admin is responsible for (assigned to them, or in a tournament without an assigned admin). It is derived from persisted match state and clears itself when the match is resolved.

## Non-goals

- No migration, backfill, e-mail, push or SignalR delivery, frontend work, change to who may resolve a match, Auth0 change, or change to the five-minute durations.

## Rolling deployment

Persisted deadlines and the existing outbox are reused. The event field is additive and defaults to `Disputed` when absent. During a rollout an old instance may still auto-accept a single report; the new behaviour does not depend on exclusivity.
