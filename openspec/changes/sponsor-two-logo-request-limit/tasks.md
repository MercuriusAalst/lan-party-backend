## 1. Request Limits

- [ ] 1.1 Derive the two-logo sponsor aggregate limit from the configured per-file limit.
- [ ] 1.2 Apply the aggregate override only to sponsor create and update endpoints.
- [ ] 1.3 Preserve the host-wide single-file cap and per-file Media validation.

## 2. Verification

- [ ] 2.1 Test two valid sponsor files whose combined body exceeds the old total cap.
- [ ] 2.2 Test an individually oversized sponsor file remains rejected.
- [ ] 2.3 Test non-sponsor request-body protection remains unchanged.
- [ ] 2.4 Run targeted API, Sponsorship, and Media tests.
- [ ] 2.5 Strict-validate, archive, and validate the live specs.
