# tournament-registration Specification

## Purpose

Define internal individual and team tournament registration, exact roster selection, member confirmation, eligibility checks, admin removal, transient cleanup, and legacy external registration URL retirement.
## Requirements
### Requirement: Internal individual registration
The API MUST allow authenticated users to register and unregister themselves for individual tournaments through internal registration endpoints.

#### Scenario: Authenticated user registers self
- **WHEN** an authenticated user confirms registration for a scheduled individual tournament
- **THEN** the API creates an active registration for that user
- **AND** the registration response identifies the tournament and the registered user using privacy-safe fields

#### Scenario: Anonymous user cannot register
- **WHEN** an anonymous client attempts to register for a tournament
- **THEN** the API rejects the request as unauthorized

#### Scenario: Duplicate individual registration blocked
- **WHEN** a user already has active or pending participation for a tournament
- **THEN** the API rejects individual registration for the same tournament without creating a duplicate

#### Scenario: User unregisters before start
- **WHEN** an authenticated user has an active individual registration for a scheduled tournament
- **AND** tournament state allows self-unregistration
- **THEN** the API deletes the registration from active participation

#### Scenario: User cannot unregister after start
- **WHEN** an authenticated user attempts to unregister from an in-progress or completed tournament
- **THEN** the API rejects the request unless an admin removes the user from the tournament

### Requirement: Team roster submission and confirmation
The API MUST allow team captains to submit exact-size rosters for teams they captain, require selected non-captain members to confirm, and automatically activate the team registration when all required confirmations are complete.

#### Scenario: Captain submits exact roster
- **WHEN** an authenticated captain submits a roster for a scheduled team tournament
- **AND** the roster contains exactly the configured tournament team size
- **AND** the roster includes the captain and only current team members
- **THEN** the API creates or updates a pending team registration
- **AND** marks the captain as confirmed automatically
- **AND** sends dedicated roster-confirmation notifications to each selected non-captain roster member without creating team membership invites

#### Scenario: Captain omitted from roster rejected
- **WHEN** a captain submits a team tournament roster that does not include themselves
- **THEN** the API rejects the roster without creating or updating the registration

#### Scenario: Smaller roster rejected
- **WHEN** a captain submits fewer roster members than the configured tournament team size
- **THEN** the API rejects the roster without changing tournament participation

#### Scenario: Oversized roster rejected
- **WHEN** a captain submits more roster members than the configured tournament team size
- **THEN** the API rejects the roster without changing tournament participation

#### Scenario: Non-captain cannot submit team roster
- **WHEN** an authenticated user who is not the team captain attempts to submit a roster for that team
- **THEN** the API rejects the request without creating or updating a registration

#### Scenario: Roster member must belong to team
- **WHEN** a captain selects a user who is not a current member of the registered team
- **THEN** the API rejects the roster without creating or updating the registration

#### Scenario: Member confirms roster selection
- **WHEN** a selected non-captain roster member confirms a valid pending roster notification
- **THEN** the API marks that member's roster selection as confirmed
- **AND** removes the consumed roster-confirmation notification from actionable notification state

#### Scenario: Team activates when all members confirm
- **WHEN** all selected non-captain roster members have confirmed their valid pending roster notifications
- **THEN** the API automatically changes the team registration to active
- **AND** the team is added to active tournament participation

#### Scenario: Confirmation rechecks eligibility
- **WHEN** a selected member confirms a pending roster notification
- **THEN** the API rechecks tournament, team, roster, and duplicate-participation eligibility before accepting the confirmation

#### Scenario: Confirming withdrawn notification rejected
- **WHEN** a user attempts to confirm a roster notification that was deleted or withdrawn by a roster edit, team unregistration, or admin removal
- **THEN** the API rejects the confirmation without changing tournament participation

### Requirement: Captain roster control
The API MUST allow only the captain to edit a team roster before tournament start, and confirmed members MUST NOT be able to leave the roster on their own.

