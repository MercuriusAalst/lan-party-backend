# Changelog

## [0.9.0](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.8.2...backend-v0.9.0) (2026-10-24)


### Features

* Optimize Uploads With Purpose-Specific Image Policies ([#166](https://github.com/MercuriusAalst/lan-party-backend/issues/166)) ([3ffa468](https://github.com/MercuriusAalst/lan-party-backend/commit/3ffa468bb84601994250d081fcc4e4981ce78391))

## [0.8.2](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.8.1...backend-v0.8.2) (2026-10-23)


### Bug Fixes

* Complete Auth0 Account Deletion And Fence Reused Identities ([#163](https://github.com/MercuriusAalst/lan-party-backend/issues/163)) ([b5bd5b4](https://github.com/MercuriusAalst/lan-party-backend/commit/b5bd5b4d9b614445f492037fbe5fee78e4792783)), closes [#162](https://github.com/MercuriusAalst/lan-party-backend/issues/162)

## [0.8.1](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.8.0...backend-v0.8.1) (2026-10-22)


### Bug Fixes

* Remove Deleted Users From Teams ([#160](https://github.com/MercuriusAalst/lan-party-backend/issues/160)) ([aa965e9](https://github.com/MercuriusAalst/lan-party-backend/commit/aa965e9fc485eeef57e9f85f61462479aae94b56)), closes [#159](https://github.com/MercuriusAalst/lan-party-backend/issues/159)

## [0.8.0](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.7.0...backend-v0.8.0) (2026-10-19)


### Features

* Add User Profile Picture Management ([#155](https://github.com/MercuriusAalst/lan-party-backend/issues/155)) ([f1c85be](https://github.com/MercuriusAalst/lan-party-backend/commit/f1c85beee827f274bc022e967a3e143410544ce9))
* Require Assigned Admin And Route Match Resolution To Them ([#153](https://github.com/MercuriusAalst/lan-party-backend/issues/153)) ([ca7b6cc](https://github.com/MercuriusAalst/lan-party-backend/commit/ca7b6cc57e9376210c0132e8ba1d35602e801ed0))


### Bug Fixes

* Lock Started Elimination Participants ([#158](https://github.com/MercuriusAalst/lan-party-backend/issues/158)) ([4f11b78](https://github.com/MercuriusAalst/lan-party-backend/commit/4f11b784b9baaef5cec3a1be049a43ee2eb2b378))
* Remove Empty Leaderboard Participants ([#157](https://github.com/MercuriusAalst/lan-party-backend/issues/157)) ([d9615a6](https://github.com/MercuriusAalst/lan-party-backend/commit/d9615a66d6892fd9cdb1ed03c4984bdfb73cfb72))

## [0.7.0](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.6.0...backend-v0.7.0) (2026-10-15)


### Features

* Add Optional Tournament Prizes and Assigned Contact Admin ([#142](https://github.com/MercuriusAalst/lan-party-backend/issues/142)) ([980530e](https://github.com/MercuriusAalst/lan-party-backend/commit/980530eb20c12c311078f9dde2da5e3a40663c9f)), closes [#86](https://github.com/MercuriusAalst/lan-party-backend/issues/86)
* Persist And Expose Featured Homepage Tournaments ([#145](https://github.com/MercuriusAalst/lan-party-backend/issues/145)) ([e7121a2](https://github.com/MercuriusAalst/lan-party-backend/commit/e7121a2901ef4e8872983b78045f226412dc6237))


### Bug Fixes

* Allow Direct Match Score Submission ([#151](https://github.com/MercuriusAalst/lan-party-backend/issues/151)) ([7663e7f](https://github.com/MercuriusAalst/lan-party-backend/commit/7663e7f6b6ff4a5abd0d65442ae494f7d693f18c))
* Correct Discovery search index usage and make rebuilds cluster-safe ([662170e](https://github.com/MercuriusAalst/lan-party-backend/commit/662170e9602abcfe554bfd134758701a21e06800)), closes [#140](https://github.com/MercuriusAalst/lan-party-backend/issues/140)
* Correct Discovery search index usage and make rebuilds cluster-safe ([#143](https://github.com/MercuriusAalst/lan-party-backend/issues/143)) ([662170e](https://github.com/MercuriusAalst/lan-party-backend/commit/662170e9602abcfe554bfd134758701a21e06800))

## [0.6.0](https://github.com/MercuriusAalst/lan-party-backend/compare/backend-v0.5.2...backend-v0.6.0) (2026-10-06)


### Features

* add leaderboard tournament type for score and time competitions ([#129](https://github.com/MercuriusAalst/lan-party-backend/issues/129)) ([93de1d5](https://github.com/MercuriusAalst/lan-party-backend/commit/93de1d5066fc371749ff61e5019c53e5831c4521)), closes [#49](https://github.com/MercuriusAalst/lan-party-backend/issues/49)
* add roster decline and selection notifications ([#128](https://github.com/MercuriusAalst/lan-party-backend/issues/128)) ([1226bef](https://github.com/MercuriusAalst/lan-party-backend/commit/1226befc780252ddbb5aeae547883b56fe0ed0df))
* Notify Removed Team Members Over Realtime ([#139](https://github.com/MercuriusAalst/lan-party-backend/issues/139)) ([0ad65a7](https://github.com/MercuriusAalst/lan-party-backend/commit/0ad65a71a8aab340b92795d926c3565de0874332))


### Bug Fixes

* display linked LAN users by public username in leaderboards ([#135](https://github.com/MercuriusAalst/lan-party-backend/issues/135)) ([0a832be](https://github.com/MercuriusAalst/lan-party-backend/commit/0a832be09e40ce1debb7e30804e3e2e4122c5f88))
* over eager deletion of pipelines: restore ([#126](https://github.com/MercuriusAalst/lan-party-backend/issues/126)) ([26da0e7](https://github.com/MercuriusAalst/lan-party-backend/commit/26da0e7bedf831537d023a039f1506fa2ccd9e78))
* restrict public user data to usernames and match opponents ([#136](https://github.com/MercuriusAalst/lan-party-backend/issues/136)) ([d6618f9](https://github.com/MercuriusAalst/lan-party-backend/commit/d6618f95959cc51d83abf2017e90e4c71bef47be))

## [0.5.2](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.5.1...backend-v0.5.2) (2025-11-14)


### Bug Fixes

* change image to azure linux ([#45](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/45)) ([06a961e](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/06a961e3d51979ee1f242e8c26642dba5134ce0f))

## [0.5.1](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.5.0...backend-v0.5.1) (2025-11-10)


### Bug Fixes

* clean up unused references ([#43](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/43)) ([e3eee1c](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/e3eee1c91f6f9583029692116b2c9dfff492462e))

## [0.5.0](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.4.0...backend-v0.5.0) (2025-10-04)


### Features

* image support ([#31](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/31)) ([edf9f78](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/edf9f788ced2cb003c055073247b8dfa1c7347a8))
* RegisterFormUrl on game ([#36](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/36)) ([fe4407b](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/fe4407ba2c0fd1b77b0218408574efe0cee3cec9))
* Sponsors ([#34](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/34)) ([0b2e621](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/0b2e62199ff3e33c5416470a0631d258cd0e64fc))
* various features and tweaks ([#39](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/39)) ([cabdef9](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/cabdef9c7ea55b581fc95cfdaadbb9f05cdf64cc))

## [0.4.0](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.3.0...backend-v0.4.0) (2025-08-13)


### Features

* authentication and authorization ([#26](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/26)) ([e4702f3](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/e4702f37698fe45e18aee9d26e0e492a1c22c408))

## [0.3.0](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.2.0...backend-v0.3.0) (2025-08-07)


### Features

* Bugfix double elimination bracket generation ([#24](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/24)) ([a300ab4](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/a300ab495f3693dadfdc8fee7c5aa4d54a51aa5f))

## [0.2.0](https://github.com/mercurius-aalst/mercurius-aalst-back-end/compare/backend-v0.1.0...backend-v0.2.0) (2025-06-10)


### Features

* implement Dockerfile for backend ([#15](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/15)) ([2a1a2f1](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/2a1a2f1d5595e38945f07a116d5b00ff83f98674))
* initialization (and LAN functionality) of backend API ([#1](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/1)) ([c752d9c](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/c752d9c00e71a0fccb878a93d9be4794c10254a6))
* Invite players to team ([#11](https://github.com/mercurius-aalst/mercurius-aalst-back-end/issues/11)) ([cf41718](https://github.com/mercurius-aalst/mercurius-aalst-back-end/commit/cf41718242e972c297f251c2b8a94d990898352b))
