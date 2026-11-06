## MODIFIED Requirements

### Requirement: Host bounds multipart image uploads before binding
The API host MUST derive its multipart image request limits from `FileStorage:MaxFileSizeInMB`. It MUST retain a host-wide Kestrel total-body limit of the configured file size plus a 65,536-byte multipart envelope. Sponsor create and update requests MAY carry two logo files and MUST receive a Kestrel total-body allowance of twice the configured file size plus the same envelope, applied before authentication, form binding, or service execution. No other endpoint may receive the two-file allowance. The multipart body limit MUST accommodate the sponsor two-file allowance while Kestrel's host default continues to bound every non-sponsor request. Media MUST retain its existing per-file validation after binding. Tournament, Team, Sponsor, and non-media requests that exceed their applicable total-body limit MUST receive HTTP 413 before their handlers or services execute.

#### Scenario: Two valid sponsor files exceed the old single-file total
- **WHEN** Sponsor create or update receives two supported files whose individual lengths are within `FileStorage:MaxFileSizeInMB` but whose combined multipart body exceeds one file plus 65,536 bytes
- **THEN** the request MUST reach the sponsor service and both files MUST undergo Media per-file validation

#### Scenario: Sponsor file exceeds the per-file limit
- **WHEN** a sponsor upload contains an individual file longer than `FileStorage:MaxFileSizeInMB` while its total multipart body remains within the sponsor two-file allowance
- **THEN** Media MUST reject that file using its existing validation behavior

#### Scenario: Non-sponsor multipart request exceeds the single-file total
- **WHEN** a Tournament or Team multipart upload's total request body exceeds the configured file size plus 65,536 bytes
- **THEN** Kestrel MUST return HTTP 413 before the upload handler or service executes

#### Scenario: Non-media request exceeds the host default
- **WHEN** the API receives a non-sponsor request body larger than the host-wide configured file size plus 65,536 bytes
- **THEN** Kestrel MUST return HTTP 413 before the endpoint handler executes

#### Scenario: Exact configured file boundary is uploaded
- **WHEN** a Tournament, Sponsor, or Team multipart upload contains one supported file whose length equals `FileStorage:MaxFileSizeInMB` and current scalar form fields fit within the envelope
- **THEN** the request retains the existing handler and service behavior