#### Scenario: Captain edits roster before start
- **WHEN** a captain updates a pending or active team roster before the tournament starts
- **THEN** the API validates the new exact-size roster
- **AND** removes or withdraws previous pending confirmations for replaced or changed roster entries
- **AND** sends new confirmation notifications to selected non-captain members who need confirmation

#### Scenario: Captain cannot edit roster after start
- **WHEN** a captain attempts to edit the roster after the tournament has started
- **THEN** the API rejects the request

#### Scenario: Confirmed member cannot leave roster
- **WHEN** a confirmed roster member attempts to remove themselves from a team tournament roster
- **THEN** the API rejects the request without changing roster or registration state

#### Scenario: Pending selected member cannot remove themselves directly
- **WHEN** a selected member with a pending roster confirmation attempts to remove themselves through a roster leave endpoint
- **THEN** the API rejects the request and requires the captain to edit the roster or unregister the team

#### Scenario: Captain unregisters team before start
- **WHEN** a captain unregisters their pending or active team registration before the tournament starts
- **THEN** the API removes the team from tournament participation
- **AND** removes related pending roster confirmations and confirmation notifications for that team registration

### Requirement: Exact tournament team size configuration
Admins MUST configure an exact roster size from 1 through 50 for team tournaments.

#### Scenario: Admin configures exact team size
- **WHEN** an admin creates or updates a team tournament with a valid team size from 1 through 50
- **THEN** the API stores the team size as the exact required roster size for that tournament

#### Scenario: Individual tournament does not require team size
- **WHEN** an admin creates or updates an individual tournament
- **THEN** the API does not require a team size for registration eligibility

#### Scenario: Invalid team size rejected
- **WHEN** an admin creates or updates a team tournament with a missing, zero, negative, or greater-than-50 team size
- **THEN** the API rejects the request with validation feedback

#### Scenario: Team size locked after registration or match generation
- **WHEN** a team tournament already has pending registrations, active registrations, or generated matches
- **THEN** the API rejects team size changes

### Requirement: Tournament roster request structure is bounded before downstream work
The API MUST validate the `userIds` collection for team-roster eligibility and submission before invoking an application service, module contract, transaction, or database operation.

#### Scenario: Missing roster collection rejected early
- **WHEN** a roster eligibility or submission request has a null or missing `userIds` collection
- **THEN** the API returns a validation-problem response keyed to `userIds`
- **AND** no roster application service or downstream dependency is invoked

#### Scenario: Oversized roster collection rejected early
- **WHEN** a roster eligibility or submission request contains more than 50 user IDs
- **THEN** the API returns a validation-problem response keyed to `userIds`
- **AND** no roster application service or downstream dependency is invoked

#### Scenario: Empty user ID rejected early
- **WHEN** a roster eligibility or submission request contains `Guid.Empty`
- **THEN** the API returns a validation-problem response keyed to `userIds`
- **AND** no roster application service or downstream dependency is invoked

#### Scenario: Duplicate roster user ID rejected early
- **WHEN** a roster eligibility or submission request contains the same user ID more than once
- **THEN** the API returns a validation-problem response keyed to `userIds` instead of silently deduplicating the roster
- **AND** no roster application service or downstream dependency is invoked

#### Scenario: Structurally valid roster reaches business validation
- **WHEN** a roster eligibility or submission request contains at most 50 unique, non-empty user IDs
- **THEN** the existing exact-size, captain, membership, lifecycle, and duplicate-participation rules remain authoritative

### Requirement: Duplicate participation prevention
The API MUST prevent a user from pending or active participation more than once in the same tournament across individual registrations, captain participation, and team roster membership.

#### Scenario: Team member already registered individually
- **WHEN** a captain selects a roster member who already has an active individual registration for the same tournament
- **THEN** the API rejects the team roster change

