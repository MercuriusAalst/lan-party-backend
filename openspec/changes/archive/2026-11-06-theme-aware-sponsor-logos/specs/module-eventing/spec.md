## ADDED Requirements

### Requirement: Sponsor lifecycle events add the light logo compatibly

`SponsorCreated` and `SponsorUpdated` payloads MUST include nullable
`LightLogoUrl` after all existing constructor fields and MUST preserve the
existing sponsor identity, name, tier, default logo URL, info URL, description,
event type, source ordering, and transactional outbox behavior. An older durable
payload that omits `LightLogoUrl` MUST deserialize with a null value. New payloads
MUST round-trip the stored optional URL without requiring other event contract
fields to be reinterpreted.

#### Scenario: Legacy sponsor event is dispatched

- **WHEN** the dispatcher reads a queued `SponsorCreated` or `SponsorUpdated`
  JSON payload written before this change
- **THEN** it MUST deserialize successfully with `LightLogoUrl = null`
- **AND** existing consumers MUST receive the same default sponsor facts as before

#### Scenario: Sponsor edit changes only the light logo

- **WHEN** a sponsor light-logo URL is added, replaced, or removed
- **THEN** the committed `SponsorUpdated` event MUST contain the updated nullable
  `LightLogoUrl`
- **AND** the event and sponsor state MUST remain in the same transaction
