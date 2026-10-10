## Backend implementation

- [x] Remove the `PUT /v1/lan/matches/{id}` endpoint, its service method and its request DTO.
- [x] Test that the route is absent from the endpoint table and that the OpenAPI document no longer exposes `PUT` on `/v1/lan/matches/{id}`.
- [ ] Remove the unused frontend client binding for the retired route.
- [ ] Synchronize the live specs and archive this change after review.
