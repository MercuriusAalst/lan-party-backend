## 1. Contract And Persistence

- [ ] 1.1 Add the nullable sponsor `LightLogoUrl` domain property and additive EF migration.
- [ ] 1.2 Add nullable `LightLogoUrl` to sponsor summaries, API DTOs, and lifecycle event contracts.

## 2. Sponsor API And Media Lifecycle

- [ ] 2.1 Bind optional multipart `LightLogo` and update-only `RemoveLightLogo` fields.
- [ ] 2.2 Reject simultaneous light-logo replacement and removal before media saves.
- [ ] 2.3 Save, retain, replace, and remove the optional variant while compensating failed mutations.
- [ ] 2.4 Clean up both logo files after successful replacement or sponsor deletion.

## 3. Read And Event Projections

- [ ] 3.1 Expose `lightLogoUrl` from sponsor list, detail, create, and update reads.
- [ ] 3.2 Expose `sponsorLightLogoUrl` in tournament detail and featured placement reads.
- [ ] 3.3 Preserve Discovery compatibility with legacy sponsor events and rebuild its default-image projection consistently.

## 4. Verification And Archive

- [ ] 4.1 Add focused tests for migration, CRUD semantics, authorization, response shapes, events, and media cleanup success/failure.
- [ ] 4.2 Run the targeted Sponsorship, Tournament, Discovery, and API test checks.
- [ ] 4.3 Synchronize this task list and strict-validate the OpenSpec change.
- [ ] 4.4 Archive the completed change and verify the live specs contain the new behavior.
