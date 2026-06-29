import { pairingError } from './errors.js';

function assertTrustedAppSession(state, action) {
  if (!state.appSessionAuthenticated) {
    // Capability and permission state is trusted input only after pairing/session auth.
    throw pairingError(`Trusted app session is required before ${action}.`, {
      deviceId: state.deviceId,
      action,
    });
  }
}

export function applyAppHello(state, hello) {
  assertTrustedAppSession(state, 'app hello');

  if (!hello || typeof hello.appInstanceId !== 'string' || hello.appInstanceId.length === 0) {
    throw new TypeError('hello.appInstanceId must be a non-empty string');
  }

  return {
    ...state,
    appHello: {
      appInstanceId: hello.appInstanceId,
      androidSdk: hello.androidSdk ?? null,
      appVersion: hello.appVersion ?? null,
      deviceModel: hello.deviceModel ?? null,
    },
  };
}

export function syncAppCapabilities(state, capabilities) {
  assertTrustedAppSession(state, 'capability sync');

  return {
    ...state,
    appCapabilities: new Set(capabilities),
  };
}

export function syncAppPermissions(state, permissions) {
  assertTrustedAppSession(state, 'permission sync');

  return {
    ...state,
    permissions: new Set(permissions),
  };
}
