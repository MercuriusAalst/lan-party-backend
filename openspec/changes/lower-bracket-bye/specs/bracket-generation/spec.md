## MODIFIED Requirements

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

### Requirement: Recover existing lower-bracket BYE advancement
The API MUST expose administrator-only `POST /v1/lan/tournaments/{tournamentId}/matches/recover-bye-advancement` to recover missing lower-bracket BYE advancement for one existing tournament. Recovery MUST use the existing bracket and persisted participant assignments, MUST NOT regenerate matches, and MUST be transactional and idempotent. It MAY assign an automatic BYE winner only into a linked downstream slot that is empty and whose match has no result. If a required destination has a different participant in that slot or has a result, the operation MUST return HTTP 409 without saving any changes. Repeating a successful recovery MUST make no changes. Recovery MUST NOT alter completed results or occupied slots. Success MUST return HTTP 200 with `{ "matchesUpdated": number }`; zero means no changes were needed.

#### Scenario: Recover an empty unplayed destination
- **WHEN** an administrator requests recovery and a completed lower-bracket BYE match has a winner absent from its empty, unplayed next-match slot
- **THEN** recovery MUST assign that winner to the slot
- **AND** the operation MUST continue through any newly eligible BYE matches

#### Scenario: Repeated recovery is idempotent
- **WHEN** an administrator repeats recovery after all eligible BYE winners are already propagated
- **THEN** the operation MUST report no changes
- **AND** persisted matches MUST remain unchanged

#### Scenario: Recovery detects conflicting live state
- **WHEN** a required next-match slot contains a different participant or its match has a result
- **THEN** recovery MUST fail without saving any assignments from that request

#### Scenario: Non-admin cannot recover a tournament
- **WHEN** an authenticated caller without the administrator role requests recovery
- **THEN** the API MUST deny the request
