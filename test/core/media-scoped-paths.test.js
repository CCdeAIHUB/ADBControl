import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { createMediaRelativePath, resolveMediaPath, sanitizeMediaExtension } from '../../core/media/scoped-paths.js';

test('media extension allowlist accepts known artifact extensions', () => {
  assert.equal(sanitizeMediaExtension('.MP4'), 'mp4');
  assert.equal(sanitizeMediaExtension('png'), 'png');
});

test('media extension allowlist rejects unknown extensions', () => {
  assert.throws(() => sanitizeMediaExtension('exe'), /unsupported media extension/);
});

test('media path resolver keeps artifacts inside media root', () => {
  const root = path.join(process.cwd(), 'tmp-media-root');
  const target = resolveMediaPath(root, 'artifact.mp4');
  assert.equal(target, path.resolve(root, 'artifact.mp4'));
});

test('media path resolver rejects traversal outside media root', () => {
  const root = path.join(process.cwd(), 'tmp-media-root');
  assert.throws(() => resolveMediaPath(root, '../outside.mp4'), /escapes media root/);
});

test('media relative path is generated from uuid and allowlisted extension', () => {
  const relative = createMediaRelativePath('00000000-0000-4000-8000-000000000000', 'h264');
  assert.equal(relative, '00000000-0000-4000-8000-000000000000.h264');
});
