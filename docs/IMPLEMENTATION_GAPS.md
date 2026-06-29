# ADBControl Implementation Gaps

Last updated: 2026-06-30

This document records unfinished or unverified project areas in the current PR. It is a delivery checklist, not an implementation guide.

## Status legend

| Status | Meaning |
| --- | --- |
| Not implemented | No production code exists yet. |
| Boundary only | Interfaces, models, tests, or contracts exist, but the real workflow is not connected. |
| Partially implemented | Some executable code exists, but integration or verification is incomplete. |
| Environment blocked | This environment could not commit or verify the related platform-specific work. |
| Needs external verification | Requires SDK, native dependency, CI, emulator, or physical device validation. |

## Gap register

| ID | Area | Status | Existing work | Missing work | Acceptance criteria | Verification required |
| --- | --- | --- | --- | --- | --- | --- |
| GAP-001 | Core `ServerConfig` and service identity | Not implemented | Transport accepts options supplied by caller. | Config module, first-run config lifecycle, identity material loading, validation, and clear failure mode. | Startup creates or loads stable config; invalid config fails loudly; no silent downgrade. | Node tests and config lifecycle review. |
| GAP-002 | Pairing ingress and trust persistence | Boundary only | Domain sync requires trusted session state. | Real ingress, approval result persistence, revocation, expiry, and restart recovery. | Unknown app cannot sync; approved app can sync; revoked app cannot reuse old trust. | Unit and integration tests. |
| GAP-003 | Core HTTP API | Not implemented | Schemas and domain/application modules exist. | HTTP server, schema middleware, route mapping, error mapping, caller auth boundary. | Invalid requests return schema error; valid operations route through capability/permission checks. | Node HTTP integration tests. |
| GAP-004 | Device registry | Boundary only | In-memory device state helpers exist. | Persistent registry, discovery updates, app session registry, online/offline transitions. | State updates are durable and auditable; stale sessions become unavailable. | State transition tests. |
| GAP-005 | Android pairing result state | Boundary only | Android session model and control envelope helpers exist. | Pairing result handling, local trusted Core state, retry/error state, lifecycle integration. | App reports capabilities only after trusted pairing succeeds. | Android unit tests and Core test server integration. |
| GAP-006 | Android real-time media permission entry | Environment blocked | Safe bootstrap Activity plus session/permission models exist. | Platform permission entry, visible lifecycle, stop/revoke handling, service-state handoff. | No real-time media session starts before permission state is valid; stop/revoke closes active state. | Android instrumented and device tests. |
| GAP-007 | Android real-time encoder adapter | Environment blocked | Frame, sink, encoder-session, and pipeline contracts exist. | Platform adapter, runtime settings, lifecycle cleanup, error path. | Authorized session can produce realtime chunks; close stops output and releases resources. | Android emulator/device tests. |
| GAP-008 | Android frame transport adapter | Environment blocked | Native transport wrapper and frame sink interface exist. | Adapter from authorized frame stream to native transport, failure handling, backpressure. | Only active authorized sessions can send frames; failure marks session failed. | JVM fake-engine tests and Android native integration tests. |
| GAP-009 | End-to-end `stream.open(realtime=true)` | Boundary only | Core routing rules are tested; Android frame/session contracts exist. | Core API to app request path, session negotiation, chunk delivery, stop/close protocol. | Caller receives realtime chunks without waiting for recording output; stop releases Core and app resources. | Core/app integration and Android device tests. |
| GAP-010 | Recording artifact workflow | Boundary only | Media store supports finalized artifacts and `mp4` extension. | Recording producer, progress/finalization events, failed-recording state. | Recording creates finalized artifact only after completion; failed runs are explicit. | Core media tests and platform integration tests. |
| GAP-011 | Android native build | Needs external verification | CMake and JNI boundary exist. | SDK/NDK job, ABI-compatible native dependency, library load smoke test. | Missing dependency fails clearly; complete environment builds and loads native layer. | Android CI and emulator/device smoke test. |
| GAP-012 | Native transport security config | Boundary only | Native connection boundary exists. | Service identity validation, protocol version checks, explicit transport errors. | Invalid identity or incompatible version fails clearly; no insecure fallback. | Native integration tests. |
| GAP-013 | Bundled ADB packaging | Not implemented | ADB adapter can use configured executable path. | Bundled executable discovery, platform/architecture selection, version reporting, update strategy. | Supported platform finds bundled ADB; unsupported platform fails clearly. | Platform packaging tests. |
| GAP-014 | Web management UI | Not implemented | Project intent is documented. | Device list, pairing approval, capability display, artifact browser, operation controls. | User can manage pairing, devices, capabilities, and artifacts through UI. | UI tests and browser validation. |
| GAP-015 | CI workflows | Not implemented | Local Node tests were run manually. | GitHub Actions for Node tests and Android build setup. | PR updates run tests automatically; failures are visible in status checks. | GitHub Actions runs. |

## Current verification snapshot

Current local verification command:

```bash
npm test
```

Last recorded result: 20 tests passed, 0 failed.

No Android SDK/NDK build, emulator test, or physical device validation has been completed in this environment.
