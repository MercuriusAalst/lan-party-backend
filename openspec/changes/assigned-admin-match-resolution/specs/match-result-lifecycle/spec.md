## MODIFIED Requirements

### Requirement: Score consensus and correction

The API MUST accept non-negative, match-format-valid score reports from an eligible side of a match that is awaiting ended confirmation, awaiting a score, or reversed. Submitting a score MUST record that side's ended confirmation and MUST NOT require either side to confirm the match ended first. The first report MUST start a five-minute server deadline for the other side. A matching report MUST complete the result and advance the bracket transactionally. A differing report MUST enter a correction window of five minutes; if the window expires without agreement, the lifecycle MUST enter AdminResolutionRequired. A single report MUST NOT become the official result without administrator resolution: if the five-minute deadline expires with only one report, the lifecycle MUST enter AdminResolutionRequired.

#### Scenario: First report records the ending and starts the window

- **WHEN** an eligible side submits a valid score before the match has both ended confirmations
- **THEN** the API MUST record that side's ended confirmation together with the report
- **AND** the lifecycle MUST enter ScoreConfirmation with the five-minute server deadline
- **AND** the opponent MUST NOT have to confirm the match ended first

#### Scenario: Matching reports complete

- **WHEN** both eligible sides report the same valid score before the deadline
- **THEN** the result MUST be completed once
- **AND** winner/loser and linked next-match assignments MUST be advanced in the same transaction

#### Scenario: Single report at the deadline requires admin resolution

- **WHEN** only one eligible side has submitted a valid score and the server clock reaches the five-minute deadline
- **THEN** the lifecycle MUST be AdminResolutionRequired
- **AND** the match MUST NOT have a winner, a final score or a bracket advancement
- **AND** the reported score MUST remain stored as that side's unconfirmed claim and MUST NOT be treated as final
- **AND** player score commands MUST be rejected
- **AND** repeating deadline processing MUST NOT publish a second resolution-required event

#### Scenario: Differing reports require correction

- **WHEN** eligible sides report different valid scores
- **THEN** the lifecycle MUST be Disputed
- **AND** the correction deadline MUST be returned in the projection

#### Scenario: Unresolved correction requires admin

- **WHEN** the correction deadline expires without matching reports
- **THEN** the lifecycle MUST be AdminResolutionRequired
- **AND** player score commands MUST be rejected
- **AND** the assigned admin MUST receive a durable resolution-required notification
- **AND** when an assigned admin exists, that admin MUST remain the primary notification recipient even though any eligible global admin may resolve
- **AND** when no assigned admin exists, the notification MUST use the global-admin fallback recipient

The `MatchResolutionRequiredIntegrationEvent` MUST expose an explicit assigned-admin or global-admin recipient contract and an additive `Reason` of `Disputed` or `MissingOpponentReport`; a payload without a reason MUST be read as `Disputed`. It MUST be published exactly once for each transition into AdminResolutionRequired, whether the cause is an expired correction window or an expired single-report window. The tournament event consumer MUST persist the notification with the originating platform message id as an idempotency key, and MUST NOT create a second notification when the same event is delivered again.

#### Scenario: Each side has one correction

- **WHEN** a disputed match is within its correction window
- **THEN** each side MAY replace its own report at most once
- **AND** a second correction by the same side MUST be rejected without changing the report

## ADDED Requirements

### Requirement: Pending admin resolution list

The API MUST expose an administrator-only `GET /lan/matches/pending-resolution` endpoint under the same admin authorization as match resolution. It MUST return the matches in AdminResolutionRequired, in tournaments that are in progress, that the calling administrator is shown. A match MUST be listed when any of these holds: (a) the tournament's assigned admin is the caller; (b) the tournament has no assigned admin; (c) the assigned admin is no longer an administrator or no longer exists; (d) the match has been waiting for resolution for at least the configured escalation wait. Any other match of a tournament assigned to a different, still valid administrator MUST NOT be listed. Each item MUST contain the match id, tournament id and name, both participant names, the participants' unconfirmed reported scores when present, a reason of `Disputed` or `MissingOpponentReport`, an assignment of `Assigned`, `Unassigned`, `AssignedAdminUnavailable` or `Escalated`, and the UTC time at which resolution became required when known. When several rules hold the assignment MUST be chosen in the order Assigned, Unassigned, AssignedAdminUnavailable, Escalated, so the assigned administrator always sees their match as `Assigned`, even after it has escalated.

