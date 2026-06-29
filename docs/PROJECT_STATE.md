# ADBControl Project State

Last updated: 2026-06-30

## Repository checkpoint

The repository is `CCdeAIHUB/ADBControl` on the `main` branch. At the start of this continuation round, the readable root content was minimal: `README.md` only contained the project title and no in-repository `SKILL.md` was found. This branch therefore starts by adding the execution contract and stable project state before deeper code changes.

## Locked project intent

ADBControl is a Core + Android companion app system for Android device control.

Core responsibilities:

- Bundle or manage ADB capability access across supported desktop/server platforms.
- Convert ADB capabilities and Android companion app capabilities into unified HTTP APIs.
- Decide per operation whether the ADB channel, Android app channel, or both should be used.
- Provide a web management UI for pairing, devices, capabilities, permissions, and media/control operations.
- Use JSON Schema / OpenAPI as the authoritative API/capability/error contract.

Android companion app responsibilities:

- Provide app-side capabilities such as screenshot, hardware information, simulated input, real-time screen streaming, and app-side permission/capability reporting.
- Communicate with Core through a secure session protocol rather than raw UDP.
- Android companion app implementation is Kotlin / Gradle Kotlin DSL.
- Do not use HTTP/3/Cronet as the locked transport implementation and do not add a fake native engine placeholder.

## Continuation memory from the previous round

The previous round's working context indicated that these seven capability areas were the active implementation target:

1. Core certificate generation and `ServerConfig`.
2. Trust ingress / pairing trust boundary.
3. Core media store.
4. Android JNI native QUIC engine boundary.
5. Android pairing result integration into service state.
6. Android hello / capability / permission initial synchronization.
7. Real-time H.264 chunk streaming connected to `stream.open(realtime=true)`, while file-level MP4 recording remains a separate path.

Because the current GitHub repository did not yet expose those files on `main`, these were treated as the required reconstruction and verification checklist.

## Current branch progress

This branch now contains:

- `core/domain/`: executable Core routing, trust, capability, and permission skeleton.
- `core/adb/`: allowlisted ADB adapter for device listing, screenshot capture, and property query. It intentionally does not expose arbitrary shell execution.
- `core/transport/`: framed TCP/TLS JSON message transport for real socket-based Core/app-style messaging tests.
- `android-app/`: Kotlin / Gradle Kotlin DSL app module.
- `android-app/src/main/cpp/`: JNI native transport boundary linked against a real QUIC library through CMake.

Implemented in executable Core code:

- structured domain errors matching the schema subsystem model;
- device state with separate ADB/app capabilities and app session authentication state;
- pairing-protected hello, capability sync, and permission sync;
- stream routing for `stream.open(realtime=true)` that requires an authenticated Android app session, `screen.stream.h264` capability, and screen capture permission;
- explicit separation between real-time H.264 chunks and MP4 recording side effects;
- allowlisted ADB process execution adapter;
- TCP/TLS framed transport for real network-message tests.

Partially added but not fully verified in this environment:

- Android JNI native QUIC boundary using CMake and external QUIC library inputs.

Not completed in this pass:

- file-level media store persistence was drafted locally, but GitHub write safety checks blocked committing that file;
- Android MediaProjection + MediaCodec screen encoder was drafted locally, but GitHub write safety checks blocked committing the direct capture-to-stream implementation;
- native QUIC Android build was not compiled because the environment does not provide Android SDK/NDK and the required ABI-compatible QUIC library.

## Current execution rule

Do not implement isolated hotfixes. Every code change must preserve module boundaries and either add executable tests/specs or explain why the change is documentation-only.
