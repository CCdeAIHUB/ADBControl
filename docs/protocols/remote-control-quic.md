# Core remote-control QUIC protocol

## Security boundary

The remote-control endpoint is a separate QUIC endpoint from the Android
Companion endpoint. QUIC completes a TLS 1.3 handshake before the core reads an
application request, and the implementation has no plaintext fallback.

The core creates a persistent self-signed certificate on first startup. A client
must trust that certificate out of band or pin the SHA-256 fingerprint printed
by the core. Encrypting traffic without validating the server certificate does
not protect against a man-in-the-middle attack.

Remote listening is disabled unless `ADBCONTROL_REMOTE_LISTEN` is explicitly
set. The built-in administrator is rejected by this endpoint even when its
password is correct. The recommended first-run sequence is:

1. use the local host frontend to change the built-in administrator password;
2. create remote users and assign their allowed devices in that frontend;
3. record and pin the printed certificate fingerprint in every remote client;
4. bind the listener to the required interface and restrict the UDP port with
   the host firewall.

## Configuration

- `ADBCONTROL_REMOTE_LISTEN`: QUIC UDP bind address, for example
  `127.0.0.1:45921` or `0.0.0.0:45921`. An unset/empty value disables it.
- `ADBCONTROL_REMOTE_DATA_DIR`: optional directory for `accounts.json`,
  `remote.cert.der`, and `remote.key.der`. On Windows it defaults to
  `%LOCALAPPDATA%/ADBControl/remote`.

The account file stores Argon2id password hashes and random salts, never
plaintext passwords. Session tokens are 256-bit random values, live in memory,
expire after eight hours, and are revoked on password change/reset or account
deletion. The certificate private key and account database must be readable only
by the core service identity in production deployment.

## Framing

Protocol name: `adbcontrol-core-remote-quic`; version: `1`.

Each request uses one QUIC bidirectional stream. The client writes one UTF-8 JSON
request, finishes its send side, reads one UTF-8 JSON response, then observes the
server's stream finish. Requests are limited to 1 MiB. Datagram control requests
are not accepted.

```json
{
  "id": "request-1",
  "sessionToken": "omitted only for auth.login",
  "method": "device.list",
  "params": {}
}
```

Responses use the same `id`, `ok`, `result`, and structured `error` contract as
core IPC.

## Authentication methods

### `auth.login`

Parameters: `username`, `password`. Returns `sessionToken`, `username`, `role`,
`devices`, and `passwordChangeRequired`. The built-in immutable username
`admin` is a local-frontend identity and this method returns
`REMOTE_AUTH_ADMIN_LOCAL_ONLY` for it. Only ordinary remote users can establish
a remote-control session.

### `auth.me`, `auth.logout`, `auth.changePassword`

`auth.changePassword` accepts `currentPassword` and `newPassword`, invalidates
all sessions for the account, and requires a new login. New passwords must be
8-1024 bytes. There is deliberately no username-change method, so the built-in
administrator username cannot be modified.

## Local frontend administrator methods

These methods are not exposed as a remotely usable administrator login flow.
The host frontend invokes the `remote.admin.*` equivalents through the Core
JSON-Lines stdio IPC, where the Core process and host service share a local
process boundary:

- `remote.admin.login`, `remote.admin.me`, `remote.admin.logout`, and
  `remote.admin.changePassword`
- `remote.admin.users.list`
- `remote.admin.users.create` with `username`, `password`
- `remote.admin.users.delete` with `username`; the built-in administrator cannot be
  deleted
- `remote.admin.users.resetPassword` with `username`, `newPassword`; this revokes all
  target sessions and requires that user to change the reset password
- `remote.admin.devices.assign` with `username`, `deviceId`, and optional `assigned`
  (default `true`). The trusted host adapter validates new assignments against
  its remembered device catalog. Pass `assigned: false` to remove a stale assignment.

Remote users can read public core/capability metadata, receive a filtered `device.list`, and use
device-scoped methods only for assigned devices. Assigned devices remain in `device.list` while
offline with `adbState: "offline"`; a current ADB identity replaces an equivalent stale mDNS
collision suffix without creating a duplicate card. A remote `adb.exec` call by a
non-administrator must begin with an assigned `-s <deviceId>` target. Keep-alive
and use a device-scoped ADB command; server/global commands such as `connect`,
`disconnect`, `devices`, `start-server`, and `kill-server` are rejected.
Keep-alive configuration and all otherwise-unclassified core methods are
administrator only.

