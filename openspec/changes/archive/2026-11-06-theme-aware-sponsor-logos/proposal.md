## Why

Sponsor logos currently have one version, so a logo that reads well on a light
surface can disappear on the dark theme. Administrators need an optional second
version while existing sponsors and clients retain the required default logo.

## What Changes

- Add nullable `LightLogoUrl` alongside the required `LogoUrl`; no existing
  logo value is changed or copied.
- Accept optional multipart `LightLogo` during sponsor creation and editing, and
  update-only `RemoveLightLogo` to explicitly clear the variant.
- Add `lightLogoUrl` to sponsor responses and `sponsorLightLogoUrl` to the
  tournament sponsor placement response used by detail and featured reads.
- Apply the existing `SponsorLogo` media purpose and cleanup rules to both
  images, including failed mutations, replacements, removals, and sponsor
  deletion.
- Add the nullable URL to lifecycle event payloads in an old-payload-compatible
  position; Discovery keeps its current default-image search projection and
  tolerates legacy payloads that omit the field.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `sponsorship-module-boundary`: adds the optional logo field, multipart edit
  behavior, response field, and two-image lifecycle.
- `tournament-module-boundary`: exposes the optional sponsor logo through the
  shared tournament sponsor placement projection.
- `featured-homepage-tournaments`: includes the optional sponsor logo on a
  present homepage sponsor placement.
- `media-storage-boundary`: validates and cleans up both sponsor logo variants
  under the existing sponsor media purpose.
- `module-eventing`: extends sponsor lifecycle payloads compatibly for existing
  durable messages.
- `discovery-search-projections`: consumes both legacy and extended sponsor
  events without changing the existing default-image search projection.

## Impact

The change affects the Sponsorship domain, persistence, API forms and
responses, Sponsorship contracts and durable events, Tournament sponsor
projections, Discovery sponsor event handlers and rebuild compatibility,
PostgreSQL migrations, and focused Sponsorship, Tournament, Discovery, and API
tests. Existing routes, authorization, media validation, event ordering, and
clients that send or read only `LogoUrl` remain compatible.
