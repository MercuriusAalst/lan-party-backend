## MODIFIED Requirements

### Requirement: Assigned tournament contact administrator

Tournament create requests MUST set an assigned contact administrator, and tournament update requests MUST NOT clear it. The selected local user MUST exist, MUST NOT be deleted, and MUST have the current Auth0 tenant `admin` role through a direct role assignment. The service MUST validate membership server-side before saving. A missing assignment, a malformed id, or a known missing or non-admin account MUST be rejected as invalid input; Auth0 or configuration failures MUST fail safely as a service-unavailable error and MUST NOT be treated as non-membership. Tournaments created before this requirement MAY still have no assigned administrator; the persisted value MUST remain nullable, MUST NOT be backfilled, and the next update of such a tournament MUST supply an assigned administrator.

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

#### Scenario: Create omits the contact
- **WHEN** an administrator creates a tournament without an assigned-contact value or with a blank one
- **THEN** the system MUST reject the request with a field validation error and MUST NOT save the tournament

#### Scenario: Contact cannot be cleared
- **WHEN** an administrator explicitly submits an empty assigned-contact field on update
- **THEN** the system MUST reject the request and MUST preserve the existing assignment
- **WHEN** an update request omits the assigned-contact field for a tournament that has one
- **THEN** the system MUST preserve the existing assignment

#### Scenario: Legacy tournament without a contact
- **WHEN** an update request omits the assigned-contact field for a tournament that has none
- **THEN** the system MUST reject the request until an assigned administrator is supplied
