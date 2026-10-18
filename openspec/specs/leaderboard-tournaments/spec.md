# leaderboard-tournaments Specification

## Purpose
TBD - created by archiving change issue-49-leaderboard-tournaments. Update Purpose after archive.
## Requirements
### Requirement: Leaderboard configuration

The API MUST support `Leaderboard` tournaments configured with exactly one ranking metric, `HighestScore` or `FastestTime`. Leaderboards MUST be individual, MUST NOT require or use registration, match format, finals format, generated matches, or round scheduling, and MUST expose their metric in tournament details. The metric MAY change while the tournament is scheduled and MUST NOT change after start. Non-leaderboard tournaments MUST reject leaderboard metrics and retain their existing behavior.

#### Scenario: Admin creates a score leaderboard
- **WHEN** an admin creates a leaderboard with `HighestScore`
- **THEN** the tournament MUST expose the leaderboard bracket and metric

#### Scenario: Invalid configuration is rejected
- **WHEN** a leaderboard omits its metric or uses team participation
- **THEN** the API MUST reject the configuration without persistence

### Requirement: Hybrid participant identity

A leaderboard participant MUST be either one linked LAN user identity or one independently identified guest with a nonblank display name. The first attempt MAY establish a participant. Linked users MUST be unique within a tournament. Equal guest display names MUST NOT merge, and an existing participant id MUST allow further attempts for that exact guest or linked user.

#### Scenario: Equal guest names remain distinct
- **WHEN** an admin records first attempts for two new guests with the same display name
- **THEN** the system MUST create two participant ids and rank them independently

#### Scenario: Linked user is reused
- **WHEN** an admin records another attempt using a linked user already present in the leaderboard
- **THEN** the system MUST attach it to the existing participant

### Requirement: Metric-specific attempts

Only authenticated admins MUST be able to add, correct, or remove leaderboard attempts. Mutations MUST be accepted only while a leaderboard tournament is in progress and MUST atomically revalidate tournament state, metric, participant identity, and attempt ownership. Highest-score attempts MUST accept a nonnegative decimal score with at most 12 integral and 6 fractional digits. Fastest-time attempts MUST accept a positive elapsed duration in whole milliseconds. Each request MUST provide only its metric's value. Attempt history MUST remain available to admin consumers until attempts are explicitly removed; changing the best result MUST NOT delete history. Removing a participant's final attempt MUST also remove that participant from the tournament. Concurrent linked-user creation or stale correction/removal MUST fail without partial mutation.

#### Scenario: Wrong metric value is rejected
- **WHEN** a score leaderboard receives a duration or a time leaderboard receives a score
- **THEN** the API MUST reject the attempt without persistence

#### Scenario: Completed results are immutable
- **WHEN** an admin tries to add, correct, or remove an attempt after completion
- **THEN** the API MUST reject the mutation without changing leaderboard data

#### Scenario: Removing a non-final attempt preserves the participant
- **WHEN** an admin removes one of a participant's multiple attempts
- **THEN** the participant MUST remain with all remaining attempts

#### Scenario: Removing the final attempt removes the participant
- **WHEN** an admin removes a participant's only attempt
- **THEN** the attempt and participant MUST no longer appear in persisted admin or public leaderboard data
- **AND** the linked LAN user MUST be recordable again as a new participant

### Requirement: Deterministic public ranking

The leaderboard MUST use only each participant's best attempt: maximum score for `HighestScore` and minimum duration for `FastestTime`. Equal best values MUST share competition ranks such as `1, 1, 3`. Stable ordering within a tie MUST use participant id without changing rank. Adding a worse attempt MUST NOT reduce the existing best result. Public responses MUST expose the metric, rank, participant id, display name, linked-user or guest kind, optional linked user id, and metric-specific best value, and MUST NOT expose attempt history or mutation metadata.

#### Scenario: Worse attempt preserves best result
- **WHEN** a participant records an attempt worse than their current best
- **THEN** the public best result and rank MUST continue to use the better attempt

