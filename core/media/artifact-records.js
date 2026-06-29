import { createHash, randomUUID } from 'node:crypto';
import { createMediaRelativePath } from './scoped-paths.js';

export function createFinalizedArtifactRecord({ deviceId, kind, extension, byteLength, bytes, metadata = {} }) {
  if (!deviceId || typeof deviceId !== 'string') throw new TypeError('deviceId is required');
  if (!kind || typeof kind !== 'string') throw new TypeError('kind is required');
  if (!Number.isSafeInteger(byteLength) || byteLength < 0) throw new TypeError('byteLength must be a safe integer');

  const id = randomUUID();
  return {
    id,
    deviceId,
    kind,
    relativePath: createMediaRelativePath(id, extension),
    metadata,
    sizeBytes: byteLength,
    sha256: createHash('sha256').update(bytes).digest('hex'),
    status: 'finalized',
    createdAt: new Date().toISOString(),
    finalizedAt: new Date().toISOString(),
  };
}

export function addArtifactRecord(index, artifact) {
  if (!index || !Array.isArray(index.artifacts)) throw new TypeError('index.artifacts is required');
  if (index.artifacts.some((item) => item.id === artifact.id)) {
    throw new TypeError(`duplicate artifact id: ${artifact.id}`);
  }
  return { ...index, artifacts: [...index.artifacts, artifact] };
}

export function findArtifactRecord(index, id) {
  if (!index || !Array.isArray(index.artifacts)) throw new TypeError('index.artifacts is required');
  const artifact = index.artifacts.find((item) => item.id === id);
  if (!artifact) throw new Error(`media artifact ${id} not found`);
  return { ...artifact };
}
