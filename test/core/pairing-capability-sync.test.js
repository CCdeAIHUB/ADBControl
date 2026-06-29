import test from 'node:test';
import assert from 'node:assert/strict';
import {
  AdbControlError,
  Capability,
  Permission,
  applyAppHello,
  createDeviceState,
  syncAppCapabilities,
  syncAppPermissions,
  withAuthenticatedAppSession,
} from '../../core/domain/index.js';

test('untrusted app session cannot publish capabilities', () => {
  // Scenario: capability state is trusted input only after pairing/session auth.
  const state = createDeviceState({ deviceId: 'untrusted' });

  assert.throws(
    () => syncAppCapabilities(state, [Capability.SCREEN_STREAM_H264]),
    (error) => {
      assert.ok(error instanceof AdbControlError);
      assert.equal(error.subsystem, 'pairing');
      assert.equal(error.code, 'PAIRING_REQUIRED');
      return true;
    },
  );
});

test('trusted app hello initializes app-side device identity metadata', () => {
  // Scenario: hello binds a trusted app instance to the normalized Core device state.
  const state = withAuthenticatedAppSession(createDeviceState({ deviceId: 'pixel-2' }), true);
  const updated = applyAppHello(state, {
    appInstanceId: 'app-instance-1',
    androidSdk: 35,
    appVersion: '0.1.0',
    deviceModel: 'Pixel Test Device',
  });

  assert.equal(updated.deviceId, 'pixel-2');
  assert.equal(updated.appHello.appInstanceId, 'app-instance-1');
  assert.equal(updated.appHello.androidSdk, 35);
});

test('capability and permission sync update separate state fields', () => {
  // Scenario: routing must require both capability support and permission allowance.
  let state = withAuthenticatedAppSession(createDeviceState({ deviceId: 'pixel-3' }), true);
  state = syncAppCapabilities(state, [Capability.SCREEN_STREAM_H264]);
  state = syncAppPermissions(state, [Permission.SCREEN_CAPTURE]);

  assert.equal(state.appCapabilities.has(Capability.SCREEN_STREAM_H264), true);
  assert.equal(state.permissions.has(Permission.SCREEN_CAPTURE), true);
});

test('permission revocation does not delete the capability record', () => {
  // Scenario: support remains known even when Android permission state changes later.
  let state = withAuthenticatedAppSession(createDeviceState({ deviceId: 'pixel-4' }), true);
  state = syncAppCapabilities(state, [Capability.SCREEN_STREAM_H264]);
  state = syncAppPermissions(state, [Permission.SCREEN_CAPTURE]);
  state = syncAppPermissions(state, []);

  assert.equal(state.appCapabilities.has(Capability.SCREEN_STREAM_H264), true);
  assert.equal(state.permissions.has(Permission.SCREEN_CAPTURE), false);
});
