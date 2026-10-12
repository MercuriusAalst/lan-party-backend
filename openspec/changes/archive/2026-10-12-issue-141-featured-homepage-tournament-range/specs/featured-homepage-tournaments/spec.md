## MODIFIED Requirements

### Requirement: Persisted ordered homepage tournament selection
The Tournament module MUST persist one ordered selection of one to four distinct tournament identifiers as homepage presentation metadata. An admin-authorized API MUST replace the complete selection atomically. The API MUST reject a replacement unless it contains between one and four distinct identifiers that each identify an existing eligible tournament. A tournament is eligible when it exists and its status is not Canceled. Invalid replacements MUST return a validation problem and MUST leave the previous selection intact.

#### Scenario: Administrator saves an ordered selection
- **WHEN** an administrator replaces the selection with one, three, or four distinct eligible tournament IDs
- **THEN** the system MUST persist the complete order and return the saved IDs
- **AND** the saved order MUST survive process restarts and be shared by API replicas

#### Scenario: Invalid replacement is rejected without partial writes
- **WHEN** an administrator submits a null or empty selection, more than four IDs, duplicate IDs, a missing tournament, or a canceled tournament
- **THEN** the API MUST return a validation problem identifying the invalid selection
- **AND** the previously saved order MUST remain unchanged

#### Scenario: Non-admin cannot replace the selection
- **WHEN** a caller without the admin role sends a replacement request
- **THEN** the API MUST reject the request according to the existing authentication and authorization policy

### Requirement: Lightweight public featured tournament read
The API MUST expose an anonymous read that returns ordered featured tournament IDs and a `tournaments` array in the same order. Each card object MUST contain only `id`, `name`, `imageUrl`, `status`, `bracketType`, `format`, and an optional singular `sponsorPlacement`, which are the public fields used by the homepage cards. Enum values MUST use the existing string JSON representation. A present `sponsorPlacement` MUST reuse the existing public-safe tournament sponsor projection and JSON shape, including the sponsor name, tier, logo URL, info URL, and description, and MUST be `null` for tournaments without a sponsor placement. Sponsor attribution MUST be resolved through the existing bounded Sponsorship module placement projection rather than per-card queries or a full graph load. The read MUST NOT load or expose tournament match, registration, roster, placement, or leaderboard-attempt graphs. When no selection has been saved, it MUST preserve the existing public ordering by planned start time, name, and ID and return up to four eligible tournaments. When a selection has been saved, it MUST omit missing and canceled tournaments, preserve the relative order of remaining eligible IDs, and fill vacancies from eligible tournaments in that same deterministic order without duplicates until the saved selection's count is reached. If fewer eligible tournaments exist than the applicable target count, it MUST return all available eligible tournaments.

#### Scenario: No saved selection uses existing tournament ordering
- **WHEN** public clients read featured tournaments before any administrator has saved a selection
- **THEN** the API MUST return the first up to four eligible tournaments in the existing public order

#### Scenario: Saved selection preserves count and order
- **WHEN** a public client reads featured tournaments after a selection of one to four IDs is saved
- **THEN** the API MUST return selected eligible tournaments in their saved relative order and MUST NOT pad the response beyond the saved selection's count
- **AND** the first returned ID MUST remain the primary featured tournament when it is still eligible
- **AND** the card projection MUST contain only fields required to render those homepage cards

#### Scenario: Sponsored card exposes public sponsor attribution
- **WHEN** a featured tournament has a sponsor placement
- **THEN** its card MUST include the singular `sponsorPlacement` with the same public-safe fields and JSON shape as the tournament detail read, including the sponsor name
- **AND** cards for tournaments without a placement MUST serialize `sponsorPlacement` as `null`
- **AND** the read MUST resolve all placements with a single bounded batch lookup instead of per-card queries

#### Scenario: Saved tournament is deleted or becomes ineligible
- **WHEN** a saved tournament is deleted or canceled
- **THEN** public reads MUST NOT expose it
- **AND** the API MUST fill its position from the next eligible tournament in deterministic public order when one is available, up to the saved selection's count
- **AND** the response MUST contain no duplicate tournament IDs

#### Scenario: Fewer eligible tournaments exist than the target count
- **WHEN** fewer eligible tournaments are available than the unsaved default of four or the saved selection's count
- **THEN** public reads MUST return every available eligible tournament without failing

#### Scenario: Saved replacement is visible to all replicas
- **WHEN** an administrator successfully replaces the selection
- **THEN** the next uncached public read MUST observe the committed order
