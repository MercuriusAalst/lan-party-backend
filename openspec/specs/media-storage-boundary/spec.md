# Media Storage Boundary Specification

## Purpose

Define Media ownership of validated image storage and the contract business modules use to create
and delete image assets.
## Requirements
### Requirement: Media owns validated image storage
The Media module MUST be the sole implementation owner of image upload validation, WebP encoding,
configured local storage, generated image references, and image deletion. Business modules MUST
use `IMediaModule` from Media.Contracts rather than reference a Media implementation service.

#### Scenario: Business module stores an image through the contract
- **WHEN** Teams, Tournament, or Sponsorship supplies an image stream and its metadata to
  `IMediaModule`
- **THEN** Media validates and stores the image and returns the relative image reference for the
  owning module to persist

#### Scenario: Host composition provides the Media implementation
- **WHEN** the API host registers application services
- **THEN** `IMediaModule` resolves to the Media module implementation without using a host adapter

### Requirement: Media preserves image upload validation
Media MUST reject a missing, empty, oversized, or unsupported image upload using the existing
validation behavior. Media MUST accept JPEG, PNG, WebP, and GIF content types within the configured
maximum size.

#### Scenario: Unsupported content type is supplied
- **WHEN** an upload declares a content type other than JPEG, PNG, WebP, or GIF
- **THEN** Media rejects the upload before persisting an image

#### Scenario: Valid image metadata is supplied
- **WHEN** an upload has a supported content type and a non-empty length no greater than the
  configured maximum size
- **THEN** Media accepts the upload for encoding and storage

### Requirement: Media stores bounded lossy WebP images
Media MUST require every production image upload to name its purpose explicitly. It MUST apply a
centralized maximum bounding box for that purpose before encoding, MUST preserve the source aspect
ratio, and MUST NOT crop, stretch or upscale. Media MUST write one WebP output per upload and
MUST continue to enforce the existing 8000x8000 pixel, 40 megapixel and configured 5 MiB input
limits. Invalid, oversized or undecodable images MUST return the existing validation behavior and
MUST NOT leave a stored file.

Tournament banners MUST be bounded to 2560 by 1440 and encoded as lossy WebP at quality 78.
Profile pictures MUST be bounded to 512 by 512 and encoded as lossy WebP at quality 82. Sponsor
and Team logos MUST be bounded to 1024 by 1024 and encoded as lossless WebP.

#### Scenario: Large tournament banner is uploaded
- **WHEN** an allowed image larger than 2560 by 1440 is uploaded as a tournament banner
- **THEN** the stored image fits within 2560 by 1440 with its aspect ratio preserved
- **AND** it is encoded once as quality-78 lossy WebP

#### Scenario: Portrait or landscape image is bounded
- **WHEN** an allowed portrait or landscape image exceeds either dimension of its purpose's bounding box
- **THEN** both output dimensions fit within the bounding box and the shorter dimension is reduced in proportion

#### Scenario: Small image is uploaded
- **WHEN** an allowed image already fits within its purpose's bounding box
- **THEN** Media does not upscale or crop it

#### Scenario: Logo and profile image policies are distinct
- **WHEN** a Sponsor or Team logo and a profile picture are uploaded
- **THEN** each uses its explicit 1024-by-1024 or 512-by-512 maximum dimension policy
- **AND** the caller cannot select an undeclared purpose or omit the purpose

#### Scenario: Existing upload safeguards are exercised
- **WHEN** an upload uses an unsupported type, exceeds the configured byte limit, exceeds the decode bounds or cannot be decoded
- **THEN** Media retains its existing validation status and cleanup behavior

#### Scenario: Transparency, orientation and GIF input are preserved as compatible
- **WHEN** a transparent or oriented image, or an allowed GIF, is uploaded
- **THEN** the stored WebP retains the applicable alpha or orientation and follows the existing GIF conversion behavior

### Requirement: Media returns and deletes safe image references
Media MUST return the existing `images/<generated>.webp` relative-reference format and MUST NOT
derive a storage key from untrusted client file names. Image deletion MUST be idempotent, MUST
not delete files outside the configured storage directory, and MUST treat blank, external,
default/static, traversal, non-image, and arbitrary references as non-owned no-ops.

#### Scenario: Stored image reference is deleted twice
- **WHEN** deletion is requested twice for the same valid image reference
- **THEN** both requests complete successfully and no file outside Media storage is affected

#### Scenario: Unsafe or non-owned image reference is supplied for deletion
- **WHEN** deletion is requested with a blank, external, default/static, traversal, non-image, or arbitrary reference
- **THEN** Media completes without deleting a file

### Requirement: HTTP image serving remains host infrastructure
The API host MUST retain Imageflow middleware configuration for `/images` using the configured
storage location, served anonymously ahead of the security pipeline with the same 8000x8000 /
40 megapixel decode, frame and encode limits as Media. The Media module MUST NOT own HTTP
middleware registration.

#### Scenario: Stored image is requested over HTTP
- **WHEN** a client requests an image under `/images`
- **THEN** the host Imageflow middleware serves it using the configured image storage location

#### Scenario: Anonymous image request
- **WHEN** an anonymous client requests an image under `/images`
- **THEN** the host serves it without requiring authentication

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

### Requirement: Business mutations coordinate image lifecycle around commit
Teams, Tournament, and Sponsorship MUST compensate a newly stored image when its owning mutation
fails or is cancelled before commit and MUST retire a replaced or deleted owned image only after
the related database and durable-business-event commit succeeds. Cleanup MUST use a non-cancelled
token, MUST be best effort, and MUST NOT replace the original pre-commit failure or turn a committed
mutation into a failure response.

#### Scenario: Owning mutation fails after image storage
- **WHEN** Media returns a newly stored image and the following mutation or commit fails or is cancelled
- **THEN** the owning module attempts to delete only that new reference with a non-cancelled token
- **AND** rethrows the original failure even when compensation also fails

#### Scenario: Replacement commits successfully
- **WHEN** a Tournament image, Sponsor logo, or Team logo replacement commits with a different owned reference
- **THEN** the owning module attempts to delete the previous unreferenced owned image after commit

#### Scenario: Post-commit deletion fails
- **WHEN** the database mutation commits but physical image deletion fails
- **THEN** the mutation remains successful
- **AND** the cleanup failure is logged for manual remediation

#### Scenario: Reference is blank or unchanged
- **WHEN** the candidate reference is blank or ordinally equal to the new current reference
- **THEN** the owning module does not request its deletion

### Requirement: Sponsor logo variants use the sponsor media policy

Sponsorship MUST store and validate the optional light sponsor logo through the
same `IMediaModule` boundary and `MediaPurpose.SponsorLogo` purpose as the
required default logo. The existing supported types, size limit, filename and
content-type validation, multipart request limit, replacement cleanup, and
compensation behavior MUST apply equally to both files. Saving one sponsor logo
MUST NOT bypass Media validation or write outside Media.

#### Scenario: Invalid light logo is rejected

- **WHEN** a sponsor create or update supplies a light logo that violates the
  existing sponsor media policy
- **THEN** Media MUST reject it using the existing validation outcome
- **AND** Sponsorship MUST NOT persist the sponsor mutation

#### Scenario: Two-image mutation fails

- **WHEN** either requested sponsor logo is stored but the following sponsor
  mutation or durable event does not commit
- **THEN** Sponsorship MUST compensate every newly stored logo from that request
  through the Media contract
