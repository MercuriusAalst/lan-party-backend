# api-error-responses Specification

## Purpose
Define the HTTP status codes and response body shape the API uses for mapped application errors.
## Requirements
### Requirement: Authorization failures distinguish forbidden from unauthenticated
The API MUST answer an authenticated caller who fails a team-captain or match-participant check with HTTP 403 and a stable error code. It MUST use `team_captain_required` when the caller is not the team's captain, `match_participant_required` when the caller owns neither side of the match, and `match_own_side_required` when a participant targets the other side. A request without an authenticated user id MUST continue to receive HTTP 401.

#### Scenario: Non-captain performs a captain-only action
- **WHEN** an authenticated user who is not the team captain mutates the team or its tournament registration
- **THEN** the API MUST return HTTP 403 with code `team_captain_required` without changing state

#### Scenario: Non-participant acts on a match
- **WHEN** an authenticated user who owns neither side of a match confirms, reports or forfeits it
- **THEN** the API MUST return HTTP 403 with code `match_participant_required` without changing the match

#### Scenario: Participant forfeits the opposing side
- **WHEN** a non-admin participant requests a forfeit for the other side
- **THEN** the API MUST return HTTP 403 with code `match_own_side_required`

#### Scenario: Authenticated user id is missing
- **WHEN** a request reaches an operation that requires the current user id and none is present
- **THEN** the API MUST return HTTP 401

### Requirement: Mapped errors use problem details
The API MUST return every mapped application error as `application/problem+json` with `status`, `title` (the status reason phrase) and `detail` (the error message), plus a `message` extension equal to the detail. Forbidden and conflict errors MUST also include their `code` extension.

#### Scenario: Forbidden error body
- **WHEN** an operation fails with a forbidden error
- **THEN** the response MUST be `application/problem+json` with status 403, the error message in `detail` and `message`, and its `code`

#### Scenario: Error without a code
- **WHEN** an operation fails with a not-found or validation error
- **THEN** the response MUST be `application/problem+json` with `detail` and `message` and MUST NOT include a `code`
