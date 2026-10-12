## ADDED Requirements

### Requirement: Admin contact picker supports bounded paging
The API MUST support optional `page` and `pageSize` query parameters on `GET /v1/lan/users/admins`. It MUST default omitted values to page 1 and page size 20, reject supplied values less than one with a validation problem before invoking the identity module, and cap a positive page size at 50. Paging MUST preserve the existing admin-only authorization, rate limit, query filtering, eligible administrator set, and raw JSON array of picker options.

#### Scenario: Default admin picker page
- **WHEN** an administrator requests the picker without `page` or `pageSize`
- **THEN** the API returns at most the first 20 eligible options as the existing raw JSON array

#### Scenario: Later admin picker page
- **WHEN** an administrator requests a positive page greater than one
- **THEN** the API returns that page of eligible options using the requested bounded page size

#### Scenario: Admin picker page size is capped
- **WHEN** an administrator supplies a positive `pageSize` greater than 50
- **THEN** the API returns at most 50 options for the requested page

#### Scenario: Invalid admin picker paging is rejected early
- **WHEN** an administrator supplies a `page` or `pageSize` less than one
- **THEN** the API returns a validation problem without invoking the identity module

#### Scenario: Admin picker query filtering is preserved
- **WHEN** an administrator supplies a query and a positive page
- **THEN** the API applies the existing normalized username filter to eligible administrators before returning the requested page

### Requirement: Admin picker pages are deterministic and overflow-safe
The identity module MUST order eligible active local administrators by normalized username and then ID, apply the requested offset and page size before materialization, and calculate the offset without integer overflow. If the offset exceeds the query provider’s supported `Int32` range, the API MUST return an empty raw array.

#### Scenario: Equal normalized usernames have stable page membership
- **WHEN** eligible administrators share a normalized username
- **THEN** their IDs deterministically break the ordering tie before paging

#### Scenario: Admin picker page offset exceeds provider range
- **WHEN** a positive page and page size produce an offset greater than `Int32.MaxValue`
- **THEN** the API returns an empty raw array without an overflow exception
