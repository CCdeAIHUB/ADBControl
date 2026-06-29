export const Permission = Object.freeze({
  SCREEN_CAPTURE: 'android.permission.screen_capture',
  INPUT_CONTROL: 'android.permission.input_control',
});

export function hasPermission(state, permission) {
  return state.permissions.has(permission);
}
