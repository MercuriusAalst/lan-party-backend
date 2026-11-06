## ADDED Requirements

### Requirement: Featured sponsor placement includes the light logo variant

The optional `sponsorPlacement` on a featured tournament card MUST include the
nullable `sponsorLightLogoUrl` alongside its existing `sponsorLogoUrl`. It MUST
reuse the same public-safe tournament sponsor placement projection as tournament
detail. A sponsor with only one logo MUST produce a non-null `sponsorLogoUrl` and
a null `sponsorLightLogoUrl`; the absence of either placement or optional logo
MUST remain compatible with existing clients.

#### Scenario: Featured placement exposes both variants

- **WHEN** a featured tournament has a sponsor placement whose sponsor has both
  logo URLs
- **THEN** the placement MUST expose both `sponsorLogoUrl` and
  `sponsorLightLogoUrl`

#### Scenario: Featured placement falls back to the default logo

- **WHEN** a featured tournament's placed sponsor has no light-logo URL
- **THEN** the placement MUST retain `sponsorLogoUrl`
- **AND** `sponsorLightLogoUrl` MUST be `null`
