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

### Requirement: Retired module events are acknowledged
The system SHALL NOT publish module events that have no consumer: MatchCompleted, MatchResultReversed, PlacementAssigned, RosterMemberConfirmed, TeamMemberAdded, TeamMemberRemoved, TeamCaptainTransferred, TournamentStarted, TournamentCompleted, TournamentReset, TournamentRegistrationCreated, TournamentRegistrationCanceled, UserAnonymized and Sponsorship `Contracts.V1.TournamentSponsorPlacementChanged`. The dispatcher SHALL mark already-stored outbox rows of these retired types, including their legacy Competition and `GameSponsorPlacementChanged` aliases, processed without resolving a payload type or invoking handlers.

#### Scenario: Stored retired event is dispatched
- **WHEN** an eligible outbox row has a retired event type
- **THEN** the dispatcher MUST record it as processed without invoking any handler
- **AND** it MUST NOT record a failed delivery attempt or dead-letter the row

#### Scenario: Account deletion publishes only consumed events
- **WHEN** a user account is anonymized
- **THEN** the system MUST publish the user deleted and profile changed events
- **AND** it MUST NOT publish a `UserAnonymizedIntegrationEvent`
