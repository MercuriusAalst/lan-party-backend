## Purpose

Define module-owned Entity Framework mapping, physical schema ownership, and explicit cross-module persistence boundaries while retaining the existing API contract.

## Requirements

### Requirement: Module-owned EF configuration
The system MUST define entity mapping configuration in the infrastructure area of the module that owns the entity. The physical `MercuriusDBContext` MUST compose those module-owned configurations and MUST NOT define module entity mapping details itself.

#### Scenario: Building the runtime model
- **WHEN** the API creates `MercuriusDBContext`
- **THEN** the EF model contains the configurations supplied by Identity, Teams, Tournament, Sponsorship, Discovery, and Platform composition

### Requirement: Schema-aligned persistence ownership
The system MUST map Identity, Teams, Tournament, Sponsorship, Discovery, and Platform tables to their respective PostgreSQL schemas. The migration moving legacy module tables to those schemas MUST preserve existing data, columns, indexes, and foreign-key constraints.

#### Scenario: Applying the persistence-boundary migration
- **WHEN** the migration is applied to a database containing legacy default-schema module tables
- **THEN** each module table is available in its owning schema with its existing data and constraints preserved

### Requirement: Explicit cross-module persistence references
The system MUST configure relationships between entities owned by different modules at the persistence-composition boundary. Module-owned configuration MUST NOT introduce a dependency on another module's entity type for a cross-module relationship.

#### Scenario: Validating a cross-module relationship
- **WHEN** the runtime EF model is inspected for a relationship from a module-owned dependent to an entity owned by another module
- **THEN** the relationship retains its foreign key with restricted delete behaviour while the owning module configuration remains independent of the other module entity type

### Requirement: Public contract preservation
The persistence-boundary refactor MUST preserve the existing API routes, authorization requirements, request and response JSON shapes, and public-search behaviour.

#### Scenario: Exercising an existing public API contract
- **WHEN** an existing API route is invoked after the persistence-boundary migration
- **THEN** it returns the same route, authorization outcome, and JSON contract as before the migration

### Requirement: Cross-module deletes are restricted
Foreign keys from one module's tables to another module's tables SHALL be `ON DELETE RESTRICT`, including `team_members` → `users`, `team_invites` → `users`, `placement_users` → `users`, `placement_teams` → `teams` and `tournament_sponsor_placements` → `tournaments`. A delete MUST NOT cascade into rows owned by another module; the owning module SHALL remove or retain its own rows explicitly. Users SHALL be anonymized and teams soft-deleted rather than hard-deleted.

#### Scenario: Database delete crosses a module boundary
- **WHEN** a row is deleted directly while another module's rows still reference it
- **THEN** the database MUST reject the delete
- **AND** the referencing rows MUST remain unchanged

#### Scenario: User with team and placement references is deleted
- **WHEN** a user who is a team member, invitee, or placement holder is deleted
- **THEN** the user MUST be anonymized and the user row MUST remain
- **AND** the referencing rows MUST remain

#### Scenario: Team with placements is deleted
- **WHEN** a team referenced by tournament placements is deleted
- **THEN** the team MUST be soft-deleted and its placements MUST remain
