# admin-collection-paging Specification

## Purpose
Define bounded and deterministic paging for administrative user and tournament-registration collections.
## Requirements
### Requirement: Bounded administrative user collection
The API MUST support optional `page` and `pageSize` query parameters on the existing no-query administrative user collection. It MUST default omitted values to page 1 and page size 20, reject non-positive supplied values with a validation problem before user-service invocation, and cap a positive page size at 50. The response MUST remain the existing raw user JSON array and MUST preserve the existing route and authorization behavior.

#### Scenario: Default administrative user page
- **WHEN** an admin requests the user collection without `query`, `page`, or `pageSize`
- **THEN** the API returns at most the first 20 users as the existing raw JSON array

#### Scenario: Invalid user page rejected early
- **WHEN** an admin requests the no-query user collection with `page` or `pageSize` less than one
- **THEN** the API returns a validation problem before invoking the user service

#### Scenario: User page size capped
- **WHEN** an admin requests the no-query user collection with a positive `pageSize` greater than 50
- **THEN** the API returns at most 50 users

#### Scenario: User search remains cursor based
- **WHEN** an authenticated caller includes the `query` key on the user collection route with any `page` value
- **THEN** the API MUST retain the existing cursor search and existing search page-size semantics
- **AND** it MUST ignore `page`

### Requirement: Deterministic and overflow-safe administrative user paging
The no-query administrative user collection MUST order users by `NormalizedUsername` and then `Id`, apply paging before materialization, calculate the offset without integer overflow, and return an empty raw array when the requested offset cannot be represented by the query provider.

#### Scenario: Equal user names have stable page membership
- **WHEN** two or more administrative users have equal normalized usernames
- **THEN** their IDs deterministically break the ordering tie before paging

#### Scenario: User page offset overflows query-provider range
- **WHEN** an administrative user request specifies a positive page whose offset exceeds `Int32.MaxValue`
- **THEN** the API returns an empty raw array without an overflow exception

### Requirement: Bounded administrative registration collection
The API MUST support optional `page` and `pageSize` query parameters on the existing admin tournament-registration collection. It MUST default omitted values to page 1 and page size 20, reject non-positive supplied values with a validation problem before registration-service invocation, and cap a positive page size at 50. The response MUST remain the existing raw registration JSON array and MUST preserve the existing route and admin authorization behavior.

#### Scenario: Default administrative registration page
- **WHEN** an admin requests a tournament's registration collection without paging parameters
- **THEN** the API returns at most the first 20 registrations as the existing raw JSON array

#### Scenario: Invalid registration page rejected early
- **WHEN** an admin requests the registration collection with `page` or `pageSize` less than one
- **THEN** the API returns a validation problem before invoking the registration service

#### Scenario: Registration page size capped
- **WHEN** an admin requests the registration collection with a positive `pageSize` greater than 50
- **THEN** the API returns at most 50 registrations

### Requirement: Deterministic and overflow-safe administrative registration paging
The administrative tournament-registration collection MUST order registrations by `Kind`, `Status`, `CreatedAtUtc`, and then `Id`; apply paging before materialization and DTO enrichment; calculate the offset without integer overflow; and return an empty raw array when the requested offset cannot be represented by the query provider.

#### Scenario: Equal registration values have stable page membership
- **WHEN** registrations share kind, status, and creation timestamp
- **THEN** their IDs deterministically break the ordering tie before paging

#### Scenario: Registration page offset overflows query-provider range
- **WHEN** an administrative registration request specifies a positive page whose offset exceeds `Int32.MaxValue`
- **THEN** the API returns an empty raw array without an overflow exception

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

