## MODIFIED Requirements

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
