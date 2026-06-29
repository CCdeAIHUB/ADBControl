import { mkdir, readFile, rename, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { addArtifactRecord, createFinalizedArtifactRecord, findArtifactRecord } from './artifact-records.js';
import { resolveMediaPath } from './scoped-paths.js';

export class ScopedFileMediaStore {
  constructor({ rootDir }) {
    if (!rootDir) throw new TypeError('rootDir is required');
    this.rootDir = rootDir;
    this.indexPath = resolveMediaPath(rootDir, 'index.json');
  }

  async init() {
    await mkdir(this.rootDir, { recursive: true });
    try {
      await readFile(this.indexPath, 'utf8');
    } catch (error) {
      if (error.code !== 'ENOENT') throw error;
      await this.#saveIndex({ artifacts: [] });
    }
  }

  async saveCompleteArtifact({ deviceId, kind, extension, bytes, metadata = {} }) {
    await this.init();
    const buffer = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
    const artifact = createFinalizedArtifactRecord({
      deviceId,
      kind,
      extension,
      byteLength: buffer.byteLength,
      bytes: buffer,
      metadata,
    });

    const targetPath = resolveMediaPath(this.rootDir, artifact.relativePath);
    await writeFile(targetPath, buffer, { flag: 'wx' });

    const index = await this.#loadIndex();
    await this.#saveIndex(addArtifactRecord(index, artifact));
    return artifact;
  }

  async getArtifact(id) {
    return findArtifactRecord(await this.#loadIndex(), id);
  }

  async listArtifacts({ deviceId } = {}) {
    const index = await this.#loadIndex();
    return index.artifacts.filter((artifact) => !deviceId || artifact.deviceId === deviceId);
  }

  async #loadIndex() {
    await this.init();
    return JSON.parse(await readFile(this.indexPath, 'utf8'));
  }

  async #saveIndex(index) {
    await mkdir(this.rootDir, { recursive: true });
    const tempPath = path.join(this.rootDir, 'index.tmp');
    await writeFile(tempPath, `${JSON.stringify(index, null, 2)}\n`, 'utf8');
    await rename(tempPath, this.indexPath);
  }
}
