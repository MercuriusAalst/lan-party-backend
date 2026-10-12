# Auth0 admin role lookup

Tournament contact validation and the administrator picker use the Auth0 tenant role named exactly `admin`. The Management API application configured through `Auth0:ManagementClientId` and `Auth0:ManagementClientSecret` must be authorized for `read:users`, `read:roles`, and `read:role_members`. `Auth0:ManagementAudience` must target the tenant Management API, in the form `https://{tenant-domain}/api/v2/`.

The lookup follows the existing core tenant role convention and checks direct role assignments. A user who receives administrator access only through a group assignment is not included by these endpoints. Auth0/configuration failures return HTTP 503 so the API does not treat an unavailable lookup as a known non-admin.
