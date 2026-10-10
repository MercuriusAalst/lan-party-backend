# enforce-cross-module-data-consistency

## Cross-module deletes

- [x] Switch the five cross-module foreign keys to `ON DELETE RESTRICT` and add migration `20261010081503_RestrictCrossModuleDeletes`.
- [x] Remove the tournament's sponsor placement through the Sponsorship module in the tournament-delete transaction; let the Sponsorship outbox writer join an ambient transaction.
- [x] Cover restrict delete behaviour, user anonymization, team soft-delete and tournament deletion with a sponsor placement in tests.

## Team concurrency

- [x] Mark `Team.Version` as a concurrency token and translate `DbUpdateConcurrencyException` from Teams saves to `409 team_changed`.
- [x] Add a regression test for a concurrent team version change.

## Retired events

- [x] Remove the twelve unconsumed event contracts and their publication.
- [x] Acknowledge stored outbox rows of retired types (including legacy Competition aliases) without dispatch.
- [x] Update eventing and team version tests.
