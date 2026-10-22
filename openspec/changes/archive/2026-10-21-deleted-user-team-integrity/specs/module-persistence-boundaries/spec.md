## MODIFIED Requirements

### Requirement: Cross-module deletes are restricted
Foreign keys from one module's tables to another module's tables SHALL be `ON DELETE RESTRICT`, including `team_members` → `users`, `team_invites` → `users`, `placement_users` → `users`, `placement_teams` → `teams` and `tournament_sponsor_placements` → `tournaments`. A delete MUST NOT cascade into rows owned by another module; the owning module SHALL remove or retain its own rows explicitly. Users SHALL be anonymized and teams soft-deleted rather than hard-deleted. When a user is anonymized, the Teams and Tournament modules SHALL clean up their own live rows asynchronously from the durable user-deleted event; the Identity module MUST NOT write them.

#### Scenario: Database delete crosses a module boundary
- **WHEN** a row is deleted directly while another module's rows still reference it
- **THEN** the database MUST reject the delete
- **AND** the referencing rows MUST remain unchanged

#### Scenario: User with team and placement references is deleted
- **WHEN** a user who is a team member, invitee, or placement holder is deleted
- **THEN** the user MUST be anonymized and the user row MUST remain
- **AND** the deletion transaction MUST NOT change rows owned by other modules
- **AND** placement rows MUST remain
- **AND** the Teams module MUST later remove the live membership and cancel pending invites as its own cleanup

#### Scenario: Team with placements is deleted
- **WHEN** a team referenced by tournament placements is deleted
- **THEN** the team MUST be soft-deleted and its placements MUST remain
