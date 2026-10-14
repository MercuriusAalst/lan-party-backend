## MODIFIED Requirements

### Requirement: Versioned integration events
The system SHALL use versioned durable event payloads for Teams and Sponsorship lifecycle facts.

#### Scenario: Teams publishes versioned lifecycle events
- **WHEN** Teams creates, renames, or deletes a team
- **THEN** the corresponding durable integration event payload MUST include the Team id and current monotonic Team version
- **AND** only these mutations MUST advance the Team version

#### Scenario: Membership and captain changes publish no durable event
- **WHEN** Teams adds or removes a member or transfers captain ownership
- **THEN** it MUST NOT publish a durable integration event
- **AND** its SignalR realtime messages MUST remain unchanged

#### Scenario: Stale version does not overwrite newer projection
- **WHEN** a consumer receives a Teams integration event whose version is older than the stored projection version
- **THEN** the consumer MUST ignore the stale event and keep the newer projection data

#### Scenario: Sponsorship publishes versioned lifecycle events
- **WHEN** Sponsorship creates, updates, or deletes a sponsor or creates, replaces, or removes a
  tournament sponsor placement
- **THEN** it MUST publish the matching V1 event payload
- **AND** the payload MUST include the SponsorId for sponsor facts and the TournamentId plus current
  placement facts or removal state for placement facts

## ADDED Requirements

### Requirement: Retired module events are acknowledged
The system SHALL NOT publish module events that have no consumer: MatchCompleted, MatchResultReversed, PlacementAssigned, RosterMemberConfirmed, TeamMemberAdded, TeamMemberRemoved, TeamCaptainTransferred, TournamentStarted, TournamentCompleted, TournamentReset, TournamentRegistrationCreated and TournamentRegistrationCanceled. The dispatcher SHALL mark already-stored outbox rows of these retired types, including their legacy Competition aliases, processed without resolving a payload type or invoking handlers.

#### Scenario: Stored retired event is dispatched
- **WHEN** an eligible outbox row has a retired event type
- **THEN** the dispatcher MUST record it as processed without invoking any handler
- **AND** it MUST NOT record a failed delivery attempt or dead-letter the row
