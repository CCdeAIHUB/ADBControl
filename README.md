# ADBControl

ADBControl is a cross-platform Android/ADB control system.

The project direction is **Core + Android companion app**:

- Core exposes unified HTTP APIs for ADB and Android-app capabilities.
- Core chooses the best channel per operation through a capability router.
- The Android companion app provides app-side capabilities such as screenshots, device information, simulated input, and real-time screen streaming.
- Control contracts use JSON Schema / OpenAPI as the authority.
- Device operations must pass through capability and permission checks.

## Current repository status

This branch adds the first in-repository execution contract, acceptance specs, executable Core domain skeleton, first Core adapters, scoped media persistence, an ADB screenshot-to-media application service, and an Android Kotlin/JNI module.

Start with:

- [`SKILL.md`](./SKILL.md) — mandatory execution rules for AI/Codex changes.
- [`docs/PROJECT_STATE.md`](./docs/PROJECT_STATE.md) — current continuation state and locked requirements.
- [`docs/ARCHITECTURE.md`](./docs/ARCHITECTURE.md) — initial module boundaries.
- [`schemas/control-api.schema.json`](./schemas/control-api.schema.json) — initial API envelope schema.
- [`schemas/stream-open.schema.json`](./schemas/stream-open.schema.json) — initial real-time stream request schema.
- [`specs/acceptance/`](./specs/acceptance/) — behavior-first acceptance specs.
- [`core/domain/`](./core/domain/) — executable routing, trust, capability, and permission domain skeleton.
- [`core/adb/`](./core/adb/) — allowlisted ADB adapter.
- [`core/application/`](./core/application/) — application services that compose domain checks with adapters.
- [`core/transport/`](./core/transport/) — framed TCP/TLS JSON transport.
- [`core/media/`](./core/media/) — scoped file media store with root containment checks.
- [`android-app/`](./android-app/) — Kotlin app module with JNI native transport boundary and no-side-effect frame/session contracts.
- [`test/core/`](./test/core/) — node:test coverage for Core rules and adapters.

## Test

```bash
npm test
```

Current local result: 20 tests passed, 0 failed.

## Android build note

The Android native transport requires an ABI-compatible QUIC library and headers via CMake inputs. No Cronet/HTTP3 fallback or mock native engine is included.

## Development rule

Do not implement device-control behavior as a one-off hotfix. Add or update tests/specs first, preserve module boundaries, and end each round with a verification report.
