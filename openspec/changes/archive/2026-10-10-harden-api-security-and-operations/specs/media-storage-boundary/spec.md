## ADDED Requirements

### Requirement: Media stores bounded lossy WebP images
Media MUST encode stored uploads as lossy WebP at quality 82 and MUST limit decode, frame and encode sizes to 8000x8000 pixels and 40 megapixels. An upload that exceeds these limits or cannot be decoded MUST be rejected with a validation error (HTTP 400) and MUST NOT leave a stored file.

#### Scenario: Valid image is uploaded
- **WHEN** a supported image within the size limits is uploaded
- **THEN** Media stores it as lossy WebP and returns its `images/<generated>.webp` reference

#### Scenario: Oversized or undecodable image is uploaded
- **WHEN** an upload exceeds the dimension limits or is not a decodable image
- **THEN** the API returns HTTP 400 and no image file remains in storage

## MODIFIED Requirements

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
