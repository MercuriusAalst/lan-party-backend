## ADDED Requirements

### Requirement: Tournament sponsor projection exposes both logo variants

The public-safe tournament sponsor placement projection MUST expose the existing
required sponsor logo as `sponsorLogoUrl` and the nullable optional variant as
`sponsorLightLogoUrl`. Both fields MUST be populated through the bounded
Sponsorship placement projection. Existing clients that omit or ignore
`sponsorLightLogoUrl` MUST continue to receive a non-null `sponsorLogoUrl`.

#### Scenario: Placed sponsor has a light logo

- **WHEN** a tournament read returns a placement for a sponsor with
  `LightLogoUrl`
- **THEN** the placement MUST expose both `sponsorLogoUrl` and
  `sponsorLightLogoUrl`

#### Scenario: Placed sponsor has only the default logo

- **WHEN** a tournament read returns a placement for a sponsor without
  `LightLogoUrl`
- **THEN** `sponsorLogoUrl` MUST remain present
- **AND** `sponsorLightLogoUrl` MUST be `null`

#### Scenario: Tournament placement lookup remains bounded

- **WHEN** a tournament response projects one or more sponsor placements
- **THEN** both logo values MUST come from the existing bounded sponsorship
  placement query without per-sponsor media or sponsorship queries