#### Scenario: Member already pending on another roster
- **WHEN** a captain selects a roster member who has a pending roster confirmation for another team in the same tournament
- **THEN** the API rejects the team roster change

#### Scenario: Member already active on another roster
- **WHEN** a captain selects a roster member who is already active on another team registration for the same tournament
- **THEN** the API rejects the team roster change

#### Scenario: Captain already participating through another team
- **WHEN** a captain attempts to submit a team roster while already pending or actively participating in the same tournament through another team registration
- **THEN** the API rejects the team registration

#### Scenario: Individual registration blocked by pending roster
- **WHEN** a user attempts to register individually while they have a pending roster confirmation in the same tournament
- **THEN** the API rejects the individual registration

#### Scenario: Individual registration blocked by active roster
- **WHEN** a user attempts to register individually while actively participating through a team roster in the same tournament
- **THEN** the API rejects the individual registration

#### Scenario: Concurrent duplicate requests safe
- **WHEN** concurrent requests attempt to create duplicate pending or active participation for the same tournament and user
- **THEN** at most one request succeeds
- **AND** the API preserves a single pending or active participation record for that tournament and user

### Requirement: Eligibility check endpoints
The API MUST expose REST endpoints that let the front-end quickly validate tournament eligibility before attempting registration or roster mutations.

#### Scenario: Current user individual eligibility checked
- **WHEN** an authenticated user requests their eligibility for an individual tournament
- **THEN** the API returns whether the user is eligible
- **AND** returns machine-readable reason codes when the user is not eligible

#### Scenario: Team registration eligibility checked
- **WHEN** a captain requests eligibility for registering a team in a team tournament
- **THEN** the API returns whether the team can submit a roster
- **AND** returns machine-readable reason codes for tournament state, captain authority, deleted team, exact team-size, or duplicate participation failures

#### Scenario: Roster candidate eligibility checked
- **WHEN** a captain requests eligibility for proposed roster members
- **THEN** the API returns per-user eligibility results using privacy-safe user identifiers
- **AND** identifies duplicate participation, non-membership, deleted user, and exact-size validation failures

#### Scenario: Eligibility endpoint does not replace mutation validation
- **WHEN** a client performs a registration, confirmation, roster edit, or removal after calling an eligibility endpoint
- **THEN** the API revalidates all eligibility and authorization rules during the mutation

### Requirement: Admin registration management
Admins MUST be able to inspect registrations and remove users or teams from tournaments, but MUST NOT add users, add teams, swap roster members, or force confirmations. The existing admin registration list MUST support optional `page` and `pageSize` query parameters, default omitted values to page 1 and page size 20, reject non-positive values before service invocation, cap positive page size at 50, and preserve its raw-array JSON response, route, and authorization.

#### Scenario: Admin lists registrations
- **WHEN** an admin requests tournament registrations for a tournament
- **THEN** the API returns the requested bounded page of current individual registrations, pending team registrations, active team registrations, roster confirmation state, and registration state

#### Scenario: Admin lists default registration page
- **WHEN** an admin requests tournament registrations without `page` or `pageSize`
- **THEN** the API returns at most the first 20 registrations in the existing raw JSON array

#### Scenario: Admin receives validation for invalid registration page
- **WHEN** an admin requests tournament registrations with `page` or `pageSize` less than one
- **THEN** the API returns a validation problem before invoking the registration service

#### Scenario: Admin removes individual user from individual tournament
- **WHEN** an admin removes a user from an individual tournament
- **THEN** the API hard-deletes the user's individual registration

#### Scenario: Admin cannot remove single pending roster member
- **WHEN** an admin attempts to remove one selected member from a pending team tournament roster
- **THEN** the API does not expose an endpoint for that operation
- **AND** the admin must remove the pending team registration if the roster must be invalidated

#### Scenario: Admin removes team from tournament
- **WHEN** an admin removes a team from a tournament
- **THEN** the API hard-deletes the team's pending or active registration
- **AND** removes related pending roster confirmations and confirmation notifications for that team registration

