## Context

Kestrel applies `MediaUploadRequestLimits.MaxRequestBodySize`, currently one
configured file plus a 65,536-byte multipart envelope, as the host-wide request
body cap. Sponsor create and update may contain two logo files. Their combined
body can exceed this cap before form binding and Media validation run, even when
each individual file is valid.

The host also configures `FormOptions.MultipartBodyLengthLimit`, which bounds the
entire multipart body rather than an individual file. Raising it alone would
not bypass Kestrel's global cap for non-sponsor requests, but sponsor endpoints
need a higher per-request Kestrel allowance.

## Goals / Non-Goals

**Goals:**

- Accept two sponsor logo files whose individual lengths meet the configured
  file limit and whose aggregate multipart body fits two files plus the
  existing envelope allowance.
- Preserve the configured per-file Media validation.
- Preserve the host-wide one-file cap for Tournament, Team, non-media, and all
  other requests.
- Keep the behavior configuration-driven and compatible during rolling deploy.

**Non-Goals:**

- Raising the configured image file-size limit.
- Adding upload slots to non-sponsor endpoints.
- Allowing arbitrary multipart field or header growth.
- Persisting request-size state or adding deployment coordination.

## Decisions

### Derive a separate sponsor aggregate limit

Extend `MediaUploadRequestLimits` with a sponsor two-logo body limit calculated
as twice the configured file size plus the existing 64 KiB envelope. Keep the
existing single-file request limit as Kestrel's host default.

Set `FormOptions.MultipartBodyLengthLimit` to the sponsor aggregate because the
form parser needs to read both files, while global Kestrel keeps non-sponsor
requests at one file plus envelope.

### Apply the override through endpoint metadata

Map a standard request-size metadata value onto sponsor POST and PATCH only.
Endpoint metadata middleware applies that value to the current request's
`IHttpMaxRequestBodySizeFeature` before authentication, form binding, or service
execution. Other endpoints receive no override and remain under the host default.

### Keep Media validation authoritative per file

Media continues comparing each `MediaUpload.Length` to
`FileStorage:MaxFileSizeInMB`. The larger aggregate host allowance therefore
does not permit an individually oversized file to be stored.

## Risks / Trade-offs

- A sponsor request may carry up to two maximum-sized files, doubling the bytes
  Kestrel accepts for those two routes -> retain the envelope, exact file count,
  authentication, streaming limits, and per-file validation.
- Endpoint metadata could be applied too late for Kestrel -> integration tests
  use real Kestrel sockets and assert service invocation or HTTP 413 behavior.
- Future multi-file endpoints might reuse metadata incorrectly -> calculate and
  attach the limit only at the explicit sponsor file-count boundary.

## Migration Plan

Deploy the backend change with the existing `FileStorage:MaxFileSizeInMB`
setting. No data migration, state, or coordinated frontend change is required.
Rollback restores the host-wide single-file behavior without schema changes.

## Open Questions

None.
