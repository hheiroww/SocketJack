import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { bundleStyles, resolveRoot } from './assets.mjs';

test('stylesheet bundling preserves order, relative URLs and deterministic output', async () => {
  const root = await fixture();
  const first = '@layer base { .hero { background: url(./hero.svg); } }';
  const second = '.hero { color: rebeccapurple; content: "@import"; } /* ends cleanly */';
  await writeFile(path.join(root, 'base.css'), first);
  await writeFile(path.join(root, 'hero.css'), second);
  const expected = first + '\n' + second + '\n';
  const bundle = await bundleStyles(root, ['base.css', 'hero.css'], 'home.css');
  assert.equal(await readFile(path.join(root, bundle.path), 'utf8'), expected);
  assert.equal(bundle.sha256, createHash('sha256').update(expected).digest('hex'));
  assert.equal(bundle.path, 'home.' + bundle.sha256.slice(0, 16) + '.css');
  assert.deepEqual(await bundleStyles(root, ['base.css', 'hero.css'], 'home.css'), bundle);
  assert.notEqual((await bundleStyles(root, ['hero.css', 'base.css'], 'home.css')).path, bundle.path);
  await writeFile(path.join(root, bundle.path), 'foreign file');
  await assert.rejects(bundleStyles(root, ['base.css', 'hero.css'], 'home.css'), /different content/);
  assert.equal(await readFile(path.join(root, bundle.path), 'utf8'), 'foreign file');
});

test('stylesheet bundling rejects cross-directory URLs and unsafe concatenation boundaries', async () => {
  const root = await fixture();
  await mkdir(path.join(root, 'nested'));
  await writeFile(path.join(root, 'nested', 'base.css'), '.hero { background: url(pic.png) }');
  await assert.rejects(bundleStyles(root, ['nested/base.css'], 'home.css'), /output directory/);
  const invalid = [
    ["@import 'other.css';", /imports/],
    ["@i\\6d port 'other.css';", /escaped at-rule/],
    ['@namespace "http://example.test/ns";', /namespaces/],
    ['@charset "utf-8";', /charset/],
    ['.hero { color: red', /unfinished/],
    ['.hero {} /* missing end', /unfinished/],
    ['.hero {} .dangling', /unfinished/],
  ];
  for (const [css, message] of invalid) {
    await writeFile(path.join(root, 'incomplete.css'), css);
    await assert.rejects(bundleStyles(root, ['incomplete.css'], 'home.css'), message);
  }
});

async function fixture() {
  const parent = process.env.SOCKETJACK_HTTP_ASSET_TEST_ROOT || tmpdir();
  return resolveRoot(await mkdtemp(path.join(parent, 'socketjack-css-')));
}
