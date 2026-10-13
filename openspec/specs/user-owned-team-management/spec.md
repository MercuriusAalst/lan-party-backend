## Purpose

Define authenticated self-service team ownership, membership, invites, captain transfer, team logo management, and related real-time update behavior.
## Requirements
### Requirement: Authenticated team creation
The API MUST allow an authenticated user to create a team and MUST make that user the team's captain and a team member.

#### Scenario: Current user creates team
- **WHEN** an authenticated user creates a team with a valid name
- **THEN** the API creates the team with the current user as captain and member

#### Scenario: Anonymous user cannot create team
- **WHEN** an anonymous client attempts to create a team
- **THEN** the API rejects the request as unauthorized

#### Scenario: Captain limit enforced
- **WHEN** a user already captains two teams and attempts to create another team
- **THEN** the API rejects the request without creating a third captained team

### Requirement: Captain-only team mutations
The API MUST allow only the current team captain to invite users, cancel team invitations, remove team members, transfer captainship, and manage the team logo.

#### Scenario: Captain mutates own team
- **WHEN** the current user is the team captain and performs a captain-only mutation
- **THEN** the API applies the mutation when all validation rules pass

#### Scenario: Non-captain mutation rejected
- **WHEN** the current user is not the team captain and attempts a captain-only mutation
- **THEN** the API rejects the request without changing team state

#### Scenario: Non-captain member removal is rejected
- **WHEN** an authenticated user who is not the team captain attempts to remove a team member
- **THEN** the API rejects the request without changing team membership

#### Scenario: Missing team rejected
- **WHEN** the requested team does not exist
- **THEN** the API returns a not-found response

### Requirement: Team invitation lifecycle
The API MUST track team invitations with Pending, Accepted, Declined, Cancelled, and Expired states, and team invite clients MAY use authenticated user search to discover stable recipient user ids before sending an invite.

#### Scenario: Captain sends invite
- **WHEN** a captain invites an existing user who is not already a team member
- **THEN** the API creates a pending invite for that user and team

#### Scenario: Duplicate pending invite blocked
- **WHEN** a pending invite already exists for the same user and team
- **THEN** the API rejects another invite for that user and team

#### Scenario: Declined invite resend allowance not exhausted
- **WHEN** a user declined a team invite and the configured declined-invite resend allowance has not been exhausted
- **THEN** the API allows the captain to send another invite for that user and team when no pending invite exists

#### Scenario: Declined invite cooldown enforced after resend allowance
- **WHEN** a user has declined the configured number of resend attempts for a team and the resend cooldown has not elapsed
- **THEN** the API rejects a new invite for that user and team

#### Scenario: Captain cancels pending invite
- **WHEN** the team captain cancels a pending invite for their team
- **THEN** the API marks the invite as cancelled and excludes it from recipient pending-invite lists

#### Scenario: Accepted or declined invite cannot be cancelled
- **WHEN** a captain attempts to cancel an invite that is not pending
- **THEN** the API rejects the cancellation without changing the invite status

#### Scenario: Pending invite expires
- **WHEN** a pending invite reaches its configured expiration time
- **THEN** the API treats the invite as expired and excludes it from actionable pending invite lists

#### Scenario: Expired invite cannot be answered
- **WHEN** a user attempts to accept or decline an expired invite
- **THEN** the API rejects the request without changing membership

#### Scenario: User search does not replace invite validation
- **WHEN** a client sends an invite using a user id discovered through authenticated user search
- **THEN** the existing invite endpoint still enforces captainship, membership, duplicate invite, expiration, and cooldown rules before creating the invite

### Requirement: Captain member removal
The API MUST allow a team captain to remove a non-captain member from their team only when the removal is not blocked by the member's pending or active tournament roster participation.

#### Scenario: Captain removes member
- **WHEN** the current user is the team captain
- **AND** the target user is a non-captain member of the team
- **AND** the target user is not pending confirmation or confirmed on a protected team tournament roster for that team
- **THEN** the API removes the target user from the team members
- **AND** publishes a privacy-safe membership removal event

#### Scenario: Anonymous user cannot remove member
- **WHEN** an anonymous client attempts to remove a team member
- **THEN** the API rejects the request as unauthorized

#### Scenario: Non-captain cannot remove member
- **WHEN** the current user is not the team captain and attempts to remove a team member
- **THEN** the API rejects the request without changing team state

