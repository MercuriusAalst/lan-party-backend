## Why

Sponsor create and update can now carry two independently valid logo files.
Their combined multipart body can exceed the host-wide one-file request cap even
when each file is within the Media file-size limit, causing an early HTTP 413.

## What Changes

- Retain the existing one-file-plus-envelope cap as the default for every host
  request, including non-media JSON and single-image uploads.
- Apply a two-file-plus-envelope aggregate body cap only to sponsor create and
  update endpoints.
- Keep Media's configured per-file validation so one oversized sponsor file is
  still rejected even when the aggregate request allowance is higher.
- Add endpoint-level Kestrel tests for two valid files over the old total,
  oversized individual-file rejection, and protection of other endpoints.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `media-storage-boundary`: distinguishes the sponsor two-logo aggregate body
  allowance from the retained host-wide single-file and per-file limits.

## Impact

The API host request-size configuration, endpoint request-size metadata and
middleware, sponsor endpoint mapping, and API/Sponsorship integration tests are
affected. Database state, authorization, DTO shapes, Media purposes, and other
endpoint limits remain unchanged.
