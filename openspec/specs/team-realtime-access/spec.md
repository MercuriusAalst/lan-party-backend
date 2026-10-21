# team-realtime-access Specification

## Purpose
Define authenticated team-management hub access, membership revocation, and process-local connection tracking.
## Requirements
### Requirement: Exact-path browser hub authentication
The API MUST accept a browser SignalR JWT from the `access_token` query parameter only for the canonical authenticated team-management hub route and MUST preserve normal bearer-header and prior authentication-event behavior.

#### Scenario: Single query token on exact hub route
- **WHEN** a request to the exact team-management hub route contains exactly one nonblank `access_token` query value and no prior token or Authorization header
- **THEN** the JWT bearer handler MUST use that original query value as the bearer token without trimming, decoding, or logging it

#### Scenario: Query token outside hub route
- **WHEN** any other route contains an `access_token` query value
- **THEN** the JWT bearer handler MUST NOT use the query value as a bearer token

#### Scenario: Ambiguous or blank query token
- **WHEN** the exact hub route contains zero, multiple, empty, or whitespace-only `access_token` query values
- **THEN** the JWT bearer handler MUST NOT select a query token

#### Scenario: Header or prior event result is preserved
- **WHEN** the request has an Authorization header or a prior authentication event already selected a token or result
- **THEN** the query-token callback MUST NOT replace that authentication state

### Requirement: Hub authentication expiration
The API MUST close an authenticated team-management hub connection when its authentication expiration is reached and allow the SignalR client to reconnect with current credentials.

#### Scenario: Authentication expires during connection
- **WHEN** an authenticated hub connection reaches the expiration recorded by bearer authentication
- **THEN** the SignalR connection dispatcher MUST close the connection

### Requirement: Serialized team subscription authorization
The API MUST resolve an active local user and verify current team membership immediately before adding a connection to a team group, serialized with process-local revocation.

#### Scenario: Active team member joins
- **WHEN** an authenticated active user is currently authorized for a non-deleted team and invokes the team join method
- **THEN** the API MUST add and track that connection in the existing team group

#### Scenario: Access is revoked while join is in progress
- **WHEN** membership, team, or account access is committed while a team join is in progress
- **THEN** the serialized join and revocation operations MUST leave no affected connection subscribed to that team group

### Requirement: Post-commit multi-connection revocation
The API MUST remove all relevant live connections from process-local realtime groups only after the underlying access-removing transaction commits.

#### Scenario: Member is removed or leaves
- **WHEN** a member removal or team leave transaction commits successfully
- **THEN** every active connection for that user in the affected team group MUST be removed before the existing membership broadcast

#### Scenario: Team is deleted
- **WHEN** a team deletion transaction commits successfully
- **THEN** every tracked active connection in the affected team group MUST be removed

#### Scenario: Account is deleted
- **WHEN** an active account's first deletion transaction commits successfully
- **THEN** every tracked active connection for that user MUST be removed from its personal and team groups

#### Scenario: Transaction fails
- **WHEN** an access-removing mutation or its durable event publication fails before commit
- **THEN** the API MUST NOT revoke the existing realtime subscriptions

#### Scenario: Post-commit revocation fails
- **WHEN** process-local group removal fails after the access mutation has committed
- **THEN** the API MUST surface the failure without claiming or attempting to roll back the committed mutation

### Requirement: Connection lifecycle tracking
The API MUST track multiple active connections per local user and clean process-local tracking when a connection leaves a team group or disconnects.

#### Scenario: User has multiple connections
- **WHEN** one user connects from multiple SignalR clients
- **THEN** revocation MUST apply to every tracked connection for that user in the affected groups

#### Scenario: Client leaves team group
- **WHEN** a connection explicitly leaves a team group
- **THEN** the API MUST remove that group from the connection's tracked state

#### Scenario: Client disconnects
- **WHEN** a SignalR connection disconnects
- **THEN** the API MUST remove that connection from user and group tracking

### Requirement: Realtime contracts and deployment scope remain stable
The API MUST preserve the team-management hub route, group names, client method names, payload shapes, REST routes, and JSON contracts. Process-local revocation MUST NOT be represented as multi-instance security.

#### Scenario: Existing realtime client contract is used
- **WHEN** an authorized client connects, subscribes, or receives an existing realtime event
- **THEN** the existing hub route, `user:{id:N}` and `team:{id:N}` group names, client method names, and payload shapes MUST remain unchanged

#### Scenario: API process restarts
- **WHEN** the single API process restarts
- **THEN** all active SignalR connections and their in-memory tracking MUST be dropped together and reconnecting clients MUST authenticate and authorize again

#### Scenario: Scale-out is considered
- **WHEN** deployment changes to multiple API instances
- **THEN** a separate distributed SignalR connection and revocation design MUST be introduced before process-local revocation can be claimed across instances

### Requirement: Affected users receive their membership change event
The API MUST publish a membership-change event to the affected user's authenticated personal group as well as the current team group, without changing the existing event name or payload shape.

#### Scenario: A connected member is removed
- **WHEN** a committed member removal revokes the affected user's team-group connections
- **THEN** the API MUST publish the existing membership-change event to the current team group and that user's personal group
- **AND** the user MUST receive the event through their personal connection while remaining revoked from the team group

#### Scenario: A user leaves or joins a team
- **WHEN** a committed membership change affects a connected user
- **THEN** the user MUST receive the existing membership-change event through their personal group
- **AND** the event MUST continue to reach the current team group

#### Scenario: A connected team member's team is deleted
- **WHEN** a team deletion commits and the API revokes the team's realtime group
- **THEN** the API MUST notify every pre-deletion member through that member's authenticated personal group
- **AND** the API MUST NOT restore or retain any deleted team's group subscription
- **AND** the API MUST notify every user with an unexpired pending invite through that user's authenticated personal group using the existing invite-change event with a cancelled status

### Requirement: Deleted users cannot subscribe to team events
The team realtime authorizer MUST deny a subscription for a user whose account is deleted, even when team cleanup has not yet removed the user as member or captain.

#### Scenario: Deleted captain joins the team group
- **WHEN** a team's deleted captain or deleted member is checked for a team subscription
- **THEN** the authorizer MUST deny the subscription