#### Scenario: Captain cannot be removed
- **WHEN** the captain attempts to remove the current captain from the team
- **THEN** the API rejects the request and leaves the captain as a member

#### Scenario: Missing member rejected
- **WHEN** the captain attempts to remove a user who is not a current member
- **THEN** the API rejects the request without changing team state

#### Scenario: Pending roster confirmation blocks member removal
- **WHEN** the target member has a pending roster confirmation for this team in a scheduled team tournament
- **THEN** the API rejects team member removal until the captain edits or unregisters the tournament roster

#### Scenario: Confirmed scheduled roster blocks member removal
- **WHEN** the target member is confirmed on an active roster for this team in a scheduled team tournament
- **THEN** the API rejects team member removal until the captain edits or unregisters the tournament roster

#### Scenario: In-progress tournament roster blocks removal
- **WHEN** the target member is confirmed on a roster for this team in an in-progress team tournament
- **THEN** the API rejects member removal and preserves team membership

#### Scenario: Completed or canceled tournament roster allows removal
- **WHEN** the target member is referenced only by completed or canceled tournament registration history where historical roster preservation does not require active team membership
- **THEN** the API allows member removal when no other removal rule blocks it

### Requirement: Invite retention
The API MUST retain invitation records only while needed for pending actions, anti-spam cooldown, or audit, and MUST provide a cleanup path for expired terminal invites.

#### Scenario: Terminal invite retained during cooldown
- **WHEN** an invite is declined, cancelled, accepted, or expired and is still within its configured retention or cooldown window
- **THEN** the API keeps the invite record for validation, audit, and anti-spam decisions

#### Scenario: Terminal invite eligible for cleanup
- **WHEN** an invite is no longer pending and its configured retention and cooldown windows have elapsed
- **THEN** the invite is eligible for cleanup and MUST no longer appear in user or captain invite projections

### Requirement: Invite recipient responses
The API MUST allow invited users to accept or decline only their own pending invitations.

#### Scenario: Recipient accepts invite
- **WHEN** the invite recipient accepts a pending invite
- **THEN** the API marks the invite accepted and adds the user as a team member

#### Scenario: Recipient declines invite
- **WHEN** the invite recipient declines a pending invite
- **THEN** the API marks the invite declined and does not add the user as a team member

#### Scenario: Different user cannot respond
- **WHEN** a user attempts to accept or decline an invite addressed to another user
- **THEN** the API rejects the request without changing the invite or membership

#### Scenario: Non-pending invite cannot be answered
- **WHEN** a user attempts to answer an invite that is accepted, declined, or cancelled
- **THEN** the API rejects the request without changing the invite or membership

### Requirement: Team member leave rules
The API MUST allow a team member to leave only when leaving is not blocked by captainship or protected tournament roster rules.

#### Scenario: Non-captain member leaves safe team
- **WHEN** a non-captain member leaves a team that is not blocked by pending or active tournament roster rules
- **THEN** the API removes the user from the team members

#### Scenario: Captain cannot leave while still captain
- **WHEN** the current team captain attempts to leave the team without transferring captainship
- **THEN** the API rejects the request and leaves the captain as a member

#### Scenario: Pending roster confirmation blocks team leave
- **WHEN** a team member attempts to leave while they have a pending roster confirmation for the team in a scheduled tournament
- **THEN** the API rejects the request and requires the captain to edit or unregister the tournament roster first

#### Scenario: Confirmed scheduled roster blocks team leave
- **WHEN** a team member attempts to leave while they are confirmed on an active roster for the team in a scheduled tournament
- **THEN** the API rejects the request and requires the captain to edit or unregister the tournament roster first

#### Scenario: In-progress tournament roster blocks leave
- **WHEN** a team member attempts to leave while they are confirmed on a roster for the team in an InProgress tournament registration
- **THEN** the API rejects the request and preserves team membership

#### Scenario: Completed tournament roster does not require active team membership
- **WHEN** a team member attempts to leave while they are referenced only by Completed tournament registration history
- **THEN** the API allows the member to leave when no other leave rule blocks it

#### Scenario: Canceled tournament roster does not require active team membership
- **WHEN** a team member attempts to leave while they are referenced only by Canceled tournament registration history
- **THEN** the API allows the member to leave when no other leave rule blocks it

