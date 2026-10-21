## OpenSpec

- [x] Write the proposal, tasks and spec deltas and validate them strictly.

## Teams

- [x] Add nullable `TeamMember.JoinedAtUtc` with an additive migration; set it on team creation and invite acceptance, never on captain transfer, and do not expose it in DTOs.
- [x] Re-read identity state under the user's identity mutation lock and reject deleted users in team creation, invite creation, invite acceptance, captain transfer and admin captain change.
- [x] Exclude deleted members and captains from live team projections, pending invite projections, Teams contract snapshots, eligibility, captained-team lookups and the captain limit.
- [x] Add the Teams `UserDeletedIntegrationEvent` handler: per team, in one locked transaction, remove the membership, cancel pending invites, run captain succession and publish `TeamCaptainSucceededIntegrationEvent`.
- [x] Implement succession ordering (legacy cohort by account creation, then join time, then user id) with deleted and captain-limit-ineligible candidates skipped, and no captain when nobody is eligible.

## Tournament

- [x] Re-read identity state under the user's identity mutation lock in individual registration, roster submission and roster confirmation.
- [ ] Add the Tournament `UserDeletedIntegrationEvent` and `TeamCaptainSucceededIntegrationEvent` handlers that apply the scheduled/in-progress/completed policy under tournament revision concurrency.

## Realtime and reconciliation

- [ ] Deny team realtime subscriptions to deleted members and captains.
- [ ] Add the single-claim, idempotent startup reconciliation for Teams and Tournament with structured logging of teams left without an eligible successor.

## Verification

- [ ] Add regression tests for the issue acceptance list.
- [ ] Run strict OpenSpec validation, synchronize the live specs and archive this change.
