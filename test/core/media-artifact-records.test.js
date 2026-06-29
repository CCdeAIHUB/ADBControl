import test from 'node:test';
import assert from 'node:assert/strict';
import { addArtifactRecord, createFinalizedArtifactRecord, findArtifactRecord } from '../../core/media/artifact-records.js';

test('finalized artifact record has generated scoped path and digest', () => {
  const record = createFinalizedArtifactRecord({
    deviceId: 'device-1',
    kind: 'screenshot',
    extension: 'png',
    byteLength: 3,
    bytes: Buffer.from([1, 2, 3]),
  });

  assert.equal(record.deviceId, 'device-1');
  assert.match(record.relativePath, /^[0-9a-f-]{36}\.png$/);
  assert.equal(record.sizeBytes, 3);
  assert.equal(record.status, 'finalized');
  assert.equal(record.sha256.length, 64);
});

test('artifact records are added and found without mutating the source index', () => {
  const record = createFinalizedArtifactRecord({
    deviceId: 'device-1',
    kind: 'stream-recording',
    extension: 'mp4',
    byteLength: 0,
    bytes: Buffer.alloc(0),
  });

  const base = { artifacts: [] };
  const next = addArtifactRecord(base, record);
  assert.equal(base.artifacts.length, 0);
  assert.equal(findArtifactRecord(next, record.id).id, record.id);
});