#### Scenario: Unprotected tournament state allows leave
- **WHEN** a team member leaves and no pending or active protected registration roster references the member for that team
- **THEN** the API allows the member to leave when no other leave rule blocks it

### Requirement: Captain transfer
The API MUST allow a team captain to transfer captainship to exactly one current team member.

#### Scenario: Captain transfers to member
- **WHEN** the captain transfers captainship to another current team member
- **THEN** the API updates the team captain to that member and keeps both users as team members

#### Scenario: Transfer to non-member rejected
- **WHEN** the captain attempts to transfer captainship to a user who is not a current team member
- **THEN** the API rejects the transfer without changing the captain

#### Scenario: Transfer preserves single captain
- **WHEN** a captain transfer succeeds
- **THEN** the team has exactly one captain after the change

#### Scenario: Recipient captain limit enforced
- **WHEN** the target member already captains two teams
- **THEN** the API rejects the captain transfer without changing the captain

### Requirement: Team logo management
The API MUST allow a team captain to upload, replace, and remove an optional team logo with server-side validation and safe serving behavior.

#### Scenario: Captain uploads valid logo
- **WHEN** the captain uploads a valid supported image within the configured size limit
- **THEN** the API stores the logo with a server-generated safe path or key and associates it with the team

#### Scenario: Captain replaces logo
- **WHEN** the captain uploads a new valid logo for a team that already has one
- **THEN** the API associates the new logo with the team and no longer exposes the previous logo as the active logo

#### Scenario: Captain removes logo
- **WHEN** the captain removes the team's logo
- **THEN** the API clears the team's active logo reference

#### Scenario: Invalid logo rejected
- **WHEN** the uploaded file has an unsupported type, unsafe content, unsafe storage path, or exceeds the configured size limit
- **THEN** the API rejects the upload without changing the team's active logo

#### Scenario: Logo served as inert image content
- **WHEN** a client retrieves a stored team logo
- **THEN** the API or static file configuration serves it as image content that cannot execute script or active content

### Requirement: Current-user team and invite projections
The API MUST expose authenticated current-user projections for captained teams, member teams, received pending invites, and pending invites sent for teams captained by the current user.

#### Scenario: Captained teams returned
- **WHEN** an authenticated user requests their team management summary
- **THEN** the response includes teams where the user is captain

#### Scenario: Member teams returned
- **WHEN** an authenticated user requests their team management summary
- **THEN** the response includes teams where the user is a member

#### Scenario: Received pending invites returned
- **WHEN** an authenticated user requests their team management summary
- **THEN** the response includes pending invites addressed to that user

#### Scenario: Sent pending invites returned for captained teams
- **WHEN** an authenticated captain requests their team management summary
- **THEN** the response includes pending invites sent for teams captained by that user

#### Scenario: Projection omits private user fields
- **WHEN** the API returns current-user team or invite projections
- **THEN** the response omits email, Auth0 IDs, deleted state, and private account metadata for other users

### Requirement: Real-time team management events
The API MUST publish privacy-safe real-time events for successful team invite, membership, and captain transfer changes.

#### Scenario: Invite event published
- **WHEN** an invite is created, cancelled, accepted, declined, or expired
- **THEN** authorized affected clients can receive a real-time event describing the invite change without private user fields

#### Scenario: Membership event published
- **WHEN** a member joins through invite acceptance or leaves a team
- **THEN** authorized affected clients can receive a real-time event describing the membership change without private user fields

#### Scenario: Captain transfer event published
- **WHEN** captainship is transferred successfully
- **THEN** authorized affected clients can receive a real-time event identifying the team and new captain by safe public fields

#### Scenario: Unauthorized client does not receive event
- **WHEN** a client is not authorized to know about a team or invite
- **THEN** the API does not send that client private team-management event details

### Requirement: Admin team endpoint retirement
The API MUST remove admin-only team management endpoints that duplicate user-owned team management flows.

#### Scenario: Admin-only create endpoint removed
- **WHEN** a client calls the previous admin-only team creation endpoint
- **THEN** the API no longer exposes that admin-only mutation route

#### Scenario: Admin-only member mutation endpoints removed
- **WHEN** a client calls previous admin-only team member or invite mutation endpoints
- **THEN** the API no longer exposes those admin-only mutation routes

