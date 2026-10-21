## ADDED Requirements

### Requirement: Deleted user tournament participation
The Tournament module MUST consume the durable user-deleted event and the durable captain-succession event and MUST apply this policy to the deleted user's participation. In a Scheduled tournament it MUST remove the user's individual registration and the user's roster rows; a team registration that loses a roster member MUST return to pending confirmation. When the deleted user was the roster captain, the registration MUST be re-pointed to the team's new captain and returned to pending confirmation, with the new captain's roster row auto-confirmed when present; when the team has no new captain the team registration MUST be removed. In an InProgress tournament registrations, rosters and matches MUST be retained for bracket integrity and the deleted user MUST NOT gain any authority or eligibility. Completed and Canceled tournaments MUST NOT change. Changes to a Scheduled tournament MUST use the tournament's persisted revision concurrency so they cannot commit together with a bracket start based on stale registrations. Processing an event again MUST NOT change any registration.

#### Scenario: Scheduled individual registration is removed
- **WHEN** a user with an individual registration in a Scheduled tournament is deleted
- **THEN** the registration MUST be removed

#### Scenario: Scheduled roster member is removed
- **WHEN** a non-captain roster member of a Scheduled tournament is deleted
- **THEN** the member's roster row MUST be removed
- **AND** the team registration MUST be pending confirmation

#### Scenario: Scheduled roster captain is succeeded
- **WHEN** the roster captain of a Scheduled tournament is deleted and the team appoints a successor
- **THEN** the registration MUST point to the successor and MUST be pending confirmation

#### Scenario: Scheduled roster captain without successor
- **WHEN** the roster captain of a Scheduled tournament is deleted and the team has no successor
- **THEN** the team registration and its roster MUST be removed

#### Scenario: In-progress tournament keeps its rows
- **WHEN** a participant of an InProgress tournament is deleted
- **THEN** the registration, roster and match rows MUST remain

#### Scenario: Historical views keep the placeholder
- **WHEN** a Completed or Canceled tournament with a deleted participant is read
- **THEN** its registrations MUST be unchanged and the participant MUST be shown with the anonymized deleted-user placeholder

### Requirement: Deleted users cannot register
Individual registration, roster submission and roster confirmation MUST re-read the affected users' current account state inside the write transaction, serialized with account deletion, and MUST reject deleted users without persisting any change.

#### Scenario: Deleted user selected for a roster
- **WHEN** a captain submits a roster containing a deleted user
- **THEN** the API MUST reject the roster without changing the registration
