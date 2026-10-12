## Why

The tournament contact-admin picker only receives the first bounded set of administrators, so later eligible administrators cannot be selected once that set exceeds the endpoint limit. Adding page navigation lets clients reach the full eligible list while preserving the picker’s existing filter and response contract.

## What Changes

- Add an optional positive `page` query parameter to the existing admin picker route, defaulting to page 1.
- Preserve the existing page-size default and 50-item cap, query filtering, eligible-admin rules, authorization, rate limit, and raw array response.
- Thread paging through the identity module with a compatibility path for existing callers and test doubles.
- Apply deterministic, overflow-safe database paging before materializing results.

## Capabilities

### New Capabilities

### Modified Capabilities

- `admin-collection-paging`: Define paging for the admin contact picker route.

## Impact

- API contract: `GET /v1/lan/users/admins` gains optional `page`.
- Identity module contract and facade: add paged admin lookup while preserving the existing method contract.
- Tests: cover endpoint validation/default/cap and identity paging, filtering, and overflow.
- No persistence, migration, authorization, configuration, or dependency changes.

