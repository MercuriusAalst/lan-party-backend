## MODIFIED Requirements

### Requirement: Tournament publishes lifecycle events
Tournament mutations MUST publish Tournament-owned integration events through the durable module
eventing boundary in the same transaction as their persisted state.

#### Scenario: Tournament lifecycle changes
- **WHEN** a tournament is created, updated, canceled, or deleted
- **THEN** the corresponding Tournament integration event is added to the durable outbox

#### Scenario: Unconsumed tournament facts are not published
- **WHEN** a tournament is started, reset, or completed, a placement is assigned, a match result is recorded or reversed, a registration is created or canceled, or a roster member is confirmed
- **THEN** no durable integration event is added for that fact

## ADDED Requirements

### Requirement: Tournament deletion removes its sponsor placement
Tournament deletion MUST remove the tournament's sponsor placement through the Sponsorship module
contract in the same database transaction as the tournament delete.

#### Scenario: Tournament with a sponsor placement is deleted
- **WHEN** a tournament that has a sponsor placement is deleted
- **THEN** Sponsorship removes the placement and publishes its placement-removed event
- **AND** the placement removal, tournament delete, and their outbox messages commit together

#### Scenario: Tournament deletion fails
- **WHEN** the tournament delete does not commit
- **THEN** the sponsor placement MUST remain
