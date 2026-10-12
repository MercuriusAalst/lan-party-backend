## Context

`GET /v1/lan/users/admins` currently filters Auth0 administrator membership against active local accounts, orders by normalized username and ID, and returns at most the requested page size. It has no page offset, so clients cannot reach eligible users past the first bounded result set.

## Goals / Non-Goals

**Goals:**

- Add stable page navigation to the existing admin picker.
- Keep existing callers and `IIdentityModule` fakes source-compatible.
- Keep paging in the database query and make large offsets safe.

**Non-Goals:**

- Change administrator eligibility, Auth0 role checks, filtering, authorization, rate limiting, response fields, or page-size limits.
- Add totals, cursors, caching, persistence changes, or Auth0 calls beyond the existing membership lookup.

## Decisions

- Extend the existing `GetAdminUsersAsync` contract with an optional `page` parameter after `cancellationToken`, defaulting to `1`. Existing callers keep compiling unchanged, and implementers that rely on the interface's default body keep compiling too. The concrete facade and the paging endpoint test double override the single method so the requested page is observed.
- Reuse the facade’s current eligibility predicate and deterministic `NormalizedUsername`, then `Id`, ordering. Calculate the offset in `long`; return an empty list if it exceeds `Int32.MaxValue`, otherwise apply `Skip` and `Take` before projection/materialization. This follows the existing no-query users paging behavior and avoids loading the whole admin list.
- Validate `page` at the endpoint before invoking the module; use the existing `SearchRequest` helpers for the existing page-size default and cap.

## Risks / Trade-offs

- [Risk] A very large valid page produces an empty result. → This matches the established administrative paging contract and avoids overflow or provider exceptions.
- [Risk] Test implementations that rely on the interface's default body cannot page internally. → The endpoint test double overrides the single method so page is observed; the default body exists only to preserve compatibility for existing implementers.

## Migration Plan

No database migration is required. Clients that do not send `page` continue to receive page 1; clients can request later pages by incrementing it. Rollback can remove page navigation from clients while keeping the existing first-page request behavior.

## Open Questions

None.