The time a match has been waiting MUST be the time of the latest resolution-required notification of its current escalation cycle, that is one issued after the match's last result or reversal. When that time is unknown, because the notification has not been written yet or the match predates it, rule (d) MUST NOT apply to the match; rules (a) to (c) still apply. The escalation wait MUST be configurable as `Tournament:MatchResolution:EscalationAfterMinutes`, MUST default to 10 minutes, and MUST be rejected at startup unless it is greater than zero.

Whether an assigned administrator is still an administrator MUST be checked only for the distinct assigned administrators of matches that are neither the caller's nor unassigned, MUST NOT be checked when there are no such matches, and MAY be answered from a per-process cache of at most 60 seconds. A failed check MUST be treated as the assigned administrator still being valid, so that a failure never widens the list.

The list MUST be derived from persisted match state so that a resolved match no longer appears, and reading it MUST NOT change any state. The endpoint MUST NOT expose the assigned administrator's raw user id. This requirement only governs visibility: resolving a match MUST remain permitted for any administrator.

#### Scenario: Assigned admin sees their pending matches

- **WHEN** the assigned administrator of a tournament requests the pending list while one of its matches awaits resolution after a conflict or a missing opponent report
- **THEN** the response MUST include that match with the matching reason and the reported scores

#### Scenario: Other admin does not see an assigned tournament's matches

- **WHEN** an administrator who is not the assigned admin requests the pending list
- **THEN** matches of tournaments assigned to another, still valid administrator MUST be omitted until they escalate

#### Scenario: Legacy tournament without assigned admin

- **WHEN** a tournament has no assigned admin and one of its matches awaits resolution
- **THEN** every administrator MUST see that match in the pending list

#### Scenario: Assigned admin is no longer an administrator

- **WHEN** the assigned administrator of a tournament has lost the admin role or no longer exists and one of its matches awaits resolution
- **THEN** every other administrator MUST see that match with the assignment `AssignedAdminUnavailable`, even when its waiting time is unknown

#### Scenario: Match escalates to every administrator

- **WHEN** a match has awaited resolution for at least the escalation wait and its assigned administrator is still valid
- **THEN** every administrator MUST see it with the assignment `Escalated`
- **AND** the assigned administrator MUST keep seeing it with the assignment `Assigned`
- **AND** before the wait has elapsed an administrator who is not the assigned admin MUST NOT see it

#### Scenario: Waiting time is unknown

- **WHEN** a match of a valid assigned administrator has no resolution-required notification of its current cycle
- **THEN** it MUST NOT be escalated to other administrators and MUST remain visible to the assigned administrator

#### Scenario: Assignment labels follow their priority

- **WHEN** a match is both unavailable-assigned and past the escalation wait
- **THEN** another administrator MUST see it as `AssignedAdminUnavailable`, not `Escalated`

#### Scenario: Administrator lookup fails

- **WHEN** the check that an assigned administrator is still an administrator fails
- **THEN** that administrator MUST be treated as still valid
- **AND** the list MUST NOT gain matches because of the failure

#### Scenario: Administrator lookups are bounded

- **WHEN** the list is requested and every candidate match is the caller's or unassigned
- **THEN** no administrator check MUST be made
- **WHEN** several matches share one other assigned administrator
- **THEN** that administrator MUST be checked once, and repeated requests within 60 seconds MUST NOT check again

#### Scenario: Non-admin is denied

- **WHEN** an authenticated caller without the admin role requests the pending list
- **THEN** the API MUST deny the request

#### Scenario: Resolved match disappears

- **WHEN** an administrator resolves a match that was in the pending list
- **THEN** the match MUST no longer appear in the pending list
