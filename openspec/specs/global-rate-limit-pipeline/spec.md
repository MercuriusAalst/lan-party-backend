# global-rate-limit-pipeline Specification

## Purpose
Define global rate-limit ordering relative to authentication, authorization, and Imageflow processing.
## Requirements
### Requirement: Global rate limiting precedes authorization
The API host MUST authenticate each request before selecting its existing global rate-limit partition and MUST enforce the global limiter before authorization. Valid authenticated callers MUST continue to use the existing subject-claim partition, and callers without a valid authenticated identity MUST continue to use the existing remote-IP partition.

#### Scenario: Repeated anonymous protected request
- **WHEN** an anonymous caller repeats a request to a protected endpoint until its global fixed-window bucket is exhausted
- **THEN** the endpoint MUST return its normal authorization challenge before exhaustion and the existing 429 rate-limit response after exhaustion

#### Scenario: Repeated authenticated forbidden request
- **WHEN** an authenticated caller without the required role repeats a request to a protected endpoint until its global fixed-window bucket is exhausted
- **THEN** the endpoint MUST return its normal forbidden response before exhaustion and the existing 429 rate-limit response after exhaustion

### Requirement: Public assets and health probes bypass the global limiter
The API host MUST serve `/images`, `/staticfiles`, and the Swagger UI ahead of the security pipeline, and MUST exclude `/health/live` and `/health/ready` from rate limiting, so these requests never consume or get rejected by a global fixed-window bucket.

#### Scenario: Repeated image or health request
- **WHEN** a caller repeats a request under `/images`, `/staticfiles`, `/health/live`, or `/health/ready` after its global fixed-window bucket is exhausted
- **THEN** the request MUST be served normally and MUST NOT receive the 429 rate-limit response
