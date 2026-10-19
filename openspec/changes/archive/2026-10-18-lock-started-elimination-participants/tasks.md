## Backend

- [x] Reject admin individual registration removal from single and double elimination tournaments that are not Scheduled.
- [x] Reject admin team registration removal from single and double elimination tournaments that are not Scheduled, preserving roster cleanup.
- [x] Increment the persisted tournament revision for scheduled elimination removals so concurrent starts and removals conflict instead of saving an inconsistent bracket.
- [x] Add regression tests for both removal kinds and bracket types, including scheduled removal and non-elimination behavior.
- [x] Strictly validate OpenSpec and run focused registration tests.
