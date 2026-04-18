# Changelog

## [2.0.0](https://github.com/sondresjolyst/garge-api/compare/v1.7.2...v2.0.0) (2026-04-18)


### ⚠ BREAKING CHANGES

* rename roles ([#97](https://github.com/sondresjolyst/garge-api/issues/97))

### Features

* rename roles ([#97](https://github.com/sondresjolyst/garge-api/issues/97)) ([04de8f8](https://github.com/sondresjolyst/garge-api/commit/04de8f835e68516e82b5101f830719073388976b))

## [1.7.2](https://github.com/sondresjolyst/garge-api/compare/v1.7.1...v1.7.2) (2026-04-16)


### Bug Fixes

* replace string.Equals with ToUpper comparison in SwitchesController ([#94](https://github.com/sondresjolyst/garge-api/issues/94)) ([#95](https://github.com/sondresjolyst/garge-api/issues/95)) ([18d842f](https://github.com/sondresjolyst/garge-api/commit/18d842f1143f37ce4e7c4b1e20932ad8982f82f9))

## [1.7.1](https://github.com/sondresjolyst/garge-api/compare/v1.7.0...v1.7.1) (2026-04-16)


### Bug Fixes

* **switches:** whitelist SOCKET type in GET /api/switches ([#91](https://github.com/sondresjolyst/garge-api/issues/91)) ([f765bc5](https://github.com/sondresjolyst/garge-api/commit/f765bc50e8e0556653f077c582dc55a8a4aa6a00))

## [1.7.0](https://github.com/sondresjolyst/garge-api/compare/v1.6.0...v1.7.0) (2026-04-14)


### Features

* **automations:** timed auto-off support ([#87](https://github.com/sondresjolyst/garge-api/issues/87)) ([2168185](https://github.com/sondresjolyst/garge-api/commit/2168185671bc1c8cca04974cd98a815ecd44f694))
* electricity price conditions in automations and DB price storage ([#86](https://github.com/sondresjolyst/garge-api/issues/86)) ([8908e17](https://github.com/sondresjolyst/garge-api/commit/8908e17a3731d6f7e0b496a53568bd08172a57c0))


### Bug Fixes

* return 200 null instead of 404 when switch or battery health has no data ([#88](https://github.com/sondresjolyst/garge-api/issues/88)) ([2c9ccc9](https://github.com/sondresjolyst/garge-api/commit/2c9ccc9ee54cb280348aa2062cbf39ce23960da3))
* sanitize user input before logging in ElectricityController ([890c253](https://github.com/sondresjolyst/garge-api/commit/890c2535e9d3869c3372fccd0feb0cec752dbdf0))

## [1.6.0](https://github.com/sondresjolyst/garge-api/compare/v1.5.0...v1.6.0) (2026-04-11)


### Features

* UserSensors + UserSwitches DB-based access control ([#78](https://github.com/sondresjolyst/garge-api/issues/78)) ([3f27f70](https://github.com/sondresjolyst/garge-api/commit/3f27f7015a7f5ec8eb19bf4d93fffe62356a7ddb))

## [1.5.0](https://github.com/sondresjolyst/garge-api/compare/v1.4.0...v1.5.0) (2026-04-10)


### Features

* automation improvements and user price zone preference ([#72](https://github.com/sondresjolyst/garge-api/issues/72)) ([9e49823](https://github.com/sondresjolyst/garge-api/commit/9e49823e6a32e8f72c5946626b6dc55de35b2dd1))
* daily cleanup of revoked and expired refresh tokens ([81b8d15](https://github.com/sondresjolyst/garge-api/commit/81b8d152f5de90ba1517454f6b055b43b0f9a334))
* **groups:** add vehicle grouping API ([#71](https://github.com/sondresjolyst/garge-api/issues/71)) ([81b8d15](https://github.com/sondresjolyst/garge-api/commit/81b8d152f5de90ba1517454f6b055b43b0f9a334))
* **groups:** add vehicle grouping API (models, DTOs, controller, EF migration) ([81b8d15](https://github.com/sondresjolyst/garge-api/commit/81b8d152f5de90ba1517454f6b055b43b0f9a334))
* **sensors:** add DELETE /sensors/{id}/claim endpoint to unclaim sensor from user account ([81b8d15](https://github.com/sondresjolyst/garge-api/commit/81b8d152f5de90ba1517454f6b055b43b0f9a334))


### Bug Fixes

* resolve merge conflict in MergeStaleTypeSwitchRows migration ([81b8d15](https://github.com/sondresjolyst/garge-api/commit/81b8d152f5de90ba1517454f6b055b43b0f9a334))

## [1.4.0](https://github.com/sondresjolyst/garge-api/compare/v1.3.4...v1.4.0) (2026-04-09)


### Features

* daily cleanup of revoked and expired refresh tokens ([#69](https://github.com/sondresjolyst/garge-api/issues/69)) ([76d4551](https://github.com/sondresjolyst/garge-api/commit/76d4551c9406966ef774c55af868e54d01fc7e08))


### Bug Fixes

* add Designer.cs for ReattachBatteryHealthToVoltageSensor migration ([c6570af](https://github.com/sondresjolyst/garge-api/commit/c6570af9c61f0d3b527a2742ec5162b4154297b0))
* deduplicate SOCKET switches and add unique constraint on Name ([#68](https://github.com/sondresjolyst/garge-api/issues/68)) ([4116e76](https://github.com/sondresjolyst/garge-api/commit/4116e763b0185dc709ba8e1c9190510952ce0f42))
* migrate SwitchData from stale switch-typed rows to SOCKET rows ([09c4bb8](https://github.com/sondresjolyst/garge-api/commit/09c4bb86da0f9ffee81a1a9821ff1c783c0d23d3))

## [1.3.4](https://github.com/sondresjolyst/garge-api/compare/v1.3.3...v1.3.4) (2026-04-08)


### Bug Fixes

* adjust LastChargedAt timestamp calculation in CreateBatteryHealth method ([90c7822](https://github.com/sondresjolyst/garge-api/commit/90c782220bb25d50673283fe02e857bbc6e121c4))
* remove battery sensor rows, link battery health to voltage sensor ([ecb732f](https://github.com/sondresjolyst/garge-api/commit/ecb732fb9ffe7b60858e364f903ddb3f9569f56c))

## [1.3.3](https://github.com/sondresjolyst/garge-api/compare/v1.3.2...v1.3.3) (2026-04-06)


### Performance Improvements

* sensor data performance overhaul ([#56](https://github.com/sondresjolyst/garge-api/issues/56)) ([0634323](https://github.com/sondresjolyst/garge-api/commit/063432346eda64b7d5f7ebd99d9883e59673208e))

## [1.3.2](https://github.com/sondresjolyst/garge-api/compare/v1.3.1...v1.3.2) (2026-03-27)


### Bug Fixes

* allow non-admin users to access battery health via voltage sensor role ([#47](https://github.com/sondresjolyst/garge-api/issues/47)) ([4a22081](https://github.com/sondresjolyst/garge-api/commit/4a22081d1b33d53ce5881c0f66f4cc6be7e90863))

## [1.3.1](https://github.com/sondresjolyst/garge-api/compare/v1.3.0...v1.3.1) (2026-03-26)


### Bug Fixes

* move Sanitize helper before action methods to fix broken routing ([c49936d](https://github.com/sondresjolyst/garge-api/commit/c49936d47e7217ff4e3cadb5a96304c9ce1c85ba))
* move Sanitize helper before action methods to fix broken routing ([#46](https://github.com/sondresjolyst/garge-api/issues/46)) ([692a855](https://github.com/sondresjolyst/garge-api/commit/692a85539d1052599aeb9a147d3b0229ed716686))

## [1.3.0](https://github.com/sondresjolyst/garge-api/compare/v1.2.0...v1.3.0) (2026-03-26)


### Features

* add battery health tracking with LastChargedAt ([#42](https://github.com/sondresjolyst/garge-api/issues/42)) ([349f9bb](https://github.com/sondresjolyst/garge-api/commit/349f9bb6a7ce10b84571105917febd31488f18eb))

## [1.2.0](https://github.com/sondresjolyst/garge-api/compare/v1.1.0...v1.2.0) (2025-08-12)


### Features

* switch from sendgrid to brevo ([#30](https://github.com/sondresjolyst/garge-api/issues/30)) ([7ca2e17](https://github.com/sondresjolyst/garge-api/commit/7ca2e171dd1a7d8fa6023333a7d04be013d22351))

## [1.1.0](https://github.com/sondresjolyst/garge-api/compare/v1.0.0...v1.1.0) (2025-08-10)


### Features

* automation ([#26](https://github.com/sondresjolyst/garge-api/issues/26)) ([2bdf8d2](https://github.com/sondresjolyst/garge-api/commit/2bdf8d2a84db5065ae63a63b68b840d72d7dc995))
* emqx, new mqtt structure and logging. ([#25](https://github.com/sondresjolyst/garge-api/issues/25)) ([afcb4f2](https://github.com/sondresjolyst/garge-api/commit/afcb4f2282338f9a3ee4fa3840747ff38f9d3e32))

## 1.0.0 (2025-06-27)


### ⚠ BREAKING CHANGES

* create dto for endpoints. organise model folder. change endpoints to plural. ([#14](https://github.com/sondresjolyst/garge-api/issues/14))

### Features

* add email verification ([#11](https://github.com/sondresjolyst/garge-api/issues/11)) ([d10c2d1](https://github.com/sondresjolyst/garge-api/commit/d10c2d17c32d7f570194ba8bab9a78c2e7e65525))
* add switch support ([#13](https://github.com/sondresjolyst/garge-api/issues/13)) ([b068531](https://github.com/sondresjolyst/garge-api/commit/b068531c46b04c259f0a22c4e34b41836e0a9866))
* claim sensor ([#17](https://github.com/sondresjolyst/garge-api/issues/17)) ([da7b2d7](https://github.com/sondresjolyst/garge-api/commit/da7b2d76a3b91f865713373e0dceaab3b3e086f7))
* custom name ([#20](https://github.com/sondresjolyst/garge-api/issues/20)) ([997d651](https://github.com/sondresjolyst/garge-api/commit/997d6510f2baeabefa41a4495618bd1225d41500))
* electricity ([77bac81](https://github.com/sondresjolyst/garge-api/commit/77bac810bee12f73a64558292a4b08e39fb42681))
* login and register ([1f5bee6](https://github.com/sondresjolyst/garge-api/commit/1f5bee6e93de15d61228ab5de4c6d75d515f1a10))
* paging ([#22](https://github.com/sondresjolyst/garge-api/issues/22)) ([95f1cbe](https://github.com/sondresjolyst/garge-api/commit/95f1cbe61672b28c3ba3a8264fb65bdd2c2f8db1))
* refresh token ([#16](https://github.com/sondresjolyst/garge-api/issues/16)) ([cedf629](https://github.com/sondresjolyst/garge-api/commit/cedf629fdeee6792ddb2e8f9b3d3cb97a188e242))
* reset password ([#19](https://github.com/sondresjolyst/garge-api/issues/19)) ([5ccc60a](https://github.com/sondresjolyst/garge-api/commit/5ccc60af14894907c0acdee7524a784832bdb570))
* sensors ([d59d4b8](https://github.com/sondresjolyst/garge-api/commit/d59d4b8d4603b9e63afed16b3abed3d175fdb32e))
* user specific custom sensor names ([#21](https://github.com/sondresjolyst/garge-api/issues/21)) ([0e6c6b4](https://github.com/sondresjolyst/garge-api/commit/0e6c6b48e7e11ef1645ff806b6d5b555007393fe))
* user, roles, authentication ([f95ca2a](https://github.com/sondresjolyst/garge-api/commit/f95ca2a8e84c421af3e9a9a4fcb0673c79443252))
* Userprofile ([465f090](https://github.com/sondresjolyst/garge-api/commit/465f090bc69a58d1ef40c651f9489f2fa9c26dc8))


### Code Refactoring

* create dto for endpoints. organise model folder. change endpoints to plural. ([#14](https://github.com/sondresjolyst/garge-api/issues/14)) ([f5ab62a](https://github.com/sondresjolyst/garge-api/commit/f5ab62a7f14e29f73be58171995f129690c58741))
