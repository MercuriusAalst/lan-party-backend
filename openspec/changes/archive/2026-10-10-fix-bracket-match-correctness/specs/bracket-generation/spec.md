## ADDED Requirements

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
A double elimination lower-bracket slot MUST be marked BYE only when the match feeding it can never produce a participant, such as the loser slot of a first-round bye. A lower-bracket participant MUST NOT be eliminated or advanced before its real opponent has arrived.

#### Scenario: Loser of a played match is not skipped
- **WHEN** an upper-bracket match between two real participants completes
- **THEN** its loser MUST enter a lower-bracket slot that is not marked BYE
- **AND** that participant MUST play a lower-bracket match before being eliminated

#### Scenario: Slot fed only by a bye is a BYE
- **WHEN** a lower-bracket slot is fed only by the loser of a first-round bye
- **THEN** the slot MUST be marked BYE and the lone opposing participant MUST advance automatically

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
