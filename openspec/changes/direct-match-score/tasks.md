## Backend implementation

- [ ] Record the revised match-result-lifecycle contract in OpenSpec, removing the ended-confirmation command and the both-sides-confirmed prerequisite by explicit product decision.
- [ ] Record the submitting side's ended confirmation atomically with its score report and start the existing five-minute window from AwaitingEndedConfirmation, AwaitingScore or Reversed.
- [ ] Remove the confirm-ended endpoint, service and interface method, domain method and `canConfirmEnded` capability.
- [ ] Advertise score submission capability for those states while keeping BYE, terminal, duplicate, correction and format guards.
- [ ] Add domain, service and endpoint regression tests for direct first and matching reports, legacy both-ended submission, mismatch, duplicate, invalid, BYE, terminal, reversed and route removal.
- [ ] Run strict OpenSpec validation.
- [ ] Synchronize the live match-result-lifecycle spec and archive this change.
