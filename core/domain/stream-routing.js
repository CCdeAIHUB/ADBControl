import { Capability, hasAppCapability } from './capabilities.js';
import { Permission, hasPermission } from './permissions.js';
import { capabilityError, pairingError, permissionError, schemaError } from './errors.js';

export function routeStreamOpen(state, payload) {
  validateStreamOpenPayload(payload);

  if (payload.realtime !== true) {
    return routeNonRealtimeRecording(state, payload);
  }

  if (!state.appSessionAuthenticated) {
    throw pairingError('Real-time screen streaming requires a trusted Android app session.', {
      deviceId: state.deviceId,
      operation: 'stream.open',
    });
  }

  if (!hasAppCapability(state, Capability.SCREEN_STREAM_H264)) {
    throw capabilityError('Android app capability screen.stream.h264 is required for real-time H.264 chunks.', {
      deviceId: state.deviceId,
      requiredCapability: Capability.SCREEN_STREAM_H264,
      adbOnline: state.adbOnline,
    });
  }

  if (!hasPermission(state, Permission.SCREEN_CAPTURE)) {
    throw permissionError('Screen capture permission is required for real-time screen streaming.', {
      deviceId: state.deviceId,
      requiredPermission: Permission.SCREEN_CAPTURE,
    });
  }

  return {
    operation: 'stream.open',
    channel: 'android_app',
    streamKind: 'realtime_h264_chunks',
    codec: 'h264',
    source: 'screen',
    transportBoundary: 'android_app_session',
    mp4RequiredToStart: false,
    recordingSideEffect: Boolean(payload.recordToMediaStore),
  };
}

function routeNonRealtimeRecording(state, payload) {
  if (!state.appSessionAuthenticated || !hasAppCapability(state, Capability.SCREEN_STREAM_H264)) {
    throw capabilityError('Recording requires an authenticated Android app with screen.stream.h264 capability.', {
      deviceId: state.deviceId,
      requiredCapability: Capability.SCREEN_STREAM_H264,
    });
  }

  if (!hasPermission(state, Permission.SCREEN_CAPTURE)) {
    throw permissionError('Screen capture permission is required for recording.', {
      deviceId: state.deviceId,
      requiredPermission: Permission.SCREEN_CAPTURE,
    });
  }

  return {
    operation: 'stream.open',
    channel: 'android_app',
    streamKind: 'mp4_recording',
    codec: payload.codec ?? 'h264',
    source: payload.source ?? 'screen',
    transportBoundary: 'android_app_session',
    mp4RequiredToStart: true,
    recordingSideEffect: true,
  };
}

function validateStreamOpenPayload(payload) {
  if (!payload || typeof payload !== 'object') {
    throw schemaError('stream.open payload must be an object.');
  }

  if (payload.source !== 'screen') {
    throw schemaError('Only screen streaming is supported by the current routing skeleton.', {
      receivedSource: payload.source,
    });
  }

  if ((payload.codec ?? 'h264') !== 'h264') {
    throw schemaError('Only h264 codec is supported by the current routing skeleton.', {
      receivedCodec: payload.codec,
    });
  }

  if (payload.realtime === true && payload.container === 'mp4') {
    throw schemaError('realtime=true uses raw_h264_chunks; MP4 recording is a separate media-store path.', {
      receivedContainer: payload.container,
    });
  }
}
