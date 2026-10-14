## MODIFIED Requirements

### Requirement: Targeted match access

Ordinary public and authenticated action reads MUST query only the requested match and its tournament metadata. They MUST NOT materialize sibling matches in the tournament. Reversal MAY traverse only the bounded downstream match graph required to validate and clear provenance.

#### Scenario: Public read of a large bracket

- **WHEN** an anonymous caller requests one match from a tournament with many other matches
- **THEN** the API MUST return the requested match without loading sibling matches

## ADDED Requirements

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
