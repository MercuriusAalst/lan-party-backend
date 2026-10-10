## ADDED Requirements

### Requirement: Concurrent team modification conflicts
The Team version MUST act as an optimistic concurrency token. When a team save fails because another operation changed the team's version since it was loaded, the API MUST return HTTP 409 with code `team_changed` and message "The team changed. Refresh and try again." and MUST NOT persist the failed change.

#### Scenario: Team changes during a mutation
- **WHEN** another operation commits a team version change while a team mutation is in progress
- **THEN** the API MUST return HTTP 409 with code `team_changed`
- **AND** the failed mutation MUST NOT be persisted
