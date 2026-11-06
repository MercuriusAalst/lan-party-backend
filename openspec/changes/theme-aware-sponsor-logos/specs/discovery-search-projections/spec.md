## ADDED Requirements

### Requirement: Sponsor search projection accepts both event payload versions

Discovery MUST continue to project active sponsors into its existing search
document shape using the required default logo. It MUST accept sponsor lifecycle
events both with a nullable `LightLogoUrl` and without that property in legacy
payloads. Repeated event delivery and rebuilds MUST remain idempotent against the
same source version rules, and a light-logo-only edit MUST update the sponsor
document using the current default logo without exposing an absent optional
field as a required search value.

#### Scenario: Legacy sponsor event updates Discovery

- **WHEN** Discovery receives a queued sponsor event that omits `LightLogoUrl`
- **THEN** it MUST apply the existing sponsor document upsert without a
  deserialization failure

#### Scenario: Rebuild matches event projection

- **WHEN** a sponsor has an optional light logo and Discovery rebuilds search
  documents from the current sponsorship contract
- **THEN** the rebuilt sponsor document MUST use the same required default logo
  and searchable metadata as the event projection
