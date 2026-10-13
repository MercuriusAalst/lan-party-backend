## Purpose
Define the durable module eventing infrastructure used for transactional outbox publication, in-process dispatch, shared inbox idempotency, and versioned Teams integration events.
## Requirements
### Requirement: Durable module event persistence
The system SHALL persist module integration events to a Platform-owned outbox before dispatching them to consumers.

#### Scenario: Event is saved to outbox
- **WHEN** a module publishes a durable integration event as part of a supported business mutation
- **THEN** the system MUST save an outbox message with a message id, registered event type, serialized payload, occurrence timestamp, retry count, and processing state

#### Scenario: Public API behavior is unchanged
- **WHEN** durable module eventing is introduced
- **THEN** existing public routes, authorization rules, request DTOs, response DTOs, JSON shapes, SignalR group names, SignalR client method names, and realtime payload timing MUST remain unchanged

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

### Requirement: In-process outbox dispatch
The system SHALL provide one hosted in-process dispatcher that resolves registered handlers and processes a deterministic bounded set of eligible outbox messages.

#### Scenario: Dispatcher isolates message tracking
- **WHEN** the dispatcher selects a batch of eligible messages
- **THEN** it MUST select their identifiers without tracking in occurrence-time and identifier order
- **AND** it MUST load and process each selected message independently

#### Scenario: Earlier failure does not detach later work
- **WHEN** an earlier selected message fails and failure recovery clears tracked state
- **THEN** the dispatcher MUST still load every later selected identifier independently
- **AND** a later successful message MUST persist its processed timestamp

#### Scenario: Dispatcher invokes handlers
- **WHEN** an eligible outbox message has a registered payload type and matching handlers
- **THEN** the dispatcher MUST deserialize the payload and invoke handlers in their registered order
- **AND** it MUST mark the outbox message processed only after all handlers complete successfully

#### Scenario: Dispatch cancellation propagates
- **WHEN** dispatch is cancelled
- **THEN** the dispatcher MUST propagate cancellation without recording it as a failed delivery attempt

### Requirement: Exclusive recoverable outbox claims
The system SHALL atomically claim an eligible outbox message before invoking any handler and MUST allow no more than one active claim for the same message.

#### Scenario: Overlapping dispatchers compete for one message
- **WHEN** two dispatchers attempt to claim the same eligible outbox message concurrently
- **THEN** exactly one dispatcher MUST acquire the active claim
- **AND** only the claim owner MUST invoke handlers for that dispatch attempt

#### Scenario: Interrupted claim expires
- **WHEN** a dispatcher stops after claiming a message without recording completion or failure
- **THEN** the message MUST remain unavailable until the claim lease expires
- **AND** the message MUST become eligible for a later at-least-once dispatch after expiry

#### Scenario: Dispatch releases its claim
- **WHEN** the claim owner records successful completion or a failed delivery attempt
- **THEN** the system MUST clear the claim token and lease expiry as part of that state update

#### Scenario: Expired owner cannot overwrite later state
- **WHEN** a dispatcher's lease expires and another dispatcher claims the same message
- **THEN** the expired dispatcher MUST NOT persist completion or failure state for the later owner's claim

### Requirement: Inbox idempotency
The system SHALL suppress duplicate consumer handling with a shared Platform inbox keyed by logical consumer name and message id.

#### Scenario: Duplicate event is ignored
- **WHEN** a consumer has already processed a message id
- **THEN** the dispatcher MUST skip invoking that consumer for the duplicate message

#### Scenario: Retry does not duplicate side effects
- **WHEN** an outbox message is retried after at least one consumer has already processed it
- **THEN** previously completed consumers MUST NOT run again for the same message id

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

### Requirement: Bounded retry and dead-letter lifecycle
The system SHALL retry failed outbox messages on a deterministic capped schedule and SHALL stop automatic delivery after five failed attempts.

#### Scenario: Failure records a deferred retry
- **WHEN** a handler fails before the fifth failed attempt
- **THEN** the dispatcher MUST increment the retry count and record the attempt time and truncated error
- **AND** it MUST schedule the next attempt after a deterministic delay that does not exceed the internal cap
- **AND** an immediate subsequent dispatch MUST NOT select that message

#### Scenario: Exhausted message becomes dead-lettered
- **WHEN** the fifth delivery attempt fails
- **THEN** the dispatcher MUST record a dead-letter timestamp and clear its next-attempt timestamp
- **AND** automatic dispatch MUST NOT select that message again

#### Scenario: Poison messages do not starve later work
- **WHEN** older poison messages fill or cross a bounded dispatch batch
- **THEN** their deferred or dead-lettered state MUST exclude them from later eligible selections
- **AND** later healthy eligible messages MUST continue to be dispatched

### Requirement: At-least-once module event delivery
The system SHALL describe durable in-process module event dispatch as at-least-once delivery and MUST rely on inbox idempotency or handler-level idempotency for repeated attempts.

#### Scenario: Completion persistence can follow handler effects
- **WHEN** a process stops after a handler effect but before durable message completion is recorded
- **THEN** a later dispatch MAY attempt the message again
- **AND** the system MUST NOT represent delivery as exactly once

#### Scenario: Terminal records remain available
- **WHEN** an outbox message succeeds or becomes dead-lettered
- **THEN** the system MUST retain its outbox row and associated inbox markers unless a separate manual operation removes them

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
