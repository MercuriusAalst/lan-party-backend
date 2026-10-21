## ADDED Requirements

### Requirement: Deleted users cannot subscribe to team events
The team realtime authorizer MUST deny a subscription for a user whose account is deleted, even when team cleanup has not yet removed the user as member or captain.

#### Scenario: Deleted captain joins the team group
- **WHEN** a team's deleted captain or deleted member is checked for a team subscription
- **THEN** the authorizer MUST deny the subscription
