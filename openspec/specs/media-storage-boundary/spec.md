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
Media MUST encode stored uploads as lossy WebP at quality 82 and MUST limit decode, frame and encode sizes to 8000x8000 pixels and 40 megapixels. An upload that exceeds these limits or cannot be decoded MUST be rejected with a validation error (HTTP 400) and MUST NOT leave a stored file.

#### Scenario: Valid image is uploaded
- **WHEN** a supported image within the size limits is uploaded
- **THEN** Media stores it as lossy WebP and returns its `images/<generated>.webp` reference

#### Scenario: Oversized or undecodable image is uploaded
- **WHEN** an upload exceeds the dimension limits or is not a decodable image
- **THEN** the API returns HTTP 400 and no image file remains in storage

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
The API host MUST derive its multipart image request limits from `FileStorage:MaxFileSizeInMB`. It MUST configure Kestrel to reject a total request body larger than the configured file size plus a 65,536-byte multipart envelope, and it MUST configure the multipart section-body limit to the configured file size. The Kestrel total-body limit applies to every request body received by the host, not only multipart requests; the section limit MUST NOT be treated as a total request limit. The host MUST apply the total request limit before Tournament, Sponsor, and Team upload handlers or their services execute, returning HTTP 413 for an over-limit request. Media MUST retain its existing file-size validation after binding.

#### Scenario: Exact configured file boundary is uploaded
- **WHEN** a Tournament, Sponsor, or Team multipart upload contains one supported file whose length equals `FileStorage:MaxFileSizeInMB` and current scalar form fields fit within the envelope
- **THEN** the request reaches the existing handler and service behavior

#### Scenario: Multipart request exceeds the envelope
- **WHEN** a multipart upload's total request body exceeds the configured file size plus 65,536 bytes
- **THEN** Kestrel returns HTTP 413 before the upload handler or service executes

#### Scenario: Direct Media caller exceeds file boundary
- **WHEN** a caller invokes Media with image metadata whose file length exceeds `FileStorage:MaxFileSizeInMB`
- **THEN** Media rejects the upload using its existing validation behavior

#### Scenario: Current non-media request body is within host cap
- **WHEN** the API receives a current non-media JSON request body smaller than the configured Kestrel limit
- **THEN** it retains its existing endpoint behavior

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
