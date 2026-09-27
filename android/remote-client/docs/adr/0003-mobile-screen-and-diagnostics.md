# ADR 0003: Mobile screen transport and client diagnostics

## Status

Accepted, 2026-09-27.

## Context

The Android remote client currently sends control requests to the Rust Core over
authenticated QUIC, but its preview page only polls companion PNG screenshots.
The Web application already owns a stable scrcpy H.264 session implementation in
the Go service. Reimplementing ADB forwarding and scrcpy framing in the Android
client or presenting PNG polling as live video would create two incompatible
media implementations.

The Android client also stores structured diagnostics only on the phone. This
makes failures which happen before a Web request invisible in the router log
console.

## Decision

1. Keep commands, login and diagnostic upload on the certificate-pinned QUIC
   connection.
2. Add the authenticated remote method `diagnostics.report`. It accepts a
   bounded, metadata-only batch, sanitises it again in Core, writes it through
   Core diagnostics and mirrors one structured line to stderr so it is visible
   in the router container log.
3. Reuse the Go service's existing scrcpy implementation for Android live video.
   A mobile-only WebSocket route accepts the remote session token, validates it
   through a local-only Core IPC method, verifies that the requested device is
   assigned to the remote account, and then enters the same `StartScrcpy` flow as
   the Web page.
4. The Android client decodes protocol-v2 H.264 with `MediaCodec`, renders to a
   `SurfaceView`, sends touch messages over the same WebSocket, and offers an
   immersive full-screen mode. PNG polling remains an explicitly labelled
   compatibility fallback.
5. Cleartext `ws://` is allowed only for loopback and RFC1918 LAN addresses.
   Non-private targets require `wss://`.

## Consequences

- Web and Android use one scrcpy session implementation and the same packet and
  touch protocol.
- Remote users cannot access the administrator HTTP API: their token is accepted
  only by the mobile screen route and only for assigned devices.
- Deployments must publish the Web service port in addition to the QUIC port.
  The client derives port 18087 from the QUIC host by default and lets the user
  override it in the Core profile.
- Client logs become remotely diagnosable without uploading command text,
  credentials, tokens, screenshots or video payloads.
