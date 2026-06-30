# ADBControl Implementation Gaps

This document tracks implementation gaps discovered from README, ADRs, specs, protocol docs, and source scans. An item is complete only when the behavior is implemented and verified; scaffolding, placeholders, or test-only mocks do not count as completion.

## Status Legend

- `passed`: implemented and directly verified.
- `not-passed`: implemented partially or failing validation.
- `not-verified`: blocked by missing environment, external dependency, or no direct validation yet.

## Frozen Acceptance Checklist

1. Core session to device registry / IPC synchronization
   - Status: `passed`
   - Completion standard: `device.list`, `device.getCapabilities`, `device.getPermissionState`, and `device.invoke` can use devices produced from `CompanionSessionManager` state rather than only static sample registry data.
   - Validation method: Rust unit tests covering `hello -> capabilityList -> permissionState -> IPC query/invoke`.
   - Evidence: `protocol::tests::ipc_device_methods_can_use_session_manager_devices`; `cargo test --workspace` passed with 55 tests.

2. Core real QUIC command router boundary
   - Status: `passed`
   - Completion standard: a non in-memory router can send `commandRequest` envelopes through a device-bound transport boundary and surface send/session failures as `AppError`.
   - Validation method: Rust unit tests with a fake transport plus `cargo check -p adbcontrol-core --features quinn-transport`.
   - Evidence: `companion::router::tests::transport_router_sends_command_request_to_transport_boundary`, `companion::router::tests::transport_router_returns_transport_error`, and `cargo check -p adbcontrol-core --features quinn-transport`.

3. Core trusted ingress, trust store, and media store loop
   - Status: `passed`
   - Completion standard: non-hello messages require trusted certificate fingerprints; trusted media chunks are stored and ACKed; untrusted chunks do not write files.
   - Validation method: Rust unit tests in `trusted_ingress` and `ingress`.
   - Evidence: `companion::trusted_ingress::tests::trusted_media_chunk_is_stored_through_inner_ingress`, `companion::trusted_ingress::tests::untrusted_media_chunk_is_rejected_before_storage`, and ingress media store tests.

4. Core QUIC listener and TLS identity runtime entry
   - Status: `not-passed`
   - Completion standard: Core can load/generate identity, bind a Quinn listener, and route incoming bytes into trusted ingress/listener boundaries.
   - Validation method: Rust unit/integration tests plus `cargo check -p adbcontrol-core --features quinn-transport`.

5. Android native QUIC engine JNI/native implementation
   - Status: `not-passed`
   - Completion standard: Android `JniNativeQuicEngine` can connect to `quic://`, send envelopes, and close using a real ABI-compatible native QUIC implementation.
   - Validation method: Android Gradle/CMake build and JNI/native tests.

6. Android screen/camera real-time chunk drain to QUIC media sink
   - Status: `not-passed`
   - Completion standard: screen and camera encoder output is drained to `MediaStreamSink` as chunks, not only written to sandbox files.
   - Validation method: Android build plus handler or instrumentation tests.

7. Android audio real-time QUIC media stream
   - Status: `not-verified`
   - Completion standard: audio recording emits chunks through `QuicMediaStreamSink` with start/stop lifecycle protection.
   - Validation method: Android build plus handler or instrumentation tests.

8. Device session recovery
   - Status: `not-passed`
   - Completion standard: Core identity/trust survives restart and Android/Core reconnect can restore the device through a controlled handshaking/ready flow.
   - Validation method: Rust identity/trust/session recovery tests and Android pairing state tests.

9. Windows Named Pipe and Unix Domain Socket IPC transports
   - Status: `not-passed`
   - Completion standard: stdio remains available and additional IPC transports are isolated behind transport modules without changing business logic.
   - Validation method: platform-gated unit tests or compile checks.

10. Android Gradle Wrapper / repeatable Android CI build
    - Status: `not-passed`
    - Completion standard: the companion app can be built without relying on a preinstalled local Gradle command.
    - Validation method: `./gradlew :app:assembleDebug` or CI workflow.
    - Evidence: local validation found no `gradlew` in `android/companion-app`; `gradle -v` failed because `gradle` is not installed.

11. Documentation/spec status consistency
    - Status: `passed`
    - Completion standard: docs no longer describe removed error codes, implemented features as reserved, or reserved features as complete.
    - Validation method: `rg` scans plus comparison against tests and source.
    - Evidence: stale `COMPANION_COMMAND_ROUTER_NOT_READY` target-state docs were updated; remaining mention is historical context in `companion-command-router.md`.
