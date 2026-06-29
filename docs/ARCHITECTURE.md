# ADBControl Architecture Boundaries

## 1. System overview

ADBControl is split into four top-level areas:

```text
Caller / Web UI
      |
      v
Core HTTP API / OpenAPI
      |
      v
Capability Router
  |           |
  v           v
ADB Adapter   Android App Session Adapter
  |           |
  v           v
USB/TCP ADB   Secure app transport / media channel
```

The Core must never expose raw platform or device operations directly to callers. All requests go through API validation, permission checks, capability routing, and structured error handling.

## 2. Core modules

Recommended Core module boundaries:

- `api`: HTTP endpoints, request/response mapping, OpenAPI integration.
- `schema`: generated or validated models from JSON Schema / OpenAPI.
- `capability`: capability model, capability registry, routing decision logic.
- `permission`: permission model, grant state, policy checks.
- `pairing`: trust ingress, certificate exchange, pairing state.
- `transport`: secure session abstraction for Android app communication.
- `adb`: ADB discovery, ADB command execution, ADB capability adapter.
- `media`: media session model, screen stream metadata, recorded media store.
- `device`: normalized device identity and device state.
- `web`: static management UI serving boundary.

## 3. Android app modules

Recommended Android module boundaries:

- `service`: foreground/background service and lifecycle integration.
- `pairing`: pairing result handling and trust persistence.
- `capability`: app-side capability and permission reporting.
- `transport`: session connection to Core and native transport bridge.
- `media`: MediaProjection, H.264 encoding, real-time chunk stream, optional MP4 recording.
- `input`: keyboard and touch simulation.
- `deviceinfo`: hardware/software information collection.
- `jni`: narrow native boundary only; no business logic should leak into JNI.

## 4. Transport rules

The app channel may use QUIC or another UDP-based upper-layer transport, but it must not be raw UDP. The protocol must define:

- peer identity and pairing trust;
- session authentication;
- replay protection;
- message envelope;
- stream/session IDs;
- error codes;
- capability negotiation;
- media stream framing;
- graceful close and reconnect behavior.

## 5. Media rules

Real-time stream and file recording are separate use cases:

- `stream.open(realtime=true)` returns a real-time stream session that emits H.264 chunks.
- MP4 recording writes file-level media artifacts and must not block the real-time stream path.
- The media store is responsible for recorded artifacts, retention metadata, and lookup.
- The stream path is responsible for low-latency chunks, backpressure, and clean close.

## 6. Capability routing rules

The router chooses a channel based on:

1. requested operation;
2. device connection state;
3. ADB availability;
4. Android app availability;
5. permission grants;
6. capability freshness;
7. operation latency and reliability requirements.

No endpoint should hard-code ADB or app usage unless the API itself explicitly targets a diagnostic path.

## 7. Error rules

Every error returned to callers must be structured with:

- stable error code;
- human-readable message;
- retryability;
- failing subsystem;
- optional details that do not leak secrets.