### Requirement: Efficient team management queries
The API MUST query team membership, invite, and roster state efficiently for user-owned team management flows.

#### Scenario: Team and invite listing avoids N+1 loading
- **WHEN** a user requests team and invite projections
- **THEN** the API retrieves the data using bounded projections or includes instead of per-row follow-up queries

#### Scenario: Roster blocking check is targeted
- **WHEN** the API checks whether a member can leave or be removed from a team
- **THEN** it queries only the pending and active tournament registration, confirmation, and roster state needed to decide whether the action is allowed

#### Scenario: Registration summaries use bounded projections
- **WHEN** the API includes team tournament registration summaries in current-user team management views
- **THEN** it retrieves pending confirmations, active registrations, full active rosters, and roster counts using bounded projections rather than per-team follow-up queries

### Requirement: Captain-owned team deletion
The API MUST allow a team captain to delete their team only when deletion preserves historical data and does not disrupt pending or active participation.

#### Scenario: Captain deletes inactive team
- **WHEN** the current user is the captain of a team that is not pending or actively participating in team tournaments or tournaments
- **THEN** the API marks the team as deleted without physically removing the team row
- **AND** anonymizes the team name to a generated deleted-team value
- **AND** clears the team logo, captain association, members, and invitations
- **AND** historical match, placement, and completed or canceled registration references to the team remain intact

#### Scenario: Deleted team name can be reused
- **WHEN** a team has been deleted
- **THEN** another active team MAY be created with the deleted team's previous name when no other active team uses that name

#### Scenario: Non-captain delete rejected
- **WHEN** the current user is not the team captain and attempts to delete the team
- **THEN** the API rejects the request without changing team state

#### Scenario: Pending tournament registration blocks delete
- **WHEN** a team has a pending team tournament registration or pending roster confirmations
- **THEN** the API rejects deletion and preserves the team as active

#### Scenario: Active participation blocks delete
- **WHEN** a team has an active Scheduled or InProgress team tournament registration
- **THEN** the API rejects deletion and preserves the team as active

#### Scenario: Completed or canceled participation allows delete
- **WHEN** a team is registered only for Completed or Canceled team tournaments or tournaments
- **THEN** the API allows deletion when no other deletion rule blocks it

#### Scenario: Deleted team hidden from active team surfaces
- **WHEN** a team has been deleted
- **THEN** active team listing, lookup, team-name search, public profile, and current-user team-management projections MUST exclude the team

#### Scenario: Historical deleted team references remain readable
- **WHEN** a deleted team is referenced by completed or canceled tournaments, matches, placements, or registrations
- **THEN** those historical records MAY continue to reference the deleted team row without exposing the original team name, logo, captain, members, invite data, confirmation data, or private registration metadata

### Requirement: Resource-oriented team membership, invitation, and logo routes
The API MUST expose authenticated resource-oriented routes for team leave, invitation creation, invitation response, and logo replacement while preserving the existing team authorization and lifecycle rules.

#### Scenario: Team member leaves through self membership deletion
- **WHEN** an authenticated team member sends `DELETE /v1/lan/teams/{teamId}/members/me`
- **THEN** the API MUST apply the existing team-leave rules and return the existing leave response JSON shape

#### Scenario: Captain creates an invitation in the team invitation collection
- **WHEN** a team captain sends `POST /v1/lan/teams/{teamId}/invites` with the recipient user id in the request body
- **THEN** the API MUST apply the existing invitation creation and validation rules and return the existing invite response JSON shape

#### Scenario: Invite recipient updates the invite resource
- **WHEN** an authenticated invite recipient sends `PATCH /v1/lan/team-invites/{inviteId}` with an accepted or declined response
- **THEN** the API MUST apply the existing invitation response rules and return the existing invite response JSON shape

#### Scenario: Captain replaces a team logo resource
- **WHEN** a team captain sends multipart form data to `PUT /v1/lan/teams/{teamId}/logo`
- **THEN** the API MUST apply the existing logo validation and replacement rules and return the existing logo response JSON shape

#### Scenario: Team action routes are absent
- **WHEN** a client calls `POST /v1/lan/teams/{teamId}/leave`, `POST /v1/lan/teams/{teamId}/invites/{userId}`, `PUT /v1/lan/teams/invites/{inviteId}`, or `POST /v1/lan/teams/{teamId}/logo`
- **THEN** the API MUST NOT expose those routes

