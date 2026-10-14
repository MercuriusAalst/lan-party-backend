## Purpose

Define Sponsorship module ownership, boundaries, persistence, HTTP composition, and lifecycle facts.
## Requirements
### Requirement: Sponsorship owns sponsor and placement implementation
The Sponsorship module SHALL own the Sponsor and TournamentSponsorPlacement implementation types,
application services, validation, endpoint mapping, and persistence configuration. Its public
surface MUST be limited to intentional contracts and composition extensions.

#### Scenario: API composes Sponsorship
- **WHEN** the API application starts
- **THEN** it MUST register Sponsorship through `AddSponsorshipModule`
- **AND** map the existing sponsor routes through `MapSponsorshipModule`

#### Scenario: Sponsorship implementation remains internal
- **WHEN** another module references Sponsorship
- **THEN** Sponsor entities, EF adapters, CRUD services, and endpoint implementation types MUST
  NOT be available through the Sponsorship contracts project

### Requirement: Sponsorship uses explicit module dependencies
Sponsorship SHALL consume Tournament facts only through external tournament identifiers and SHALL
consume image storage only through `Media.Contracts`. It MUST NOT reference Tournament or Media
implementation projects, their EF entities, DbContexts, repositories, or internal services.

#### Scenario: Sponsor placement is changed for a tournament
- **WHEN** Tournament calls `ISponsorshipModule` to replace a tournament's sponsor placement
- **THEN** Sponsorship MUST store the supplied tournament identifier as an external reference
- **AND** it MUST NOT load or navigate to a Tournament entity

#### Scenario: Sponsor logo is stored
- **WHEN** a sponsor is created or its logo is replaced
- **THEN** Sponsorship MUST store the media URL returned through `IMediaModule`
- **AND** it MUST NOT invoke a Media implementation service directly

### Requirement: Sponsorship owns tournament sponsor placement behavior
Sponsorship SHALL own the one-to-one placement for each tournament, including sponsor identity, context,
headline, support line, and display order. A placement change MUST validate a non-null sponsor
before persisting it and MUST preserve the existing not-found validation outcome.

#### Scenario: Placement is created or replaced
- **WHEN** a valid `SponsorPlacementInput` is supplied for a tournament identifier
- **THEN** Sponsorship MUST create or update that tournament's single placement with the supplied values
- **AND** the corresponding Tournament read model MUST receive the placement through
  `ISponsorshipModule`

#### Scenario: Placement is removed
- **WHEN** `null` is supplied as the placement for a tournament identifier
- **THEN** Sponsorship MUST remove that tournament's existing placement
- **AND** subsequent Sponsorship and Tournament reads MUST report no placement

### Requirement: Sponsorship read models are bounded and persistence-compatible
Sponsorship read operations SHALL use no-tracking projections and SHALL provide a bounded batched
placement lookup for a supplied set of tournament identifiers. It MUST retain the existing
`Sponsors` and `TournamentSponsorPlacements` tables, scalar mapping, and unique tournament placement
constraint after the schema rename. The sponsor-to-placement relationship MUST cascade and the
tournament-to-placement relationship MUST restrict deletes.

#### Scenario: Tournament enriches multiple tournaments
- **WHEN** Tournament requests placements for multiple tournament identifiers
- **THEN** Sponsorship MUST return the matching placement summaries from a bounded query
- **AND** it MUST NOT issue one placement query per tournament identifier

#### Scenario: Existing database is composed
- **WHEN** the shared EF model is built after the rename migration
- **THEN** Sponsorship MUST map the renamed Sponsor and TournamentSponsorPlacement schema with a cascading sponsor-to-placement and a restricting tournament-to-placement relationship

#### Scenario: Sponsorship joins an ambient transaction
- **WHEN** a Sponsorship mutation runs inside a transaction opened by its caller
- **THEN** Sponsorship MUST save its state and outbox message in that transaction without committing it

### Requirement: Existing Sponsorship HTTP contracts remain stable
The rename MUST preserve sponsor route templates, route names and tags, API version metadata,
authorization metadata, antiforgery metadata, form request binding, validation outcomes, response
JSON shapes, and anonymous read access, except that tournament placement resources use the canonical
tournament routes and identifiers.

#### Scenario: Existing client uses a sponsor endpoint
- **WHEN** a client calls an existing sponsor list, detail, create, update, or delete endpoint
- **THEN** the same route, authorization requirement, request shape, and response JSON contract
  MUST apply

#### Scenario: Existing client uses the tournament sponsor placement endpoint
- **WHEN** a client replaces a tournament's sponsor placement through the canonical Tournament endpoint
- **THEN** the existing authorization, validation, and tournament response JSON behavior MUST remain

### Requirement: Sponsorship publishes lifecycle facts
Sponsorship mutations SHALL publish typed integration-event contracts in the `Contracts.V1`
namespace without exposing EF entities. Events MUST describe sponsor creation, update, and deletion,
including the relevant SponsorId facts needed by later consumers. Tournament sponsor placement changes
MUST NOT publish an integration event; consumers read the current placement through the Sponsorship
module contract.

#### Scenario: Sponsor metadata changes
- **WHEN** a sponsor is created, updated, or deleted
- **THEN** Sponsorship MUST publish the matching `Contracts.V1.SponsorCreated`,
  `Contracts.V1.SponsorUpdated`, or `Contracts.V1.SponsorDeleted` event through the durable module
  eventing boundary

#### Scenario: Tournament placement changes
- **WHEN** a tournament's sponsor placement is created, replaced, or removed
- **THEN** Sponsorship MUST persist the placement change
- **AND** it MUST NOT write a `Contracts.V1.TournamentSponsorPlacementChanged` outbox message

### Requirement: Sponsorship coordinates Sponsor logo lifecycle
Sponsorship MUST compensate a newly stored Sponsor logo if the following Sponsor mutation, outbox
write, or transaction commit fails or is cancelled. It MUST retire a replaced or deleted owned
Sponsor logo only after the Sponsor state and matching durable lifecycle event commit succeeds.

#### Scenario: Sponsor create or replacement fails before commit
- **WHEN** Sponsorship stores a Sponsor logo and its owning mutation or matching durable-event commit does not succeed
- **THEN** Sponsorship attempts non-cancelled deletion of only the new logo and preserves the original failure
- **AND** it does not delete the previously current logo

#### Scenario: Sponsor replacement commits
- **WHEN** a Sponsor update commits with a different owned logo URL
- **THEN** Sponsorship attempts to delete the previous logo after the state and SponsorUpdated event commit

#### Scenario: Sponsor deletion commits
- **WHEN** Sponsor deletion and its SponsorDeleted event commit
- **THEN** Sponsorship attempts to delete the former current logo with a non-cancelled token

#### Scenario: Sponsor post-commit cleanup fails
- **WHEN** Sponsor state and its lifecycle event are committed but logo deletion fails
- **THEN** Sponsorship logs the orphan and returns the committed mutation outcome
