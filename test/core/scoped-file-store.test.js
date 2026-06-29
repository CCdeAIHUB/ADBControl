import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { ScopedFileMediaStore } from '../../core/media/scoped-file-store.js';

test('scoped file media store saves finalized artifact inside media root', async () => {
  const rootDir = await mkdtemp(path.join(os.tmpdir(), 'adbcontrol-media-'));
  try {
    const store = new ScopedFileMediaStore({ rootDir });
    const artifact = await store.saveCompleteArtifact({
      deviceId: 'device-1',
      kind: 'screenshot',
      extension: 'png',
      bytes: Buffer.from([1, 2, 3]),
      metadata: { source: 'test' },
    });

    assert.equal(artifact.status, 'finalized');
    assert.equal((await readFile(path.join(rootDir, artifact.relativePath))).byteLength, 3);
    assert.equal((await store.getArtifact(artifact.id)).id, artifact.id);
    assert.equal((await store.listArtifacts({ deviceId: 'device-1' })).length, 1);
  } finally {
    await rm(rootDir, { recursive: true, force: true });
  }
});
