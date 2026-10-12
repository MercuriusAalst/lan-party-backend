# Tournament prizes and contact admin

## Why

Tournament details currently have no prize metadata or designated contact. Add optional first-, second-, and third-place prize text and allow administrators to select an Auth0 administrator as the tournament contact. Public tournament responses expose only the selected contact's public ID, username, and display name.

## What Changes

The back end remains authoritative: create and update requests validate the selected account against its current Auth0 `admin` role, while Auth0 failures fail safely. Administrators can search a bounded list of valid admin accounts for the picker.

## Scope

- Persist nullable prize text on tournaments and expose it through public and admin tournament detail responses.
- Accept the optional assigned admin on create and update, with omitted PATCH fields preserving existing values and explicit empty values clearing them.
- Validate assigned admins through the existing Identity/Auth0 boundary and expose a privacy-safe, admin-only admin picker endpoint.
- Migrate the three prize columns and cover persistence, clearing, authorization, privacy, and upstream failure behavior.