#### Scenario: Admin cannot add or swap roster members
- **WHEN** an admin attempts to add a user, add a team, swap roster members, or force-confirm a roster member
- **THEN** the API does not expose an endpoint for that operation

#### Scenario: Non-admin cannot use admin removals
- **WHEN** a non-admin client calls an admin registration removal endpoint
- **THEN** the API rejects the request as forbidden

### Requirement: External registration URL removal
The API MUST remove external registration URL behavior from the primary tournament registration model.

#### Scenario: Tournament creation does not accept registration URL
- **WHEN** an admin creates a tournament
- **THEN** the API does not require or accept an external registration URL as part of the tournament registration model

#### Scenario: Tournament update does not preserve registration URL
- **WHEN** an admin updates a tournament
- **THEN** the API does not require, accept, or return an external registration URL as registration metadata

#### Scenario: Registration does not depend on URL
- **WHEN** an authenticated user or captain registers for a tournament
- **THEN** the API evaluates the internal registration request without reading external registration URL data

### Requirement: Registration projections and privacy
The API MUST expose registration views that are efficient and privacy-safe for the caller's authorization level.

#### Scenario: Public registration projection omits private user fields
- **WHEN** an anonymous or public client reads tournament details with registrations
- **THEN** embedded users and roster members omit email, Auth0 IDs, deleted state, timestamps, pending confirmation state, confirmation tokens, notification identifiers, and private account metadata

#### Scenario: Public registration projection includes active registrations only
- **WHEN** an anonymous or public client reads tournament details with registrations
- **THEN** pending registrations and pending roster confirmations are omitted from the public registration projection

#### Scenario: Current user registration state returned
- **WHEN** an authenticated user reads registration state for a tournament
- **THEN** the API identifies whether the current user is registered individually, has a pending roster confirmation, is confirmed on an active roster, can unregister, can confirm, or can manage a captained team registration

#### Scenario: Registration list avoids N+1 queries
- **WHEN** the API returns tournament registration lists, eligibility responses, or public registration projections
- **THEN** it retrieves registration, confirmation, team, and roster data using bounded projections or includes instead of per-row follow-up queries

#### Scenario: Public active rosters returned
- **WHEN** a public client reads tournament details with active team registrations
- **THEN** the API returns the full active roster for each active team registration using privacy-safe member fields

### Requirement: Transient registration data cleanup
The API MUST physically remove transient pending registration, roster confirmation, and notification data when it is no longer actionable.

#### Scenario: Team unregister cleanup
- **WHEN** a captain unregisters a pending team registration before tournament start
- **THEN** the API deletes the pending team registration, roster member confirmation rows, and related roster confirmation notifications

#### Scenario: Roster replacement cleanup
- **WHEN** a captain replaces a pending roster before tournament start
- **THEN** the API deletes or withdraws obsolete roster member confirmation rows and related notification records before creating the new pending confirmation set

#### Scenario: Admin pending team removal cleanup
- **WHEN** an admin removes a pending team registration
- **THEN** the API deletes the pending team registration, roster member confirmation rows, and related roster confirmation notifications

#### Scenario: Cleanup keeps registration queries bounded
- **WHEN** registration, eligibility, or current-user notification queries run
- **THEN** they filter by tournament, user, and active or pending state using indexed predicates
- **AND** they do not scan stale withdrawn confirmation records

### Requirement: Legacy admin route removal
The API MUST remove unused legacy admin participant mutation routes that bypass internal registration and confirmation rules.

#### Scenario: Legacy user registration route removed
- **WHEN** a client calls the previous admin route for directly adding or removing a user from a tournament's registered users
- **THEN** the API no longer exposes that route

#### Scenario: Legacy team registration route removed
- **WHEN** a client calls the previous admin route for directly adding or removing a team from a tournament's registered teams
- **THEN** the API no longer exposes that route

