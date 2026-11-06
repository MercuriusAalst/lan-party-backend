## Context

`Sponsor.LogoUrl` is required and is stored through the Media module with
`MediaPurpose.SponsorLogo`. Sponsor create and edit are multipart operations.
Their image is saved before the database mutation, while replacements and
deletions are cleaned up after the state change and outbox message commit.
Tournament placement DTOs use a sponsorship contract summary, and Discovery
consumes the sponsor lifecycle events to maintain a default-image search
projection.

The deployment permits rolling overlap between backend versions. Old replicas
must continue to read and write sponsors while the additive database column is
present, and queued durable events written without the new property must still
deserialize.

## Goals / Non-Goals

**Goals:**

- Preserve `LogoUrl` as the required default image and existing response field.
- Add a nullable light-coloured variant for dark themes.
- Make create, independent replacement, retention, and explicit removal
  unambiguous in multipart requests.
- Preserve media compensation and post-commit cleanup when two images are
  involved.
- Expose the variant in sponsor and tournament placement reads.
- Keep existing event, Discovery, and rolling-deployment behavior compatible.

**Non-Goals:**

- Selecting a logo based on the backend request or storing a theme preference.
- Enforcing logo colour on the server.
- Adding a second image column to Discovery search documents.
- Changing sponsor routes, authorization, media limits, formats, or validation.

## Decisions

### Persist one additive nullable column

Add nullable `LightLogoUrl` to the Sponsorship sponsor entity and model. Existing
rows remain unchanged with `NULL`, and `LogoUrl` stays required. This expand-only
migration works with old and new replicas during a rolling deployment and does
not copy or move media.

### Use explicit multipart semantics

Creation accepts the existing required `Logo` and optional `LightLogo`. Update
accepts optional `Logo` and `LightLogo`, plus `RemoveLightLogo` defaulting to
`false`. An omitted file preserves the corresponding stored URL. Supplying
`LightLogo` and `RemoveLightLogo=true` is rejected before media is saved. This
avoids interpreting a missing multipart file as a removal request.

### Keep image orchestration in the existing service

Both files use `MediaPurpose.SponsorLogo` and the same `IMediaModule` boundary.
The service saves only the files provided by the request. If any subsequent save
or the database/outbox transaction fails, it compensates every newly stored file.
After a successful commit it retires replaced or removed old URLs, and sponsor
deletion retires both stored URLs. Cleanup uses a non-cancelled token and logs
failures without changing the committed outcome, matching the existing media
contract.

### Extend projections and events additively

`GetSponsorDTO` gains `LightLogoUrl`; `SponsorSummary` gains nullable
`LightLogoUrl` at the end of its constructor; tournament placement gains
`SponsorLightLogoUrl`. The same placement projection feeds tournament detail and
featured tournaments. `SponsorCreated` and `SponsorUpdated` gain nullable
`LightLogoUrl` as their final optional constructor parameter so old JSON
payloads can omit it.

Discovery continues to project the required default `LogoUrl` because sponsor
documents are not rendered as public search results. Both old and new event
payloads update the same search document/source version. A rebuild reads the
existing sponsor search contract and therefore remains consistent with event
updates without an unrelated Discovery schema expansion.

## Risks / Trade-offs

- Two sequential media saves can leave a temporary orphan if compensation itself
  fails -> log the orphan and preserve the original failure, as with one image.
- Post-commit deletion is best effort and can leave an orphan -> retain the
  committed sponsor result and emit the existing warning diagnostic.
- A nullable event constructor parameter relies on serializer support for
  omitted optional values -> cover legacy JSON and round-trip event tests.
- Old replicas do not understand the new multipart field or column -> old
  requests continue to use the required default image, and the nullable database
  column does not require old code to write it.

## Migration Plan

1. Add the nullable `LightLogoUrl` column without backfill and deploy the backend
   before the frontend.
2. Keep `LogoUrl` mandatory and unchanged so old clients, old replicas, and
   existing consumers continue to operate.
3. During rollback, old replicas ignore the additive column and retain their
   existing behavior for the required logo.

## Open Questions

None.
