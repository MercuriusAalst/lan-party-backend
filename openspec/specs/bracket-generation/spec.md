# bracket-generation Specification

## Purpose

Defines how single and double elimination brackets are seeded, how byes are placed and advanced, and the double elimination grand final. Double elimination with exactly two participants cannot complete; this is a known open product decision.
## Requirements
### Requirement: Elimination bracket seeding
Single and double elimination brackets MUST use classic seeding (1 vs P, 2 vs P-1, recursively) over P = the smallest power of two not below the participant count n. The first round MUST contain exactly P - n byes, given to the top seeds. No first-round match MUST pair two byes, and no participant MUST advance through more than one bye round.

#### Scenario: Byes go to the top seeds
- **WHEN** an elimination bracket is generated for 5 participants
- **THEN** the first round MUST contain 3 byes, assigned to seeds 1, 2 and 3
- **AND** seeds 4 and 5 MUST play each other

#### Scenario: No empty first-round match
- **WHEN** an elimination bracket is generated for any participant count that is not a power of two
- **THEN** every first-round match MUST contain at least one real participant
- **AND** every second-round upper-bracket match MUST be fillable by two real participants

### Requirement: Double elimination lower-bracket byes
A double elimination lower-bracket slot MUST be marked BYE only when the match feeding it can never produce a participant, such as the loser slot of a first-round bye. A lower-bracket participant MUST NOT be eliminated or advanced before its real opponent has arrived. When a lower-bracket match with one real participant is automatically decided by a BYE, its winner MUST be propagated to its linked next match, including through consecutive BYE matches.

#### Scenario: Loser of a played match is not skipped
- **WHEN** an upper-bracket match between two real participants completes
- **THEN** its loser MUST enter a lower-bracket slot that is not marked BYE
- **AND** that participant MUST play a lower-bracket match before being eliminated

#### Scenario: Slot fed only by a bye is a BYE
- **WHEN** a lower-bracket slot is fed only by the loser of a first-round bye
- **THEN** the slot MUST be marked BYE
- **AND** the lone opposing participant MUST advance automatically
- **AND** its winner MUST be assigned to the next match when that slot is empty
- **AND** if that next match is also a BYE, advancement MUST continue through the chain

### Requirement: Double elimination grand final
A double elimination bracket MUST have a single grand final between the upper- and lower-bracket winners, without a bracket reset. The grand final winner MUST win the tournament even when the upper-bracket winner has then lost only once.

#### Scenario: Lower-bracket winner wins the grand final
- **WHEN** the lower-bracket winner wins the grand final
- **THEN** the tournament MUST be decided without an additional reset match

### Requirement: Bye advancement provenance
A participant advanced by a bye during bracket generation MUST be recorded with no source match (null). A participant advanced by a played match MUST record that match's id as its source.

#### Scenario: Bye winner has no source match
- **WHEN** a bracket is generated and a bye winner is placed in its next match
- **THEN** the participant's source match id MUST be null rather than an empty id

### Requirement: Recover existing lower-bracket BYE advancement
The API MUST expose administrator-only `POST /v1/lan/tournaments/{tournamentId}/matches/recover-bye-advancement` to recover missing lower-bracket BYE advancement for one existing double-elimination tournament that is in progress. Recovery MUST use the existing bracket and persisted participant assignments, MUST NOT regenerate matches, and MUST be transactional and idempotent. It MAY propagate the winner of a lower BYE match when exactly one non-BYE participant exists and the winner matches that participant. A completed BYE match is eligible only when it has no loser, score, forfeit side, or reported score; its result fields MUST remain unchanged. It MAY assign an automatic BYE winner only into a linked downstream slot that is empty and whose match has no result. If a required destination has a different participant in that slot or has a result, the operation MUST return HTTP 409 without saving any changes. An ambiguous completed BYE match MUST return HTTP 409 without saving any changes. Recovery MUST check each source match's `ResultVersion` before it propagates a winner into a missing downstream slot; if that source changes concurrently, recovery MUST return HTTP 409 and MUST NOT save downstream assignments. Repeating a successful recovery MUST make no changes. Recovery MUST NOT alter completed results or occupied slots. Success MUST return HTTP 200 with `{ "matchesUpdated": number }`; zero means no changes were needed.

#### Scenario: Recover an empty unplayed destination
- **WHEN** an administrator requests recovery and a completed lower-bracket BYE match has a winner absent from its empty, unplayed next-match slot
- **THEN** recovery MUST assign that winner to the slot
- **AND** the operation MUST continue through any newly eligible BYE matches

#### Scenario: Repeated recovery is idempotent
- **WHEN** an administrator repeats recovery after all eligible BYE winners are already propagated
- **THEN** the operation MUST report no changes
- **AND** persisted matches MUST remain unchanged

#### Scenario: Recover an already recorded BYE winner
- **WHEN** an in-progress tournament has a completed lower BYE match with one non-BYE participant, that participant as winner, no loser or scores, and a missing downstream assignment
- **THEN** recovery MUST propagate that winner
- **AND** the completed match's result fields MUST remain unchanged

#### Scenario: Reject an ambiguous completed BYE match
- **WHEN** a completed lower BYE match has a different winner, loser, or score
- **THEN** recovery MUST return HTTP 409 without saving any changes

#### Scenario: Concurrent reversal prevents stale recovery
- **WHEN** a lower BYE match changes after recovery reads it but before recovery commits
- **THEN** recovery MUST return HTTP 409
- **AND** its downstream participant assignments MUST NOT be saved

#### Scenario: Recovery detects conflicting live state
- **WHEN** a required next-match slot contains a different participant or its match has a result
- **THEN** recovery MUST fail without saving any assignments from that request

#### Scenario: Non-admin cannot recover a tournament
- **WHEN** an authenticated caller without the administrator role requests recovery
- **THEN** the API MUST deny the request
