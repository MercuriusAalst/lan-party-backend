## 1. Request Limits

- [x] 1.1 Derive the two-logo sponsor aggregate limit from the configured per-file limit.
- [x] 1.2 Apply the aggregate override only to sponsor create and update endpoints.
- [x] 1.3 Preserve the host-wide single-file cap and per-file Media validation.

## 2. Verification

- [x] 2.1 Test two valid sponsor files whose combined body exceeds the old total cap.
- [x] 2.2 Test an individually oversized sponsor file remains rejected.
- [x] 2.3 Test non-sponsor request-body protection remains unchanged.
- [x] 2.4 Run targeted API, Sponsorship, and Media tests.
- [x] 2.5 Strict-validate, archive, and validate the live specs.
