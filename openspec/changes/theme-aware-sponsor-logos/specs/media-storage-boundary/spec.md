## ADDED Requirements

### Requirement: Sponsor logo variants use the sponsor media policy

Sponsorship MUST store and validate the optional light sponsor logo through the
same `IMediaModule` boundary and `MediaPurpose.SponsorLogo` purpose as the
required default logo. The existing supported types, size limit, filename and
content-type validation, multipart request limit, replacement cleanup, and
compensation behavior MUST apply equally to both files. Saving one sponsor logo
MUST NOT bypass Media validation or write outside Media.

#### Scenario: Invalid light logo is rejected

- **WHEN** a sponsor create or update supplies a light logo that violates the
  existing sponsor media policy
- **THEN** Media MUST reject it using the existing validation outcome
- **AND** Sponsorship MUST NOT persist the sponsor mutation

#### Scenario: Two-image mutation fails

- **WHEN** either requested sponsor logo is stored but the following sponsor
  mutation or durable event does not commit
- **THEN** Sponsorship MUST compensate every newly stored logo from that request
  through the Media contract
