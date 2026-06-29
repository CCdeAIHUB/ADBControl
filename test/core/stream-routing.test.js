import test from 'node:test';
import assert from 'node:assert/strict';
import {
  AdbControlError,
  Capability,
  Permission,
  createDeviceState,
  routeStreamOpen,
  syncAppCapabilities,
  syncAppPermissions,
  withAdbState,
  withAuthenticatedAppSession,
} from '../../core/domain/index.js';

function pairedStreamingDevice() {
  let state = createDeviceState({ deviceId: 'pixel-1' });
  state = withAuthenticatedAppSession(state, true);
  state = syncAppCapabilities(state, [Capability.SCREEN_STREAM_H264]);
  state = syncAppPermissions(state, [Permission.SCREEN_CAPTURE]);
  return state;
}

test('stream.open realtime=true routes to Android app real-time H.264 chunks without requiring MP4', () => {
  // Scenario: screen stream must start as low-latency chunks, not as a file recording dependency.
  const plan = routeStreamOpen(pairedStreamingDevice(), {
    realtime: true,
    source: 'screen',
    codec: 'h264',
  });

  assert.equal(plan.channel, 'android_app');
  assert.equal(plan.streamKind, 'realtime_h264_chunks');
  assert.equal(plan.transportBoundary, 'android_app_session');
  assert.equal(plan.mp4RequiredToStart, false);
  assert.equal(plan.recordingSideEffect, false);
});

test('recordToMediaStore remains a side effect and does not make MP4 required to start', () => {
  // Scenario: MP4 recording can be requested, but real-time chunks remain the primary stream path.
  const plan = routeStreamOpen(pairedStreamingDevice(), {
    realtime: true,
    source: 'screen',
    codec: 'h264',
    recordToMediaStore: true,
  });

  assert.equal(plan.streamKind, 'realtime_h264_chunks');
  assert.equal(plan.recordingSideEffect, true);
  assert.equal(plan.mp4RequiredToStart, false);
});

test('ADB-only state cannot satisfy real-time H.264 app streaming capability', () => {
  // Scenario: ADB online alone must not be treated as equivalent to app-side low-latency streaming.
  const adbOnlyState = withAdbState(createDeviceState({ deviceId: 'adb-only' }), {
    online: true,
    capabilities: [Capability.SCREENSHOT],
  });

  assert.throws(
    () => routeStreamOpen(adbOnlyState, { realtime: true, source: 'screen', codec: 'h264' }),
    (error) => {
      assert.ok(error instanceof AdbControlError);
      assert.equal(error.subsystem, 'pairing');
      assert.equal(error.code, 'PAIRING_REQUIRED');
      return true;
    },
  );
});

test('missing app capability is reported as a structured capability error', () => {
  // Scenario: an authenticated app session still cannot route a capability it did not publish.
  const state = withAuthenticatedAppSession(createDeviceState({ deviceId: 'missing-cap' }), true);

  assert.throws(
    () => routeStreamOpen(state, { realtime: true, source: 'screen', codec: 'h264' }),
    (error) => {
      assert.ok(error instanceof AdbControlError);
      assert.equal(error.subsystem, 'capability');
      assert.equal(error.code, 'CAPABILITY_UNAVAILABLE');
      assert.equal(error.details.requiredCapability, Capability.SCREEN_STREAM_H264);
      return true;
    },
  );
});

test('permission loss disables new streams while preserving the capability record', () => {
  // Scenario: capability support and current permission grant are separate routing inputs.
  let state = createDeviceState({ deviceId: 'permission-loss' });
  state = withAuthenticatedAppSession(state, true);
  state = syncAppCapabilities(state, [Capability.SCREEN_STREAM_H264]);
  state = syncAppPermissions(state, []);

  assert.equal(state.appCapabilities.has(Capability.SCREEN_STREAM_H264), true);

  assert.throws(
    () => routeStreamOpen(state, { realtime: true, source: 'screen', codec: 'h264' }),
    (error) => {
      assert.ok(error instanceof AdbControlError);
      assert.equal(error.subsystem, 'permission');
      assert.equal(error.code, 'PERMISSION_DENIED');
      assert.equal(error.details.requiredPermission, Permission.SCREEN_CAPTURE);
      return true;
    },
  );
});

test('realtime=true rejects mp4 container because MP4 is a separate media-store path', () => {
  assert.throws(
    () => routeStreamOpen(pairedStreamingDevice(), {
      realtime: true,
      source: 'screen',
      codec: 'h264',
      container: 'mp4',
    }),
    (error) => {
      assert.ok(error instanceof AdbControlError);
      assert.equal(error.subsystem, 'schema');
      assert.equal(error.code, 'SCHEMA_INVALID');
      return true;
    },
  );
});
