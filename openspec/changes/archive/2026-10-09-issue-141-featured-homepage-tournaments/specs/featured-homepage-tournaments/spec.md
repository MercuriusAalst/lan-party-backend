## Purpose

Define persisted homepage tournament curation and its public read behavior.

## ADDED Requirements

### Requirement: Persisted ordered homepage tournament selection
The Tournament module MUST persist one ordered selection of exactly four distinct tournament identifiers as homepage presentation metadata. An admin-authorized API MUST replace the complete selection atomically. The API MUST reject a replacement unless it contains exactly four distinct identifiers that each identify an existing eligible tournament. A tournament is eligible when it exists and its status is not Canceled. Invalid replacements MUST return a validation problem and MUST leave the previous selection intact.

#### Scenario: Administrator saves an ordered selection
- **WHEN** an administrator replaces the selection with four distinct eligible tournament IDs
- **THEN** the system MUST persist the complete order and return the saved IDs
- **AND** the saved order MUST survive process restarts and be shared by API replicas

#### Scenario: Invalid replacement is rejected without partial writes
- **WHEN** an administrator submits fewer or more than four IDs, duplicates, a missing tournament, or a canceled tournament
- **THEN** the API MUST return a validation problem identifying the invalid selection
- **AND** the previously saved order MUST remain unchanged

#### Scenario: Non-admin cannot replace the selection
- **WHEN** a caller without the admin role sends a replacement request
- **THEN** the API MUST reject the request according to the existing authentication and authorization policy

### Requirement: Lightweight public featured tournament read
The API MUST expose an anonymous read that returns ordered featured tournament IDs and a `tournaments` array in the same order. Each card object MUST contain only `id`, `name`, `imageUrl`, `status`, `bracketType`, and `format`, which are the public fields used by the homepage cards. Enum values MUST use the existing string JSON representation. The read MUST NOT load or expose tournament match, registration, roster, placement, or leaderboard-attempt graphs. When no selection has been saved, it MUST preserve the existing public ordering by planned start time, name, and ID. It MUST omit missing and canceled tournaments from saved selections, preserve the relative order of remaining eligible IDs, and fill vacancies from eligible tournaments in that same deterministic order without duplicates. If fewer than four eligible tournaments exist, it MUST return all available eligible tournaments.

#### Scenario: No saved selection uses existing tournament ordering
- **WHEN** public clients read featured tournaments before any administrator has saved a selection
- **THEN** the API MUST return the first up to four eligible tournaments in the existing public order

#### Scenario: Saved selection is returned in saved order
- **WHEN** a public client reads featured tournaments after a selection is saved
- **THEN** the API MUST return the selected eligible tournaments in their saved relative order
- **AND** the card projection MUST contain only fields required to render those homepage cards

#### Scenario: Saved tournament is deleted or becomes ineligible
- **WHEN** a saved tournament is deleted or canceled
- **THEN** public reads MUST NOT expose it
- **AND** the API MUST fill its position from the next eligible tournament in deterministic public order when one is available
- **AND** the response MUST contain no duplicate tournament IDs

#### Scenario: Fewer than four eligible tournaments exist
- **WHEN** fewer than four eligible tournaments are available
- **THEN** public reads MUST return every available eligible tournament without failing

#### Scenario: Saved replacement is visible to all replicas
- **WHEN** an administrator successfully replaces the selection
- **THEN** the next uncached public read MUST observe the committed order