### Requirement: Historical registration display snapshots
Tournament MUST persist the username and team-name display facts captured when an individual or
team roster registration is created, while retaining the external user and team IDs as references.

#### Scenario: Individual registration captures user display data
- **WHEN** an authenticated user registers for an individual tournament
- **THEN** Tournament stores the user's ID and username-at-registration snapshot

#### Scenario: Team roster captures historical display data
- **WHEN** a captain submits a valid team roster
- **THEN** Tournament stores the team ID and team-name-at-registration snapshot
- **AND** stores each selected user ID and username-at-registration snapshot

#### Scenario: Existing registrations are migrated
- **WHEN** the Phase 11 migration is applied to an existing database
- **THEN** existing registration and roster rows are backfilled from their referenced user and team
  records where those records still exist
- **AND** no existing registration row is deleted

#### Scenario: Public registration JSON remains stable
- **WHEN** a client reads tournament registration data after the snapshot migration
- **THEN** the existing registration and roster JSON property names and privacy rules remain
  unchanged

### Requirement: Resource-oriented tournament registration routes
The API MUST expose authenticated resource-oriented routes under
`/v1/lan/tournaments/{tournamentId}` for individual registration, registration eligibility, team
roster submission, and roster-member confirmation while preserving the existing registration rules
and response DTO JSON shapes. The former `/v1/lan/games/{gameId}` routes MUST NOT be exposed.

#### Scenario: Current user creates an individual registration resource
- **WHEN** an authenticated user sends `PUT /v1/lan/tournaments/{tournamentId}/registrations/individual/me`
- **THEN** the API MUST apply the existing individual-registration rules and return the existing registration response JSON shape with `tournamentId`

#### Scenario: Client reads individual or team registration eligibility
- **WHEN** an authenticated client sends `GET /v1/lan/tournaments/{tournamentId}/registrations/individual/eligibility` or `GET /v1/lan/tournaments/{tournamentId}/registrations/teams/{teamId}/eligibility`
- **THEN** the API MUST return the existing eligibility result for the requested registration resource

#### Scenario: Captain calculates proposed team roster eligibility
- **WHEN** an authenticated team captain sends `POST /v1/lan/tournaments/{tournamentId}/registrations/teams/{teamId}/roster/eligibility` with proposed roster user ids
- **THEN** the API MUST return the existing proposed-roster eligibility result without changing registration or roster state

#### Scenario: Captain replaces the team registration roster
- **WHEN** an authenticated team captain sends `PUT /v1/lan/tournaments/{tournamentId}/registrations/teams/{teamId}/roster`
- **THEN** the API MUST apply the existing roster submission, replacement, and validation rules and return the existing registration response JSON shape

#### Scenario: Roster member confirms their roster-member resource
- **WHEN** an authenticated selected roster member sends `PATCH /v1/lan/tournaments/{tournamentId}/registrations/roster-members/{rosterMemberId}` with confirmation status `Confirmed`
- **THEN** the API MUST apply the existing roster-confirmation rules and return the existing registration response JSON shape

#### Scenario: Unsupported roster-member confirmation update is rejected
- **WHEN** a client sends a roster-member confirmation update with a status other than `Confirmed`
- **THEN** the API MUST reject the request without changing the roster member or registration state

#### Scenario: Registration action routes are absent
- **WHEN** a client calls the former `/v1/lan/games/{gameId}/registrations` action routes
- **THEN** the API MUST NOT expose those routes or aliases

### Requirement: Roster selection response actions

An authenticated selected roster member MUST be able to accept or decline their own actionable pending roster selection. A decline MUST release that user from the tournament roster while retaining the pending team registration and every other roster member's recorded response.

#### Scenario: Selected member declines a pending roster place
- **WHEN** an authenticated selected member declines their own pending roster place while the tournament is scheduled
- **THEN** the API MUST remove only that member from the pending team roster
- **AND** the API MUST retain the team registration in pending confirmation state
- **AND** the declining user MUST become eligible for other participation in that tournament subject to the normal eligibility rules

