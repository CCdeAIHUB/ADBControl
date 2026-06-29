# Acceptance Spec: Pairing, Trust, Capability, and Permission Sync

## Scenario 1: pairing creates trusted app session

Given Core has generated its server certificate and pairing configuration
And an Android app requests pairing
When the user approves the pairing request
Then Core records the trust relationship
And the Android app stores the pairing result
And subsequent app sessions authenticate against the established trust boundary.

## Scenario 2: untrusted session cannot publish capabilities

Given an Android app session is not paired or fails authentication
When it sends hello, capability, or permission state messages
Then Core rejects the messages
And no device capability state is updated from that session.

## Scenario 3: hello initializes device identity

Given a paired Android app connects to Core
When it sends a hello message
Then Core creates or updates normalized device identity
And associates the app session with that device.

## Scenario 4: capability and permission sync are separate

Given a paired Android app connects to Core
When it reports supported capabilities and current permission grants
Then Core records both states separately
And routing decisions require both capability support and permission allowance.

## Scenario 5: permission loss disables dependent operations

Given `screen.stream.h264` is supported
And screen capture permission was previously granted
When the app reports that screen capture permission is revoked
Then Core keeps the capability record
But rejects new screen stream requests with a structured `permission` error until permission is granted again.
