## MODIFIED Requirements

### Requirement: Explicit cross-module persistence references
The system MUST configure relationships between entities owned by different modules at the persistence-composition boundary. Module-owned configuration MUST NOT introduce a dependency on another module's entity type for a cross-module relationship.

#### Scenario: Validating a cross-module relationship
- **WHEN** the runtime EF model is inspected for a relationship from a module-owned dependent to an entity owned by another module
- **THEN** the relationship retains its foreign key with restricted delete behaviour while the owning module configuration remains independent of the other module entity type

## ADDED Requirements

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
