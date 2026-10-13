# Enforce cross-module data consistency

## Why

Cross-module foreign keys cascaded deletes into tables owned by other modules, so a delete in one module could silently remove another module's rows. Team versions were bumped for every team event but never checked, so concurrent team writes could interleave. Twelve module events were written to the outbox without any consumer.

## What Changes

- Make the cross-module foreign keys `team_members`/`team_invites`/`placement_users` → `users`, `placement_teams` → `teams` and `tournament_sponsor_placements` → `tournaments` `ON DELETE RESTRICT`. Owning modules clean up explicitly: Tournament deletion removes its sponsor placement through the Sponsorship module contract in the same transaction; users are anonymized and teams soft-deleted, never hard-deleted.
- Make `Team.Version` an optimistic concurrency token; a concurrent team modification returns `409 Conflict` with code `team_changed`.
- Stop publishing MatchCompleted, MatchResultReversed, PlacementAssigned, RosterMemberConfirmed, TeamMemberAdded, TeamMemberRemoved, TeamCaptainTransferred, TournamentStarted, TournamentCompleted, TournamentReset, TournamentRegistrationCreated and TournamentRegistrationCanceled. Already-stored outbox rows of these types are acknowledged without dispatch. Team versions now advance only on create, rename and delete. SignalR realtime messages are unchanged.

## Impact

- Migration `20261010081503_RestrictCrossModuleDeletes`.
- Capabilities: `module-persistence-boundaries`, `module-eventing`, `tournament-module-boundary`, `sponsorship-module-boundary`, `user-owned-team-management`.
- No HTTP route or JSON shape changes beyond the new `team_changed` conflict.
