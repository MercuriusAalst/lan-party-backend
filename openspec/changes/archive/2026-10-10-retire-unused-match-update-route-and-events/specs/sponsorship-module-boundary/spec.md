## MODIFIED Requirements

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
