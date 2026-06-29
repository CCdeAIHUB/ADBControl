export function createDeviceState({ deviceId }) {
  if (!deviceId || typeof deviceId !== 'string') {
    throw new TypeError('deviceId must be a non-empty string');
  }

  return {
    deviceId,
    adbOnline: false,
    appSessionAuthenticated: false,
    appHello: null,
    adbCapabilities: new Set(),
    appCapabilities: new Set(),
    permissions: new Set(),
  };
}

export function withAdbState(state, { online, capabilities = [] }) {
  return {
    ...state,
    adbOnline: Boolean(online),
    adbCapabilities: new Set(capabilities),
  };
}

export function withAuthenticatedAppSession(state, authenticated = true) {
  return {
    ...state,
    appSessionAuthenticated: Boolean(authenticated),
  };
}

export function withPermissions(state, permissions) {
  return {
    ...state,
    permissions: new Set(permissions),
  };
}
