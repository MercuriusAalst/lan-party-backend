## REMOVED Requirements

### Requirement: Global rate limiting precedes Imageflow handling
**Reason**: `/images` is now public and served ahead of the security pipeline.
**Migration**: Replaced by "Public assets and health probes bypass the global limiter".

## ADDED Requirements

### Requirement: Public assets and health probes bypass the global limiter
The API host MUST serve `/images`, `/staticfiles`, and the Swagger UI ahead of the security pipeline, and MUST exclude `/health/live` and `/health/ready` from rate limiting, so these requests never consume or get rejected by a global fixed-window bucket.

#### Scenario: Repeated image or health request
- **WHEN** a caller repeats a request under `/images`, `/staticfiles`, `/health/live`, or `/health/ready` after its global fixed-window bucket is exhausted
- **THEN** the request MUST be served normally and MUST NOT receive the 429 rate-limit response
