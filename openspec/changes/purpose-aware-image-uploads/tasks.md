## 1. Specification

- [x] 1.1 Define the required-purpose contract and bounded output policies in the media storage specification.
- [x] 1.2 Validate the OpenSpec change strictly before implementation.

## 2. Purpose-Aware Encoding

- [ ] 2.1 Add the internal image-purpose contract and centralized immutable policies.
- [ ] 2.2 Apply an aspect-preserving maximum bounding box before one WebP encode, without crop or upscale.
- [ ] 2.3 Preserve supported inputs, security bounds, error mapping, cancellation and failed-file cleanup.
- [ ] 2.4 Assign explicit purposes in Tournament, Sponsorship, Teams and Identity uploads.

## 3. Regression Coverage

- [ ] 3.1 Cover landscape, portrait, small and non-upscaled dimensions for every policy.
- [ ] 3.2 Cover orientation and transparency preservation, GIF compatibility and decode-limit cleanup.
- [ ] 3.3 Cover each production consumer's purpose and the existing upload/lifecycle behavior.

## 4. Evidence And Rollout

- [ ] 4.1 Run a reproducible benchmark across banner-like, photo, text, alpha and small images.
- [ ] 4.2 Inspect output quality and select the single production logo encoder from measured evidence.
- [ ] 4.3 Document benchmark results, historical encoding evidence, existing-asset dry-run procedure and frontend follow-up.
- [ ] 4.4 Reconcile this task list, validate strictly and archive the completed change.