### Requirement: Bounded invite maintenance
The API MUST keep current-user invite projections fresh without performing global invite maintenance on request threads, and MUST persist invite expiry and retention cleanup through deterministic, configurable, bounded maintenance batches.

#### Scenario: Current-user read does not maintain unrelated invites
- **WHEN** an authenticated user requests their team summary, received invites, or sent invites
- **THEN** the API MUST query only team and invite data relevant to that user or teams they captain
- **AND** MUST NOT expire, delete, scan for events, or publish expiry events for unrelated invites

#### Scenario: Due invite is immediately absent from actionable reads
- **WHEN** a pending invite's expiration timestamp has elapsed but scheduled maintenance has not yet persisted its Expired status
- **THEN** current-user actionable invite projections MUST exclude that invite using its expiration timestamp
- **AND** the read MUST NOT persist an invite status transition

#### Scenario: Expiry maintenance is bounded and deterministic
- **WHEN** one scheduled maintenance cycle finds more due pending invites than the configured batch size
- **THEN** it MUST transition no more than the configured batch size ordered by expiration timestamp and stable invite identifier
- **AND** remaining due invites MUST be left for later cycles

#### Scenario: Retention maintenance is bounded and deterministic
- **WHEN** one scheduled maintenance cycle finds more eligible terminal invites than the configured batch size
- **THEN** it MUST delete no more than the configured batch size ordered by terminal timestamp and stable invite identifier
- **AND** remaining eligible invites MUST be left for later cycles

#### Scenario: Expiry event fan-out is bounded and idempotent
- **WHEN** a maintenance cycle successfully persists pending invites as expired
- **THEN** it MUST attempt one privacy-safe expiry event for each invite transitioned by that cycle
- **AND** MUST NOT publish more expiry events than the configured batch size
- **AND** a later maintenance cycle MUST NOT republish events for those already-expired invites

#### Scenario: Concurrent maintenance instances do not duplicate work
- **WHEN** multiple application instances attempt invite maintenance concurrently
- **THEN** at most one instance MUST own the maintenance batch
- **AND** the other instances MUST leave the owned invites and their expiry events unprocessed

#### Scenario: Maintenance honors cancellation
- **WHEN** application shutdown or caller cancellation is requested during maintenance
- **THEN** database queries, writes, waits, and realtime publication MUST observe the cancellation token

### Requirement: Team logo retirement preserves current and historical references
The API MUST retire a replaced, removed, or soft-deleted Team logo only after the Team mutation and
its durable business event commit, and MUST retain the file while any active Team or any tournament
registration snapshot references the same logo URL. Reference-check failure MUST retain the file.

#### Scenario: Unreferenced Team logo is replaced or removed
- **WHEN** a Team logo replacement or removal commits and no active Team or registration snapshot references the previous different logo URL
- **THEN** the API attempts to delete the previous logo after commit with a non-cancelled token

#### Scenario: Historical tournament snapshot references Team logo
- **WHEN** any tournament registration stores the candidate URL in `TeamLogoUrlAtRegistration`
- **THEN** Team logo replacement, removal, or soft deletion retains the physical logo file

#### Scenario: Current Team references candidate logo
- **WHEN** an active Team still uses the candidate logo URL
- **THEN** the API retains the physical logo file

#### Scenario: Team logo reference check fails
- **WHEN** a current or historical Team-logo reference query fails
- **THEN** the API logs the failure and retains the physical logo file

#### Scenario: Team soft deletion commits
- **WHEN** a Team soft deletion and its durable TeamDeleted event commit
- **THEN** the existing SignalR group access is revoked in its current post-commit order
- **AND** only then does the API attempt eligible logo retirement without changing the deletion response

### Requirement: Concurrent team modification conflicts
The Team version MUST act as an optimistic concurrency token. When a team save fails because another operation changed the team's version since it was loaded, the API MUST return HTTP 409 with code `team_changed` and message "The team changed. Refresh and try again." and MUST NOT persist the failed change.

#### Scenario: Team changes during a mutation
- **WHEN** another operation commits a team version change while a team mutation is in progress
- **THEN** the API MUST return HTTP 409 with code `team_changed`
- **AND** the failed mutation MUST NOT be persisted