#### Scenario: Decline preserves other responses
- **WHEN** one pending selected member declines
- **THEN** confirmed, auto-confirmed, and pending responses for all other roster members MUST remain unchanged
- **AND** the captain MUST be able to repair the incomplete registration by submitting an exact-size replacement roster

#### Scenario: Decline retry is privacy-safe and idempotent
- **WHEN** an authenticated user declines a roster-member identifier that is missing, no longer pending, or not owned by that user
- **THEN** the API MUST make no state change
- **AND** MUST return the same successful no-content response without disclosing whether another user's selection exists

#### Scenario: Unchanged responses survive roster repair
- **WHEN** a captain replaces an incomplete pending roster with an exact-size roster
- **THEN** the API MUST preserve confirmation status and confirmation time for every unchanged selected member
- **AND** MUST require a response only from newly selected non-captain members

### Requirement: Current-user roster selection notifications

The API MUST expose a bounded authenticated query for the current user's actionable pending roster selections. Pending roster-member rows MUST be the authoritative notification source so the result remains correct after reload without separate notification persistence.

#### Scenario: Current user lists pending roster selections
- **WHEN** an authenticated user requests their roster selection notifications
- **THEN** the API MUST return pending selections for scheduled tournaments only
- **AND** each item MUST include roster member, tournament, and team identifiers; tournament and team display names; the team logo URL when available; and the selection timestamp

#### Scenario: Notification query is paged and deterministic
- **WHEN** the current user requests a valid page
- **THEN** the API MUST return an envelope containing items, total count, page, and page size
- **AND** MUST order selections by newest selection timestamp and then roster-member identifier
- **AND** omitted paging values MUST default to page 1 and page size 20
- **AND** positive page size MUST be capped at 50

#### Scenario: Invalid notification page is rejected
- **WHEN** page or page size is less than one
- **THEN** the API MUST return a validation problem before querying roster selections

#### Scenario: Resolved selection disappears after reload
- **WHEN** a pending selection is confirmed, declined, withdrawn by roster replacement, or removed with its team registration
- **THEN** it MUST no longer appear in the authoritative current-user notification query

### Requirement: Roster response realtime invalidation

The API MUST attempt roster confirmation realtime delivery after the corresponding database commit so connected user and team clients can refresh their authoritative state. Realtime delivery is advisory; the pending-selection query remains authoritative after reload or reconnect.

#### Scenario: Confirmation publishes committed change
- **WHEN** a selected member confirms a pending roster place successfully
- **THEN** the API MUST attempt a `Confirmed` roster confirmation change to the affected user and team targets after commit

#### Scenario: Decline publishes committed change
- **WHEN** a selected member declines a pending roster place successfully
- **THEN** the API MUST attempt a `Declined` roster confirmation change to the affected user and team targets after commit

#### Scenario: Replacement publishes changed actionable selections
- **WHEN** a captain replaces a pending roster
- **THEN** the API MUST attempt `Withdrawn` for removed pending selections and `Pending` for newly selected pending members after commit
- **AND** MUST NOT publish a new pending change for unchanged roster members whose response is preserved

#### Scenario: Advisory delivery fails after a member response

- **WHEN** confirmation or decline is committed and realtime delivery fails
- **THEN** the API MUST still report the committed member response as successful
- **AND** a later pending-selection query MUST reflect the committed state

### Requirement: Exact-size roster activation

A team tournament registration MUST become active only when its current roster has the tournament's exact required size and every current roster member is confirmed or auto-confirmed.

#### Scenario: Incomplete roster cannot activate
- **WHEN** a member confirms after another selected member has declined or been removed
- **THEN** the team registration MUST remain pending until the captain submits an exact-size roster and all required responses are complete

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

