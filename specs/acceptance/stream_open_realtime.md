# Acceptance Spec: `stream.open(realtime=true)`

## Scenario 1: open a real-time H.264 screen stream

Given a paired Android device has granted screen capture permission
And the Android app channel reports `screen.stream.h264` capability
When the caller sends `stream.open` with `realtime=true`, `source=screen`, and `codec=h264`
Then Core validates the request against `schemas/stream-open.schema.json`
And Core routes the operation to the Android app session adapter
And Core returns a stream session id
And the session emits ordered H.264 chunks until closed
And no MP4 file is required for the real-time stream to start.

## Scenario 2: keep MP4 recording separate from real-time chunks

Given a real-time screen stream is already open
When the caller requests `recordToMediaStore=true`
Then Core may create a media-store recording artifact
But backpressure or failure in MP4 writing must not silently stop the real-time chunk stream
And recording errors must be reported as structured media/storage errors.

## Scenario 3: reject missing app capability

Given a device is connected through ADB only
And the Android app channel is unavailable or has not reported `screen.stream.h264`
When the caller sends `stream.open` with `realtime=true`
Then Core rejects the request with a structured `capability` error
And Core must not pretend that ADB can provide the same low-latency stream unless an explicit ADB streaming adapter exists.

## Scenario 4: close stream cleanly

Given a real-time stream session is active
When the caller closes the session or the device disconnects
Then Core sends a close signal to the app session when possible
And releases session resources
And marks the stream state as closed with a terminal reason.
