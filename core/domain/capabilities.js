export const Capability = Object.freeze({
  SCREEN_STREAM_H264: 'screen.stream.h264',
  SCREENSHOT: 'device.screenshot',
  DEVICE_INFO: 'device.info',
  KEYBOARD_INPUT: 'input.keyboard',
  TOUCH_INPUT: 'input.touch',
});

export function hasCapability(state, capability) {
  return state.appCapabilities.has(capability) || state.adbCapabilities.has(capability);
}

export function hasAppCapability(state, capability) {
  return state.appCapabilities.has(capability);
}
