## ADDED Requirements

### Requirement: Deleted users leave live team participation
When a user account is deleted, the Teams module MUST consume the durable user-deleted event and, for every non-deleted team that references the user, MUST in one transaction per team remove the user's membership, cancel every pending invite addressed to the user, and run captain succession when the user was the captain. Cancelled invites MUST keep their row with status Cancelled. This system cleanup MUST NOT be blocked by protected tournament rosters. Processing the same deletion again MUST NOT change any team. The Identity module MUST NOT write Teams data.

#### Scenario: Deleted member is removed
- **WHEN** a user who is a non-captain member of a team is deleted
- **THEN** the user MUST no longer be a member of that team
- **AND** the team captain and the other members MUST be unchanged

#### Scenario: Pending invites to a deleted user are cancelled
- **WHEN** a user with pending team invites is deleted
- **THEN** every pending invite addressed to the user MUST become Cancelled
- **AND** the invite rows MUST remain for history

#### Scenario: Protected tournament roster does not block cleanup
- **WHEN** a deleted user is on a team roster of a scheduled or in-progress tournament
- **THEN** the user MUST still be removed from the team

#### Scenario: Captain of several teams is deleted
- **WHEN** a user who captains several teams is deleted
- **THEN** every one of those teams MUST be processed

#### Scenario: Deletion is processed twice
- **WHEN** the same user deletion is processed again
- **THEN** no team, membership or invite MUST change

#### Scenario: Every deletion route triggers cleanup
- **WHEN** a user deletes their own account, or an administrator deletes the account by id or by username
- **THEN** the same team cleanup MUST be applied

### Requirement: Automatic captain succession
When a deleted user was the captain of a team, the Teams module MUST appoint the first eligible remaining member as captain. A candidate is eligible when the account is not deleted and the candidate captains fewer than the maximum number of non-deleted teams, not counting this team. Candidates MUST be ordered with members without a recorded join time first, ordered by account creation time and then by user id, followed by members with a recorded join time ordered by join time and then by user id. Ineligible candidates MUST be skipped. When no candidate is eligible the team MUST be left without a captain. Every automatic succession MUST publish a durable captain-succession event for other modules and MUST log teams left without a captain.

#### Scenario: Longest-serving member succeeds
- **WHEN** a deleted captain's team has members who joined at different times
- **THEN** the member with the earliest join time MUST become captain

#### Scenario: Equal join times
- **WHEN** two candidates have the same join time
- **THEN** the candidate with the lower user id MUST become captain

#### Scenario: Legacy members precede recorded joiners
- **WHEN** some members have no recorded join time and others do
- **THEN** the members without a recorded join time MUST be considered first, ordered by account creation time

#### Scenario: Ineligible candidates are skipped
- **WHEN** the first candidate is deleted or already captains the maximum number of teams
- **THEN** that candidate MUST be skipped and the next eligible candidate MUST become captain

#### Scenario: No eligible successor
- **WHEN** no remaining member is eligible
- **THEN** the team MUST have no captain
- **AND** the system MUST log the team as left without an eligible successor

### Requirement: Team join seniority
The Teams module MUST record a membership join time when a team is created for its creator and when an invite is accepted. Leaving and rejoining MUST record a new join time. Captain transfer and succession MUST NOT change any join time. Memberships created before this requirement MUST keep no join time and MUST NOT be backfilled. The join time MUST NOT be exposed in any API response.

#### Scenario: Rejoining resets seniority
- **WHEN** a member leaves a team and later accepts a new invite to it
- **THEN** the membership MUST carry the new join time

#### Scenario: Captain transfer keeps join times
- **WHEN** captainship is transferred
- **THEN** no membership join time MUST change

### Requirement: Deleted users cannot receive team authority
Team creation, invite creation, invite acceptance, captain transfer and administrative captain change MUST re-read the affected user's current account state inside the write transaction, serialized with account deletion, and MUST reject a deleted user without persisting any change. Requests from a deleted current user MUST be rejected.

#### Scenario: Transfer to a deleted member is rejected
- **WHEN** a captain transfers captainship to a member whose account is deleted
- **THEN** the API MUST reject the transfer
- **AND** the captain, membership and team version MUST be unchanged

#### Scenario: Invite to a deleted user is rejected
- **WHEN** a captain invites a deleted user
- **THEN** the API MUST reject the invite without creating it

#### Scenario: Deleted user cannot accept an invite
- **WHEN** a deleted user's pending invite is accepted
- **THEN** the API MUST reject the response without adding a membership

#### Scenario: Deletion races a team write
- **WHEN** a user's account deletion commits while a write that adds the user to a team or makes the user captain is in progress
- **THEN** the write MUST either be rejected or be followed by the deleted-user cleanup, and the user MUST NOT remain a member or captain

### Requirement: Live team views exclude deleted users
Live team projections MUST exclude deleted members, deleted captains and pending invites addressed to deleted users. This applies to team details, search and paging, public team profiles, current-user team summaries, captain pickers, pending invite lists, Teams contract roster snapshots, registration eligibility, membership mutation guards, captained-team lookups and the captain limit. Identity user lookups used for history MUST remain unfiltered.

#### Scenario: Deleted captain is not shown as captain
- **WHEN** a team's captain account is deleted and cleanup has not yet run
- **THEN** live team projections MUST NOT report that user as captain or member

#### Scenario: Deleted captain does not count toward eligibility
- **WHEN** a deleted user requests team registration eligibility or a captained-team lookup
- **THEN** the deleted user MUST NOT be treated as the captain of any team

### Requirement: Legacy deleted-user repair
The schema migration that introduces membership join times MUST replay the durable user-deleted event for every deleted user who is still a member or captain of a non-deleted team, the recipient of a pending invite, or a participant of a Scheduled or InProgress tournament, so the same idempotent cleanup handlers repair the data. The replay MUST NOT enqueue a second undelivered event for a user who already has one, and its first delivery attempt SHOULD be delayed so an instance from the previous release cannot acknowledge it without the cleanup handlers during a rolling deployment.

#### Scenario: Legacy corrupt data is repaired
- **WHEN** a deleted user is still a member or captain of a team when the migration runs
- **THEN** the replayed event MUST remove the user and run captain succession through the regular event dispatch

#### Scenario: Replay runs twice
- **WHEN** the replay step runs again before the events are delivered
- **THEN** no additional event MUST be enqueued

#### Scenario: Historical-only references are not replayed
- **WHEN** a deleted user is referenced only by completed or canceled tournaments
- **THEN** no event MUST be enqueued for that user
