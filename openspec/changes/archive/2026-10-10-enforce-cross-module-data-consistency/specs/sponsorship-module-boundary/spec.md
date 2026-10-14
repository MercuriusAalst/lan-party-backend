## MODIFIED Requirements

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
