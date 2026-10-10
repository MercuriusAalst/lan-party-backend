## Purpose

Define Tournament module ownership, explicit module dependencies, event publication, and HTTP stability.
## Requirements
### Requirement: Tournament owns tournament lifecycle implementation
The Tournament module MUST own tournament, match, placement, tournament registration, roster member,
and bracket implementation types and MUST expose only intentional contracts and composition
extensions publicly. The module/project/namespace and composition names MUST use Tournament
terminology for the tournament aggregate.

#### Scenario: API composes Tournament
- **WHEN** the API application starts
- **THEN** it registers Tournament services through `AddTournamentModule`
- **AND** maps all tournament, match, and registration routes through `MapTournamentModule`

#### Scenario: Implementation types remain internal
- **WHEN** another module references Tournament
- **THEN** Tournament entities, EF adapters, bracket moderators, and application services are not
  available as public API

### Requirement: Tournament uses explicit module contracts
The Tournament module MUST use Teams, Identity, Sponsorship, and Media contracts for facts owned by
those capabilities and MUST NOT reference their implementation projects, EF entities, DbContexts,
repositories, or internal services. Aggregate-level contracts MUST use Tournament terminology and
`TournamentId` while genuine single-game concepts remain unchanged.

#### Scenario: Team registration eligibility is evaluated
- **WHEN** Tournament validates a team roster
- **THEN** it obtains team authority and roster data through `Teams.Contracts`
- **AND** stores only external IDs and historical snapshots needed by Tournament

#### Scenario: User display data is required
- **WHEN** Tournament builds registration or placement read models
- **THEN** it obtains current privacy-safe user data through bounded `Identity.Contracts` lookups
- **AND** does not load Identity EF entities

### Requirement: Tournament publishes lifecycle events
Tournament mutations MUST publish Tournament-owned integration events through the durable module
eventing boundary in the same transaction as their persisted state.

#### Scenario: Tournament lifecycle changes
- **WHEN** a tournament is created, updated, canceled, or deleted
- **THEN** the corresponding Tournament integration event is added to the durable outbox

#### Scenario: Unconsumed tournament facts are not published
- **WHEN** a tournament is started, reset, or completed, a placement is assigned, a match result is recorded or reversed, a registration is created or canceled, or a roster member is confirmed
- **THEN** no durable integration event is added for that fact

### Requirement: Existing HTTP contracts remain stable
The rename MUST preserve the existing Tournament route authorization metadata, antiforgery
metadata, request and response JSON shapes, and validation outcomes except for the required
in-place resource and identifier rename from `/v1/lan/games/{gameId}` to
`/v1/lan/tournaments/{tournamentId}`.

#### Scenario: Existing client migrates to Tournament endpoints
- **WHEN** a client calls a canonical tournament, match, or tournament-registration endpoint
- **THEN** the corresponding existing authorization requirement applies
- **AND** the response semantics remain unchanged with aggregate-level `tournamentId` fields
- **AND** the old `/v1/lan/games` route is not exposed

### Requirement: Tournament coordinates Tournament image lifecycle
Tournament MUST compensate a newly stored Tournament image if the following Tournament mutation or commit
fails or is cancelled, and MUST retire replaced or deleted owned Tournament images only after the Tournament
state and durable lifecycle event commit succeeds.

#### Scenario: Tournament image mutation fails before commit
- **WHEN** Tournament stores a Tournament image and the owning Tournament mutation or durable-event commit does not succeed
- **THEN** Tournament attempts non-cancelled deletion of only the new image and preserves the original failure

#### Scenario: Tournament replacement or deletion commits
- **WHEN** a Tournament image is replaced or a Tournament is deleted successfully
- **THEN** Tournament attempts eligible prior-image deletion after the Tournament state and durable event commit

### Requirement: Tournament provides bounded historical Team-logo reference decisions
Tournament MUST implement the existing Teams read-contract boundary with one cancellation-aware,
no-tracking existence query over all `TournamentRegistration.TeamLogoUrlAtRegistration` values.
It MUST NOT expose Tournament entities, repositories, or queryables through the contract.

#### Scenario: Teams evaluates historical logo retention
- **WHEN** Teams asks whether a non-blank logo URL is retained by tournament history
- **THEN** Tournament executes one bounded existence query over all tournament-registration snapshots
- **AND** returns whether an ordinally equal stored URL exists

### Requirement: Durable tournament event compatibility
The rename migration MUST rewrite pending, non-dead-lettered events written with legacy
Competition/Game CLR type names or legacy `gameId` JSON payload keys to Tournament CLR type names
and `tournamentId` JSON payload keys, and its `Down` operation MUST reverse those rewrites. The
durable event dispatcher MAY retain only a focused alias/normalization path for historical,
dead-lettered, or rollback rows that cannot be rewritten safely. Newly published events MUST use
Tournament CLR type names and `tournamentId` JSON keys.

#### Scenario: Legacy durable event survives the rename migration
- **WHEN** an unprocessed, non-dead-lettered outbox row contains a legacy Competition/Game type string and `gameId` payload before the migration
- **THEN** the migration rewrites its type string to the corresponding Tournament contract
- **AND** rewrites its aggregate identifier key to `tournamentId`
- **AND** its `Down` operation restores the legacy type string and `gameId` key

#### Scenario: Historical or dead-lettered legacy event is dispatched
- **WHEN** a historical, dead-lettered, or rollback outbox row still contains a legacy Competition/Game type string and `gameId` payload
- **THEN** the focused dispatcher compatibility path resolves and normalizes it to the corresponding Tournament contract without exposing an HTTP compatibility route

#### Scenario: New event uses canonical naming
- **WHEN** Tournament publishes a lifecycle or registration event after the rename
- **THEN** its durable type registration and JSON payload use Tournament terminology and `tournamentId`

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
