import test from 'node:test';
import assert from 'node:assert/strict';
import { parseAdbDevices, parseGetprop } from '../../core/adb/adb-adapter.js';

test('parseAdbDevices parses connected device rows', () => {
  const devices = parseAdbDevices(`List of devices attached\nserial123 device product:test model:Phone device:test transport_id:1\n`);
  assert.equal(devices.length, 1);
  assert.equal(devices[0].serial, 'serial123');
  assert.equal(devices[0].state, 'device');
  assert.equal(devices[0].details.model, 'Phone');
});

test('parseGetprop parses property rows', () => {
  const properties = parseGetprop(`[ro.product.model]: [Phone]\n[ro.build.version.sdk]: [35]\n`);
  assert.equal(properties['ro.product.model'], 'Phone');
  assert.equal(properties['ro.build.version.sdk'], '35');
});
