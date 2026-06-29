import path from 'node:path';

const EXTENSIONS = new Set(['png', 'mp4', 'h264', 'json']);

export function sanitizeMediaExtension(extension) {
  const normalized = String(extension ?? '').replace(/^\./, '').toLowerCase();
  if (!EXTENSIONS.has(normalized)) {
    throw new TypeError(`unsupported media extension: ${extension}`);
  }
  return normalized;
}

export function resolveMediaPath(rootDir, relativePath) {
  if (!rootDir) throw new TypeError('rootDir is required');
  if (!relativePath || typeof relativePath !== 'string') throw new TypeError('relativePath is required');
  if (path.isAbsolute(relativePath)) throw new TypeError('media path must be relative');

  const root = path.resolve(rootDir);
  const target = path.resolve(root, relativePath);
  const prefix = `${root}${path.sep}`;
  if (target !== root && !target.startsWith(prefix)) {
    throw new TypeError('media path escapes media root');
  }
  return target;
}

export function createMediaRelativePath(id, extension) {
  if (!/^[0-9a-f-]{36}$/i.test(id)) throw new TypeError('artifact id must be a UUID');
  return `${id}.${sanitizeMediaExtension(extension)}`;
}
