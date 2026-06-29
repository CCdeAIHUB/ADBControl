import test from 'node:test';
import assert from 'node:assert/strict';
import { Capability } from '../../core/domain/capabilities.js';
import { createDeviceState, withAdbState } from '../../core/domain/device-state.js';
import { ScreenshotCaptureService } from '../../core/application/screenshot-capture-service.js';

test('screenshot capture service stores ADB screenshot as media artifact', async () => {
  const state = withAdbState(createDeviceState({ deviceId: 'device-1' }), {
    online: true,
    capabilities: [Capability.SCREENSHOT],
  });

  const saved = [];
  const service = new ScreenshotCaptureService({
    adbAdapter: {
      async capturePng(deviceId) {
        assert.equal(deviceId, 'device-1');
        return { stdout: Buffer.from([0x89, 0x50, 0x4e, 0x47]), commandName: 'capturePng' };
      },
    },
    mediaStore: {
      async saveCompleteArtifact(artifact) {
        saved.push(artifact);
        return { id: 'artifact-1', ...artifact };
      },
    },
  });

  const artifact = await service.captureViaAdb(state, { requestId: 'req-1' });
  assert.equal(artifact.id, 'artifact-1');
  assert.equal(artifact.deviceId, 'device-1');
  assert.equal(artifact.kind, 'adb-screenshot');
  assert.equal(artifact.extension, 'png');
  assert.equal(saved[0].metadata.source, 'adb');
});

test('screenshot capture service rejects devices without screenshot capability', async () => {
  const state = createDeviceState({ deviceId: 'device-1' });
  const service = new ScreenshotCaptureService({
    adbAdapter: { capturePng: async () => Buffer.alloc(0) },
    mediaStore: { saveCompleteArtifact: async () => ({}) },
  });

  await assert.rejects(
    () => service.captureViaAdb(state),
    (error) => error.code === 'CAPABILITY_UNAVAILABLE',
  );
});
