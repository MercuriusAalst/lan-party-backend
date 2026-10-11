## Backend implementation

- [x] Add nullable first-, second-, and third-place prize fields and a safe contact-admin summary to tournament responses.
- [x] Accept optional contact-admin and prize fields on create/update; trim prize text, convert blanks to null, cap each value at 200 characters, and preserve omitted PATCH metadata.
- [x] Verify a selected contact is an existing non-deleted local user with the current Auth0 `admin` role; distinguish invalid membership from Auth0/configuration failures.
- [x] Add an admin-only, bounded Auth0-role-backed admin search endpoint returning only ID, username, and display name.
- [x] Add the three nullable prize columns and align the EF model snapshot.
- [x] Add focused tests for valid/invalid/missing admin selection, Auth0 failure, public privacy, prize validation/clear/persistence, and endpoint authorization.
- [x] Complete independent focused validation in the API and Tournament suites; then mark this task complete. The Auth0 role tests and strict OpenSpec validation have passed, and the live spec has been synced. Final evidence: API 197/197, Tournament 214/214, Platform 118/118, Identity 61/61, plus an independent binding review that executed the Kestrel form-binding path (2/2) and the real-service contact path (10/10).
