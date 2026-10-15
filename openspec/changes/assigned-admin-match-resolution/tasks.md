## Backend implementation

- [x] Require an assigned admin on tournament create and reject clearing it on update; keep the column nullable with no migration or backfill and require a supplied admin on the next update of a legacy tournament.
- [x] Replace single-report auto-accept with the `AdminResolutionRequired` transition that retains the reported score without making it final.
- [x] Add the additive `Reason` to `MatchResolutionRequiredIntegrationEvent` and publish it once per transition for both disputed and missing-opponent-report matches.
- [x] Add the admin-only `GET /lan/matches/pending-resolution` endpoint, service and `PendingMatchResolutionDTO`, falling back to every admin for tournaments without an assigned admin.
- [x] Add regression tests for mandatory admin, the new transition and event reason, the pending list visibility rules, and clearing after resolution.
- [x] Run strict OpenSpec validation.
- [ ] Run the full verification, synchronize the live specs, and archive this change after review.
