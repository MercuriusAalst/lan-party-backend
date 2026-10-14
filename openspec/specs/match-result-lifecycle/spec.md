# Match result lifecycle

## Purpose

Define the privacy-safe match lifecycle projection, participant confirmations, score consensus,
forfeits, administrative resolution and reversal, targeted access, and transactional authorized
transitions.

## Requirements

### Requirement: Match lifecycle projection

The API MUST expose a privacy-safe lifecycle state, ended-confirmation flags, server-provided deadlines, and only the score reports permitted by the caller's relationship to the match. The projection MUST retain participant and bracket fields already returned by the match endpoint and MUST NOT expose moderation subjects or private notes. The anonymous projection MUST redact score report values. The authenticated action projection MAY expose the requesting participant's own report and MUST expose both reports to an eligible participant or team captain, the assigned tournament admin, or the global admin fallback when no assignment exists. Assignment MUST NOT be required for an authenticated global admin to perform an otherwise eligible administrative action.

#### Scenario: Public match is awaiting confirmations

- **WHEN** an anonymous caller requests a match whose participants have not both confirmed that play has ended
- **THEN** the API MUST return the match with the lifecycle state and confirmation flags
- **AND** it MUST omit authenticated identity details

#### Scenario: Client refreshes after a deadline

- **WHEN** a match is read after a server deadline has elapsed
- **THEN** the API MUST return the effective authoritative state
- **AND** any client countdown MUST be advisory only

#### Scenario: Unassigned authenticated viewer cannot inspect reports

- **WHEN** an authenticated user who owns neither side requests the match action projection
- **THEN** the API MUST omit both private score reports
- **AND** it MUST still return the public lifecycle state and available bracket fields

#### Scenario: Authorized participant sees both reports

- **WHEN** an authenticated participant or team captain requests the match action projection
- **THEN** the API MUST include both private score reports when present so the participant can compare and correct a dispute

#### Scenario: Assigned admin sees both reports

- **WHEN** the assigned tournament admin requests the match action projection
- **THEN** the API MUST include both private score reports when present

#### Scenario: Unassigned admin cannot inspect an assigned match

- **WHEN** an authenticated global admin who is not the assigned tournament admin requests an assigned match action projection
- **THEN** the API MUST omit both private score reports
- **AND** it MUST preserve eligible administrative capabilities for that global admin

#### Scenario: Protected action capabilities are authoritative

- **WHEN** an authenticated caller requests the match action projection
- **THEN** the API MUST return capability flags for resolve, administrative forfeit, and reversal
- **AND** each unavailable administrative capability MUST include a stable blocked reason

#### Scenario: Unassigned global admin can resolve

- **WHEN** an authenticated global admin who is not assigned to the tournament requests the action projection for a disputed or admin-resolution-required match
- **THEN** the API MUST report resolve as available when the tournament is in progress and the match lifecycle allows resolution
- **AND** the global admin MUST be able to resolve without reassignment
- **AND** the persisted result MUST record that resolver's identity and server UTC time

### Requirement: Targeted match access

Ordinary public and authenticated action reads MUST query only the requested match and its tournament metadata. They MUST NOT materialize sibling matches in the tournament. Reversal MAY traverse only the bounded downstream match graph required to validate and clear provenance.

#### Scenario: Public read of a large bracket

- **WHEN** an anonymous caller requests one match from a tournament with many other matches
- **THEN** the API MUST return the requested match without loading sibling matches

### Requirement: Participant end confirmation

An authenticated individual participant or team captain MUST be able to confirm that their side has ended the match. The API MUST reject users who are not a participant or captain, reject repeated/expired confirmations, and MUST allow score submission only after both sides have confirmed.

#### Scenario: Both sides confirm

- **WHEN** each eligible side confirms ended
- **THEN** the lifecycle MUST enter AwaitingScore
- **AND** the API MUST allow score reports from either eligible side

#### Scenario: Unauthorized confirmation

- **WHEN** a signed-in user who does not own either side confirms ended
- **THEN** the API MUST reject the command
- **AND** the match MUST remain unchanged

### Requirement: Score consensus and correction

The API MUST accept non-negative, match-format-valid score reports only after both ended confirmations. The first report MUST start a five-minute server deadline for the other side. A matching report MUST complete the result and advance the bracket transactionally. A differing report MUST enter a correction window of five minutes; if the window expires without agreement, the lifecycle MUST enter AdminResolutionRequired.

#### Scenario: Matching reports complete

- **WHEN** both eligible sides report the same valid score before the deadline
- **THEN** the result MUST be completed once
- **AND** winner/loser and linked next-match assignments MUST be advanced in the same transaction

#### Scenario: First report is auto-accepted at the exact deadline

- **WHEN** only one eligible side has submitted a valid score and the server clock reaches the five-minute deadline
- **THEN** the first score MUST become official and advance the bracket atomically
- **AND** repeating deadline processing MUST NOT create a second result

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

