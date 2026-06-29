import { execFile } from 'node:child_process';

export class AdbAdapterError extends Error {
  constructor({ message, exitCode = null, stderr = '', commandName = '' }) {
    super(message);
    this.name = 'AdbAdapterError';
    this.code = 'ADB_ADAPTER_FAILED';
    this.exitCode = exitCode;
    this.stderr = stderr;
    this.commandName = commandName;
  }
}

export class AdbAdapter {
  constructor({ adbPath = process.env.ADBCONTROL_ADB_PATH ?? 'adb', defaultTimeoutMs = 15_000 } = {}) {
    this.adbPath = adbPath;
    this.defaultTimeoutMs = defaultTimeoutMs;
  }

  async listDevices() {
    const { stdout } = await this.#run('listDevices', ['devices', '-l']);
    return parseAdbDevices(stdout);
  }

  async capturePng(deviceId) {
    assertDeviceId(deviceId);
    return this.#run('capturePng', ['-s', deviceId, 'exec-out', 'screencap', '-p'], {
      maxBuffer: 64 * 1024 * 1024,
    });
  }

  async queryDeviceProperties(deviceId) {
    assertDeviceId(deviceId);
    const { stdout } = await this.#run('queryDeviceProperties', ['-s', deviceId, 'shell', 'getprop'], {
      maxBuffer: 4 * 1024 * 1024,
    });
    return parseGetprop(stdout);
  }

  async #run(commandName, args, options = {}) {
    return new Promise((resolve, reject) => {
      execFile(this.adbPath, args, {
        timeout: options.timeoutMs ?? this.defaultTimeoutMs,
        maxBuffer: options.maxBuffer ?? 16 * 1024 * 1024,
        windowsHide: true,
        encoding: 'buffer',
      }, (error, stdout, stderr) => {
        const out = Buffer.isBuffer(stdout) ? stdout.toString('utf8') : String(stdout ?? '');
        const err = Buffer.isBuffer(stderr) ? stderr.toString('utf8') : String(stderr ?? '');
        if (error) {
          reject(new AdbAdapterError({
            message: `ADB adapter command ${commandName} failed`,
            exitCode: typeof error.code === 'number' ? error.code : null,
            stderr: err,
            commandName,
          }));
          return;
        }
        resolve({ stdout: out, stderr: err, commandName });
      });
    });
  }
}

function assertDeviceId(deviceId) {
  if (!deviceId || typeof deviceId !== 'string') throw new TypeError('deviceId is required');
}

export function parseAdbDevices(output) {
  return output
    .split(/\r?\n/)
    .slice(1)
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const [serial, state, ...detailParts] = line.split(/\s+/);
      const details = Object.fromEntries(
        detailParts
          .map((part) => part.split(':'))
          .filter(([key, value]) => key && value)
          .map(([key, value]) => [key, value]),
      );
      return { serial, state, details };
    });
}

export function parseGetprop(output) {
  return Object.fromEntries(
    output
      .split(/\r?\n/)
      .map((line) => line.match(/^\[([^\]]+)\]: \[(.*)\]$/))
      .filter(Boolean)
      .map((match) => [match[1], match[2]]),
  );
}
