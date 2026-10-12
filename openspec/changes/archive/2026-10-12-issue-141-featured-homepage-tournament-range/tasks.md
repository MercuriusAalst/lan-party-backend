## 1. Selection contract and persistence

- [x] 1.1 Accept one through four distinct eligible IDs and keep invalid replacements atomic.
- [x] 1.2 Backfill saved reads only up to the saved selection count; keep unsaved reads capped at four.
- [x] 1.3 Replace the database cardinality check with a one-to-four constraint and verify transactional downgrade behavior.

## 2. Regression coverage and validation

- [x] 2.1 Cover accepted one-, three-, and four-item selections; reject empty, five-item, duplicate, missing, and canceled selections.
- [x] 2.2 Cover saved read count/order and fallback to the saved count, including primary-card ordering.
- [x] 2.3 Cover database constraint boundaries and migration preservation/rollback behavior.
- [x] 2.4 Strictly validate the change and updated live spec; run focused backend tests and migration checks.