The `MatchResolutionRequiredIntegrationEvent` MUST expose an explicit assigned-admin or global-admin recipient contract. The tournament event consumer MUST persist the notification with the originating platform message id as an idempotency key, and MUST NOT create a second notification when the same event is delivered again.

#### Scenario: Each side has one correction

- **WHEN** a disputed match is within its correction window
- **THEN** each side MAY replace its own report at most once
- **AND** a second correction by the same side MUST be rejected without changing the report

### Requirement: Forfeit

An eligible participant MAY forfeit their own side through an explicit command. An admin MAY forfeit either side. A forfeit MUST be irreversible by normal participants, MUST complete the match with the other side as winner, and MUST advance the bracket transactionally.

#### Scenario: Participant forfeits

- **WHEN** an eligible participant confirms a forfeit command
- **THEN** the match MUST be marked Forfeited
- **AND** the opposing side MUST be the winner

### Requirement: Administrative resolution and reversal

An authenticated admin MUST be able to resolve a disputed or admin-resolution-required match with a final score. An authenticated admin MUST be able to reverse a completed or forfeited result only when linked next matches have no result and every populated downstream assignment affected by the source has proven provenance. The API MUST reject a reversal with an actionable reason when downstream play has started or completed, or when participant provenance cannot be established. Assignment of a primary tournament administrator MUST NOT be a prerequisite for an otherwise authorized global administrator to resolve, force-forfeit, or reverse a match. Each mutation MUST record the actual authenticated resolver and server UTC timestamp.

#### Scenario: Admin resolves dispute

- **WHEN** an admin submits a valid final score for a disputed match
- **THEN** the match MUST complete and bracket advancement MUST be transactional
- **AND** the projection MUST identify the completed lifecycle state

#### Scenario: Reversal is blocked by downstream result

- **WHEN** an admin requests reversal and a linked next match has a score, winner, loser, or forfeit result
- **THEN** the API MUST reject the request with a clear reason
- **AND** no match or bracket state MUST change

#### Scenario: Reversal fails closed when legacy provenance is unavailable

- **WHEN** an otherwise reversible result has a populated linked downstream assignment without a source edge that can be proven to belong to it
- **THEN** the API MUST reject the reversal with a stable blocked reason
- **AND** no match or bracket state MUST change

### Requirement: Transactional and authorized transitions

Every lifecycle mutation MUST revalidate the current match state, participant ownership, server deadline, and tournament status immediately before saving. Bracket advancement and reversal MUST commit atomically with the match result. Unauthorized or invalid commands MUST NOT partially mutate persisted state.

#### Scenario: Invalid mutation is atomic

- **WHEN** a lifecycle command fails authorization, deadline, or state validation
- **THEN** the API MUST return a machine-readable failure reason
- **AND** no match or linked bracket state MUST be persisted

### Requirement: Read-only match reads and background deadline processing

`GET /v1/lan/matches/{id}` and `GET /v1/lan/matches/{id}/me` MUST NOT persist state or publish events. For an in-progress tournament they MUST return the effective state of an expired deadline computed in memory. Only the background deadline processor MUST persist expired-deadline outcomes, advance the next match, and publish their events; this MAY lag the deadline by up to one poll interval (about 30 seconds). Each poll MUST process at most 100 expired matches, oldest deadline first. Each match MUST be saved independently so that a concurrency conflict skips only that match. At most one API instance MUST process deadlines at a time, enforced by a PostgreSQL advisory lock.

#### Scenario: Read after an expired deadline does not persist

- **WHEN** a match is read after its score confirmation deadline has elapsed and before the processor has run
- **THEN** the API MUST return the effective completed state
- **AND** no match, next-match assignment or event MUST be persisted by the read

#### Scenario: Processor persists and advances

- **WHEN** the deadline processor polls and finds an expired match
- **THEN** it MUST persist the outcome, advance the linked next match, and publish the completion or resolution-required event once

#### Scenario: Large backlog is bounded

- **WHEN** more than 100 matches have expired deadlines
- **THEN** one poll MUST process the 100 oldest and leave the rest for later polls

#### Scenario: Concurrent change skips one match

- **WHEN** one expired match changes concurrently while a batch is processed
- **THEN** only that match MUST be skipped and the other matches in the batch MUST still be saved

#### Scenario: Single processing instance

- **WHEN** several API instances poll at the same time
- **THEN** only the instance holding the advisory lock MUST process expired deadlines

### Requirement: Single administrative resolution route

The API MUST expose administrative score resolution only through `POST /v1/lan/matches/{id}/resolve`. It MUST NOT expose the former `PUT /v1/lan/matches/{id}` route as an alias. `GET /v1/lan/matches/{id}` MUST remain available.

#### Scenario: Admin resolves through the resolve route

- **WHEN** an authenticated admin submits a valid final score to `POST /v1/lan/matches/{id}/resolve`
- **THEN** the API MUST resolve the match exactly as specified by the administrative resolution requirement

#### Scenario: Former update route is unavailable

- **WHEN** a client sends `PUT /v1/lan/matches/{id}`
- **THEN** the API MUST NOT route the request to a match mutation
