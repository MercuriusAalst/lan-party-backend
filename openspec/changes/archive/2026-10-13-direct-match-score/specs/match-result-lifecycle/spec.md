## REMOVED Requirements

### Requirement: Participant end confirmation

**Reason**: Score submission now records the submitting side's ended confirmation, so the separate confirm-ended command, its route and its capability flag are removed by explicit product decision. Players enter a score and submit it.

**Migration**: Clients submit a score directly through the existing score route. The confirm-ended route and the `canConfirmEnded` action-state capability no longer exist, so the back-end and front-end must be deployed together. Ended-confirmation timestamps and lifecycle enum values stay persisted and projected.

## MODIFIED Requirements

### Requirement: Score consensus and correction

The API MUST accept non-negative, match-format-valid score reports from an eligible side of a match that is awaiting ended confirmation, awaiting a score, or reversed. Submitting a score MUST record that side's ended confirmation and MUST NOT require either side to confirm the match ended first. The first report MUST start a five-minute server deadline for the other side. A matching report MUST complete the result and advance the bracket transactionally. A differing report MUST enter a correction window of five minutes; if the window expires without agreement, the lifecycle MUST enter AdminResolutionRequired.

#### Scenario: First report records the ending and starts the window

- **WHEN** an eligible side submits a valid score before the match has both ended confirmations
- **THEN** the API MUST record that side's ended confirmation together with the report
- **AND** the lifecycle MUST enter ScoreConfirmation with the five-minute server deadline
- **AND** the opponent MUST NOT have to confirm the match ended first

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
