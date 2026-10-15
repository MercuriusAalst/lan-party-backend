# Allow direct match score submission

Players currently confirm that a match ended, wait for their opponent to confirm the same, and only then submit a score, which both sides must do before the result is final. Participants asked for the short path: enter the score and submit it. The product decision is to delete the confirmation flow rather than keep it alongside the new one.

This change lets an eligible participant or team captain submit a score directly through the existing score route. Submitting a score records that side's ended confirmation and starts the existing five-minute confirmation window, so neither side has to confirm the match ended first. The lifecycle protections stay: matching reports complete the result, differing reports open the existing correction window, a single report is auto-accepted at the deadline, unresolved corrections still require an administrator, and the bracket advances transactionally.

The confirm-ended command, its route and its action-state capability flag are removed, because nothing legitimately calls them once score submission records the ending. Confirmed-ended timestamps and lifecycle enum values stay persisted and projected, so no migration is needed.

## Scope

- Accept a first score report in AwaitingEndedConfirmation, AwaitingScore or Reversed and record that actor's ended confirmation with it.
- Remove the confirm-ended endpoint, service method, domain method and `canConfirmEnded` capability, updating every caller, test and spec.
- Keep the persisted five-minute confirmation window, correction window, duplicate-report guard, format validation, BYE/terminal guards and transactional advancement.
- Require a coordinated back-end and front-end deployment because an old front-end calling the removed route will fail.
