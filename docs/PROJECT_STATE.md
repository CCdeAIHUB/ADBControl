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

- Provide app-side capabilities such as screenshot, hardware information, simulated keyboard input, simulated touch, real-time screen streaming, and app-side permission/capability reporting.
- Communicate with Core through a secure session protocol rather than raw UDP.

## Continuation memory from the previous round

The previous round's working context indicated that these seven capability areas were the active implementation target:

1. Core certificate generation and `ServerConfig`.
2. Trust ingress / pairing trust boundary.
3. Core media store.
4. Android JNI native QUIC engine boundary.
5. Android pairing result integration into service state.
6. Android hello / capability / permission initial synchronization.
7. Real-time H.264 chunk streaming connected to `stream.open(realtime=true)`, while file-level MP4 recording remains a separate path.

Because the current GitHub repository does not yet expose those files on `main`, these are treated as the required reconstruction and verification checklist for the next implementation pass.

## Current execution rule

Do not implement isolated hotfixes. Every code change must preserve module boundaries and either add executable tests/specs or explain why the change is documentation-only.
