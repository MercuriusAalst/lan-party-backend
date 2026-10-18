## ADDED Requirements

### Requirement: Started elimination participants are locked
The API MUST reject admin removal of an individual registration or team registration from a single or double elimination tournament unless the tournament is Scheduled. A scheduled admin removal MUST participate in the tournament's persisted revision concurrency so it cannot commit alongside a bracket start based on stale registrations. Other bracket types MUST retain their existing admin removal behavior.

#### Scenario: Admin removes an individual from a scheduled elimination tournament
- **WHEN** an admin removes an individual registration while a single or double elimination tournament is Scheduled
- **THEN** the API removes the registration

#### Scenario: Admin cannot remove an individual after an elimination tournament leaves Scheduled
- **WHEN** an admin removes an individual registration after a single or double elimination tournament is InProgress, Completed, or Canceled
- **THEN** the API rejects the request without removing the registration

#### Scenario: Admin cannot remove a team after an elimination tournament leaves Scheduled
- **WHEN** an admin removes a team registration after a single or double elimination tournament is InProgress, Completed, or Canceled
- **THEN** the API rejects the request without removing the registration or its roster

#### Scenario: Concurrent removal and start cannot save a stale bracket
- **WHEN** an admin removes a registration while a single or double elimination tournament starts concurrently
- **THEN** the persisted tournament revision allows at most one operation to commit
- **AND** the tournament MUST NOT keep a bracket participant whose registration was removed by the competing operation

#### Scenario: Other bracket types retain admin removal behavior
- **WHEN** an admin removes a registration from a tournament using another bracket type
- **THEN** the API retains its existing admin removal behavior
