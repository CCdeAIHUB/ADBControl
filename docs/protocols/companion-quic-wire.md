# Companion QUIC wire protocol

ADBControl uses IETF QUIC with ALPN `adbcontrol-companion/1`. TLS server identity is pinned by the exact DER certificate delivered during pairing/configuration.

## Bootstrap and reconnect

When ADB is available, the desktop starts the companion app through its transparent connection activity and passes the selected LAN address, actual QUIC listener port, TLS server name, device id, and pinned DER certificate. The activity only starts the foreground connection service and then closes; it does not replace the phone's current foreground app.

The companion app stores this trusted configuration and reconnects immediately whenever its UI or foreground service starts. Native QUIC connection work runs on a dedicated connection thread. Handshakes time out after six seconds; if the desktop supplies a newer endpoint while a previous attempt is active, the app keeps only the newest configuration and connects it immediately after the active attempt exits. Once the control channel is ready, the desktop sends a heartbeat every 10 seconds and the app replies on the same stream so an otherwise idle session remains live.

ADBControl does not accept an unauthenticated UDP broadcast as a new connection configuration. A future no-ADB LAN discovery layer may advertise an endpoint, but a previously paired app must accept it only when the advertised certificate fingerprint matches its stored pin. Initial trust must still be established through ADB or an explicit user-confirmed pairing flow.

Every application stream starts with five bytes:

```text
41 43 51 31 TT
 A  C  Q  1 type
```

Stream type `1` is the single long-lived bidirectional control stream. Each direction carries repeated `uint32_be length` followed by one UTF-8 JSON `adbcontrol-companion-quic` envelope. A control envelope is limited to 4 MiB.

Stream type `2` is one unidirectional H.264 stream per projection session. Its preface is followed by `uint32_be metadata_length` and UTF-8 JSON metadata:

```json
{"sessionId":"...","codec":"h264","width":576,"height":1280,"bitrate":8000000,"frameRate":60}
```

Encoded packets then repeat until FIN:

```text
int64_be  presentation_time_us
uint32_be MediaCodec flags
uint32_be payload_length
bytes     H.264 payload
```

Codec-config packets keep `MediaCodec.BUFFER_FLAG_CODEC_CONFIG`; keyframes keep `MediaCodec.BUFFER_FLAG_KEY_FRAME`. The receiver must preserve packet order for decoder correctness and may drop decoded frames only after decode when rendering falls behind.
