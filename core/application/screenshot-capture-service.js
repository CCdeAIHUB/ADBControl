import { Capability, hasCapability } from '../domain/capabilities.js';
import { AdbControlError } from '../domain/errors.js';

export class ScreenshotCaptureService {
  constructor({ adbAdapter, mediaStore }) {
    if (!adbAdapter) throw new TypeError('adbAdapter is required');
    if (!mediaStore) throw new TypeError('mediaStore is required');
    this.adbAdapter = adbAdapter;
    this.mediaStore = mediaStore;
  }

  async captureViaAdb(state, { requestId = null } = {}) {
    if (!hasCapability(state, Capability.SCREENSHOT)) {
      throw new AdbControlError({
        code: 'CAPABILITY_UNAVAILABLE',
        subsystem: 'capability',
        retryable: false,
        message: 'ADB screenshot capability is unavailable for this device.',
        details: { requestId, requiredCapability: Capability.SCREENSHOT },
      });
    }

    const result = await this.adbAdapter.capturePng(state.deviceId);
    const bytes = Buffer.isBuffer(result.stdout) ? result.stdout : Buffer.from(result.stdout, 'binary');

    return this.mediaStore.saveCompleteArtifact({
      deviceId: state.deviceId,
      kind: 'adb-screenshot',
      extension: 'png',
      bytes,
      metadata: {
        requestId,
        source: 'adb',
        commandName: result.commandName,
      },
    });
  }
}
