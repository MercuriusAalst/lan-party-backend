## MODIFIED Requirements

### Requirement: Transactional event publication
The system SHALL save supported business mutations and their durable outbox messages in the same database commit.

#### Scenario: Business change and event commit together
- **WHEN** a supported Teams lifecycle mutation succeeds
- **THEN** the Teams state change and its durable outbox message MUST both be committed

#### Scenario: Event enqueue failure prevents commit
- **WHEN** a supported Teams lifecycle mutation cannot enqueue its durable integration event
- **THEN** the Teams state change MUST NOT be committed

#### Scenario: Sponsorship change and event commit together
- **WHEN** Sponsorship creates, updates, or deletes a sponsor
- **THEN** the Sponsorship state change and its matching durable outbox message MUST both be
  committed

## ADDED Requirements

### Requirement: Retired event types are acknowledged
Integration events that no consumer handles SHALL NOT be published. Outbox rows already stored with a retired event type, including `Mercurius.Modules.Identity.Contracts.UserAnonymizedIntegrationEvent` and `Mercurius.Modules.Sponsorship.Contracts.V1.TournamentSponsorPlacementChanged` (and its legacy `GameSponsorPlacementChanged` alias), MUST be marked processed without dispatch, retry, or dead-lettering.

#### Scenario: Account deletion publishes only consumed events
- **WHEN** a user account is anonymized
- **THEN** the system MUST publish the user deleted and profile changed events
- **AND** it MUST NOT publish a `UserAnonymizedIntegrationEvent`

#### Scenario: Stored retired row is acknowledged
- **WHEN** the dispatcher claims an outbox row whose event type is retired
- **THEN** it MUST mark the row processed without invoking handlers or dead-lettering it
