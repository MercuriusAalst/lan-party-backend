## ADDED Requirements

### Requirement: Tournament prize metadata
Tournaments MUST support optional first-, second-, and third-place prize text. Each value MUST be no longer than 200 characters after trimming, and blank values MUST be stored as null. Authorized tournament create/update requests MUST be able to set and clear each prize independently.

#### Scenario: Tournament prize metadata is saved and cleared
- **WHEN** an administrator creates or updates a tournament with prize values
- **THEN** the system MUST persist each non-blank value after trimming
- **AND** MUST clear a prize when its field is explicitly submitted blank
- **AND** MUST preserve a prize field omitted from a PATCH request

#### Scenario: Prize value exceeds the limit
- **WHEN** an administrator submits a prize value longer than 200 characters after trimming
- **THEN** the system MUST reject the request with a field validation error

### Requirement: Assigned tournament contact administrator
Tournament create and update requests MAY set an assigned contact administrator. When non-null, the selected local user MUST exist, MUST NOT be deleted, and MUST have the current Auth0 tenant `admin` role through a direct role assignment. The service MUST validate membership server-side before saving. A known missing or non-admin account MUST be rejected as invalid input; Auth0 or configuration failures MUST fail safely as a service-unavailable error and MUST NOT be treated as non-membership.

#### Scenario: Valid administrator is assigned
- **WHEN** an administrator creates or updates a tournament with an existing local account that currently has the Auth0 `admin` role
- **THEN** the system MUST save the account ID as the assigned contact

#### Scenario: Invalid or deleted account is assigned
- **WHEN** an administrator selects a missing, deleted, or non-admin account
- **THEN** the system MUST reject the request and MUST NOT save the tournament change

#### Scenario: Auth0 membership cannot be checked
- **WHEN** Auth0 role membership lookup fails or Management API configuration is unavailable
- **THEN** the system MUST return a service-unavailable error
- **AND** MUST NOT save the tournament change or report the account as a known non-admin

#### Scenario: Contact is cleared or omitted
- **WHEN** an administrator explicitly submits an empty assigned-contact field
- **THEN** the system MUST clear the assigned contact
- **WHEN** an update request omits the assigned-contact field
- **THEN** the system MUST preserve the existing assignment

### Requirement: Privacy-safe public contact and prizes
Public tournament responses MUST include the three optional prize values and MUST expose the assigned contact only as an object containing public ID, username, and display name. They MUST NOT expose the raw assigned user ID as a separate field, email, Auth0 ID, social handles, or other private profile fields.

#### Scenario: Public tournament detail includes contact and prizes
- **WHEN** an anonymous client reads tournament details with prize metadata and an assigned contact
- **THEN** it MUST receive each configured prize value and the contact's ID, username, and display name
- **AND** MUST NOT receive other contact account fields

#### Scenario: Tournament has no contact or prizes
- **WHEN** an anonymous client reads a tournament with no configured metadata
- **THEN** each prize value and the contact summary MUST be null

### Requirement: Searchable administrator contact options
The API MUST expose an administrator-only search endpoint for current Auth0 admin accounts. Results MUST be bounded and MUST contain only public ID, username, and display name. Auth0 failures MUST be returned as service unavailable rather than as an empty result. Auth0 role lookup MUST use the existing Management API audience and an application with `read:users`, `read:roles`, and `read:role_members` scopes.

#### Scenario: Administrator searches contact options
- **WHEN** an authenticated administrator requests the contact picker with an optional search query and bounded page size
- **THEN** the API MUST return matching current admin accounts as a JSON array of public summaries

#### Scenario: Non-administrator searches contact options
- **WHEN** an authenticated non-administrator requests the contact picker
- **THEN** the API MUST deny the request
