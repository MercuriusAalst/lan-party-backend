## ADDED Requirements

### Requirement: Sponsors support an optional light-coloured logo

Sponsorship MUST retain the required `LogoUrl` as the default dark-coloured
logo intended for light backgrounds and MAY store a nullable `LightLogoUrl` for
the light-coloured logo intended for dark backgrounds. Migration of existing
sponsors MUST leave `LogoUrl` unchanged, set no `LightLogoUrl`, and require no
media copy. Sponsor list, detail, create, and update responses MUST expose the
nullable JSON property `lightLogoUrl` while preserving `logoUrl`.

#### Scenario: Existing sponsor remains compatible

- **WHEN** an existing sponsor is read after the nullable column migration
- **THEN** its original `logoUrl` MUST remain unchanged
- **AND** its `lightLogoUrl` MUST be `null`

#### Scenario: Sponsor has both logo versions

- **WHEN** an authorized administrator creates or updates a sponsor with both
  `Logo` and `LightLogo` files
- **THEN** both files MUST be stored with `MediaPurpose.SponsorLogo`
- **AND** the response MUST expose both `logoUrl` and `lightLogoUrl`

### Requirement: Sponsor editing distinguishes retention from removal

Sponsor creation MUST require the existing multipart `Logo` field and MUST
accept optional multipart `LightLogo`. Sponsor update MUST keep the existing
optional `Logo` field, add optional multipart `LightLogo`, and add boolean
`RemoveLightLogo` defaulting to `false`. Omitting both logo fields MUST preserve
the stored URLs. Supplying only `LightLogo` or `Logo` MUST replace only that
variant. `RemoveLightLogo=true` MUST clear only the optional light-logo URL.
Requests that supply both `LightLogo` and `RemoveLightLogo=true` MUST return a
validation problem before saving media or mutating the sponsor.

#### Scenario: Unrelated sponsor edit retains both logos

- **WHEN** an administrator updates sponsor metadata without either logo file
  and without `RemoveLightLogo=true`
- **THEN** the existing default and optional light-logo URLs MUST be retained

#### Scenario: Administrator replaces one logo independently

- **WHEN** an administrator supplies a new `LightLogo` without a new `Logo`
- **THEN** only `lightLogoUrl` MUST change
- **AND** the existing `logoUrl` MUST remain

#### Scenario: Administrator explicitly removes the light logo

- **WHEN** an administrator supplies `RemoveLightLogo` with value `true` and no
  `LightLogo`
- **THEN** `lightLogoUrl` MUST become `null`
- **AND** the required `logoUrl` MUST remain

#### Scenario: Conflicting light-logo instructions are rejected

- **WHEN** an administrator supplies both `LightLogo` and
  `RemoveLightLogo=true`
- **THEN** the API MUST return a validation problem
- **AND** it MUST NOT store either request image or change the sponsor

### Requirement: Both sponsor logos share mutation lifecycle safety

Every stored sponsor image MUST use the existing `SponsorLogo` media purpose and
media validation. If media storage, sponsor persistence, outbox persistence, or
the transaction fails or is cancelled before commit, Sponsorship MUST compensate
every image newly stored by that request and preserve the original failure.
After a successful commit, Sponsorship MUST retire media URLs that were replaced
or explicitly removed. Sponsor deletion MUST retire both the default and
optional light-logo URLs. Post-commit cleanup failures MUST be logged as orphans
without changing the successful mutation result.

#### Scenario: Light-logo creation fails before commit

- **WHEN** the light-logo file is stored but sponsor creation or its lifecycle
  event does not commit
- **THEN** Sponsorship MUST attempt cleanup of every newly stored file from that
  request with a non-cancelled token
- **AND** no previously owned media MUST be deleted

#### Scenario: Sponsor edit commits

- **WHEN** a sponsor update commits with a replaced or removed logo URL
- **THEN** the state and matching event MUST commit first
- **AND** Sponsorship MUST subsequently attempt to delete the retired URL

#### Scenario: Sponsor deletion commits

- **WHEN** a sponsor with both logo URLs is deleted and its event commits
- **THEN** Sponsorship MUST attempt to delete both former logo URLs after commit

#### Scenario: Post-commit cleanup fails

- **WHEN** sponsor state and its lifecycle event commit but deleting one of the
  retired logo URLs fails
- **THEN** Sponsorship MUST log the orphan
- **AND** it MUST return the committed mutation outcome