#### Scenario: Tie uses competition ranking
- **WHEN** two participants share the best value and another follows
- **THEN** their ranks MUST be `1`, `1`, and `3` in deterministic order

### Requirement: Admin attempt history

An authenticated admin MUST be able to retrieve every leaderboard participant and attempt needed to correct result entry. The response MUST include participant identity, attempt ids, metric-specific values, timestamps, and concurrency tokens. Public callers MUST NOT access this projection.

#### Scenario: Admin reviews history
- **WHEN** an admin retrieves leaderboard management data while results exist
- **THEN** all nonremoved attempts MUST be returned under their participant

### Requirement: Leaderboard lifecycle and placements

A scheduled leaderboard MUST start with zero participants and MUST transition to `InProgress` without generating matches. Completion MUST fail unless at least one participant has a valid best result. Completion MUST freeze the current competition ranks as final placements, including placements for guests, and the final public leaderboard MUST agree with those placements. Reset from completed or canceled MUST remove participants, attempts, leaderboard placements, and generated-match remnants and return a clean scheduled tournament. Existing lifecycle behavior for other brackets MUST remain unchanged.

#### Scenario: Empty leaderboard starts but cannot complete
- **WHEN** an admin starts an empty leaderboard and then completes it
- **THEN** start MUST succeed without matches and completion MUST be rejected

#### Scenario: Guest wins
- **WHEN** a guest has the winning best result at completion
- **THEN** the guest MUST receive the first-place placement without a LAN user id

#### Scenario: Reset clears leaderboard state
- **WHEN** an admin resets a completed or canceled leaderboard
- **THEN** participants, attempts, placements, and matches MUST be empty

### Requirement: Leaderboard attempt-history route
The API MUST expose `GET /v1/lan/tournaments/{tournamentId}/leaderboard/attempts` to authenticated admins as the read route for the existing leaderboard attempt-history projection. It MUST preserve the existing admin response shape and MUST NOT expose the former `GET /v1/lan/tournaments/{tournamentId}/leaderboard/admin` route as an alias. Anonymous `GET /v1/lan/tournaments/{tournamentId}/leaderboard` MUST continue to expose live rankings without attempt history, and tournament details MUST continue to expose finalized placements.

#### Scenario: Admin reads leaderboard attempt history
- **WHEN** an authenticated admin requests `GET /v1/lan/tournaments/{tournamentId}/leaderboard/attempts`
- **THEN** the API MUST return the existing participant and attempt-history projection

#### Scenario: Anonymous caller reads the public leaderboard
- **WHEN** an anonymous caller requests `GET /v1/lan/tournaments/{tournamentId}/leaderboard`
- **THEN** the API MUST return live rankings without attempt history, and finalized placements MUST remain available through tournament details

#### Scenario: Former admin-history route is unavailable
- **WHEN** a client requests `GET /v1/lan/tournaments/{tournamentId}/leaderboard/admin`
- **THEN** the API MUST NOT expose that route as an alias

### Requirement: Tournament lifecycle concurrency conflicts

When a tournament lifecycle or configuration save fails because the tournament aggregate changed concurrently, the API MUST return a conflict response with a code that identifies the affected tournament type. A leaderboard conflict MUST keep code leaderboard_changed and message "The tournament or leaderboard changed. Refresh and try again." A non-leaderboard conflict MUST use code tournament_changed and message "The tournament changed. Refresh and try again." The failed operation MUST NOT persist its changes.

#### Scenario: Non-leaderboard lifecycle save conflicts

- **WHEN** another operation changes a non-leaderboard tournament while a lifecycle save is in progress
- **THEN** the API MUST return HTTP 409 with code tournament_changed and message "The tournament changed. Refresh and try again."
- **AND** the failed lifecycle change MUST NOT be persisted

#### Scenario: Leaderboard lifecycle conflict keeps its contract

- **WHEN** another operation changes a leaderboard tournament while a lifecycle save is in progress
- **THEN** the API MUST retain HTTP 409 with code leaderboard_changed and message "The tournament or leaderboard changed. Refresh and try again."
- **AND** the failed lifecycle change MUST NOT be persisted

