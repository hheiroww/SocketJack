import { createHash, randomUUID } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { lstat, readFile, readdir, realpath, rename, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { brotliCompress, constants, gzip } from 'node:zlib';

const compressible = new Set([
  '.css', '.html', '.htm', '.js', '.mjs', '.json', '.svg', '.txt', '.wasm', '.webmanifest', '.xml',
]);
const assets = new Set([...compressible,
  '.avif', '.gif', '.ico', '.jpeg', '.jpg', '.otf', '.png', '.ttf', '.webp', '.woff', '.woff2',
]);
const encoders = [
  ['br', '.br', promisify(brotliCompress), { params: { [constants.BROTLI_PARAM_QUALITY]: 8 } }],
  ['gzip', '.gz', promisify(gzip), { level: constants.Z_BEST_COMPRESSION }],
];
const minimumBytes = 1024;
const maximumBytes = 16 * 1024 * 1024;

export function validAssetPath(relative) {
  return typeof relative === 'string' && relative.length > 0 && relative.split('/').every(part =>
    part.length > 0 && !part.startsWith('.') && !/[. ]$/u.test(part) && !/[\\:%?#<>"|*\x00-\x1f\x7f]/u.test(part) &&
    !/^(?:aux|con|nul|prn|com[1-9]|lpt[1-9])(?:\.|$)/iu.test(part));
}

function contains(root, candidate) {
  const relative = path.relative(root, candidate);
  return relative === '' || (!path.isAbsolute(relative) && relative !== '..' && !relative.startsWith('..' + path.sep));
}

async function statIfPresent(file) {
  try {
    return await lstat(file);
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

export async function localEntry(root, relative) {
  if (!validAssetPath(relative)) return null;
  let file = root;
  let stat;
  for (const part of relative.split('/')) {
    if (stat && !stat.isDirectory()) return null;
    file = path.join(file, part);
    stat = await statIfPresent(file);
    if (!stat || stat.isSymbolicLink()) return null;
  }
  return { file, stat };
}

export async function resolveRoot(value) {
  const root = await realpath(value);
  if (!(await lstat(root)).isDirectory()) throw new Error('The public root must be a directory.');
  return root;
}

export async function bundleStyles(root, includes, output) {
  if (!validAssetPath(output) || !output.endsWith('.css')) {
    throw new Error('Choose a relative .css output name.');
  }
  const directory = path.posix.dirname(output);
  const decoder = new TextDecoder('utf-8', { fatal: true });
  const parts = [];
  let size = 0;

  for (const relative of includes) {
    const input = await localEntry(root, relative);
    if (!input?.stat.isFile() || !relative.endsWith('.css') || path.posix.dirname(relative) !== directory) {
      throw new Error('Bundle only regular .css files from the output directory: ' + relative);
    }
    if (size + input.stat.size > maximumBytes) throw new Error('CSS input exceeds the 16 MiB bundle limit.');
    const bytes = await readFile(input.file);
    size += bytes.length;
    if (size > maximumBytes) throw new Error('CSS input exceeds the 16 MiB bundle limit.');
    const css = decoder.decode(bytes);
    assertIndependentStylesheet(css, relative);
    parts.push(css);
  }
  if (!parts.length) throw new Error('Choose at least one stylesheet.');

  const bytes = Buffer.from(parts.join('\n') + '\n', 'utf-8');
  const sha256 = digest(bytes);
  const relative = output.slice(0, -4) + '.' + sha256.slice(0, 16) + '.css';
  const destination = path.join(root, ...relative.split('/'));
  const prior = await statIfPresent(destination);
  if (prior) {
    if (!prior.isFile() || prior.isSymbolicLink() || prior.size !== bytes.length ||
        (await hashFile(destination)).sha256 !== sha256) {
      throw new Error('Bundle path already holds different content: ' + relative);
    }
  } else {
    await atomicWrite(destination, bytes, false);
  }
  return { path: relative, length: bytes.length, sha256 };
}

function assertIndependentStylesheet(css, file) {
  const closer = new Map([['{', '}'], ['(', ')'], ['[', ']']]);
  const stack = [];
  const doubleQuote = /"(?:\\(?:\r\n|[\s\S])|[^"\\\r\n\f])*"/y;
  const singleQuote = /'(?:\\(?:\r\n|[\s\S])|[^'\\\r\n\f])*'/y;
  const ruleName = /[a-z0-9_-]*/iy;
  let complete = true;
  const incomplete = () => new Error('CSS has an unfinished token or rule: ' + file);

  for (let offset = 0; offset < css.length;) {
    const char = css[offset++];
    if (/[ \t\r\n\f]/u.test(char)) continue;
    if (char === '/' && css[offset] === '*') {
      const end = css.indexOf('*/', offset + 1);
      if (end < 0) throw incomplete();
      offset = end + 2;
      continue;
    }
    if (char === '"' || char === "'") {
      const quoted = char === '"' ? doubleQuote : singleQuote;
      quoted.lastIndex = offset - 1;
      if (!quoted.exec(css)) throw incomplete();
      offset = quoted.lastIndex;
    } else if (char === '\\') {
      if (offset === css.length) throw incomplete();
      offset++;
    } else if (char === '@') {
      ruleName.lastIndex = offset;
      const name = ruleName.exec(css)[0].toLowerCase();
      offset = ruleName.lastIndex;
      if (css[offset] === '\\' || name === 'import' || name === 'namespace' || name === 'charset') {
        throw new Error('Resolve CSS imports, namespaces, charset rules and escaped at-rule names first: ' + file);
      }
    } else if (closer.has(char)) {
      stack.push(closer.get(char));
    } else if (char === '}' || char === ')' || char === ']') {
      if (stack.pop() !== char) throw incomplete();
    }
    if (stack.length === 0) complete = char === '}' || char === ';';
  }
  if (stack.length || !complete) throw incomplete();
}

async function findFiles(root, includes) {
  const candidates = new Set();
  for (const include of includes) {
    const entry = await localEntry(root, include);
    if (!entry) throw new Error('An include is missing or contains a disallowed path or symbolic link: ' + include);
    if (entry.stat.isDirectory()) {
      await visit(include, entry.file);
    } else if (entry.stat.isFile() && assets.has(path.extname(include).toLowerCase())) {
      candidates.add(include);
    } else {
      throw new Error('The include is not a supported public asset: ' + include);
    }
  }
  return [...candidates].sort();

  async function visit(prefix, directory) {
    for (const item of await readdir(directory, { withFileTypes: true })) {
      const relative = prefix + '/' + item.name;
      if (!validAssetPath(relative) || item.isSymbolicLink()) continue;
      if (item.isDirectory()) {
        await visit(relative, path.join(directory, item.name));
      } else if (item.isFile() && assets.has(path.extname(item.name).toLowerCase())) {
        candidates.add(relative);
      }
    }
  }
}

const digest = bytes => createHash('sha256').update(bytes).digest('hex');

async function hashFile(file) {
  const hash = createHash('sha256');
  let length = 0;
  for await (const chunk of createReadStream(file)) {
    hash.update(chunk);
    length += chunk.length;
  }
  return { length, sha256: hash.digest('hex') };
}

async function atomicWrite(destination, content, replace) {
  const temp = path.join(path.dirname(destination), '.socketjack-' + randomUUID() + '.tmp');
  await writeFile(temp, content, { flag: 'wx' });
  try {
    if (!replace && (await statIfPresent(destination))) {
      throw new Error('The output appeared during preparation. Retry in an isolated staging directory.');
    }
    await rename(temp, destination);
  } catch (error) {
    await unlink(temp).catch(() => {});
    throw error;
  }
}

async function writeSidecar(root, relative, content, previous) {
  const file = path.join(root, ...relative.split('/'));
  const stat = await statIfPresent(file);
  const result = { path: relative, sha256: digest(content), length: content.length };
  if (stat) {
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('The sidecar destination is not a normal file: ' + relative);
    const existing = await hashFile(file);
    if (existing.sha256 === result.sha256 && existing.length === result.length) return result;
    if (previous?.path !== relative || existing.sha256 !== previous.sha256 || existing.length !== previous.length) {
      throw new Error('The sidecar is not owned by the previous manifest: ' + relative);
    }
  }
  await atomicWrite(file, content, Boolean(stat));
  return result;
}

async function readPreviousManifest(file) {
  const stat = await statIfPresent(file);
  if (!stat) return null;
  if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('The output manifest must be a normal file.');
  const previous = JSON.parse(await readFile(file, 'utf8'));
  if (previous?.version !== 1 || !Array.isArray(previous.assets)) {
    throw new Error('Refusing to replace a file that is not a SocketJack version 1 asset manifest.');
  }
  return new Map(previous.assets.map(entry => [entry.path, entry.encodings]));
}

export async function prepare(root, includes, manifestPath) {
  const destination = path.join(await realpath(path.dirname(path.resolve(manifestPath))), path.basename(manifestPath));
  if (contains(root, destination)) throw new Error('Write the manifest outside the public root.');
  const previous = await readPreviousManifest(destination);
  const files = await findFiles(root, includes);
  if (!files.length) throw new Error('No supported public assets matched the includes.');

  const entries = [];
  let sidecars = 0;
  for (const relative of files) {
    const source = await localEntry(root, relative);
    if (!source?.stat.isFile()) throw new Error('A selected asset changed during preparation: ' + relative);
    const variants = {};
    let identity;
    if (compressible.has(path.extname(relative).toLowerCase()) && source.stat.size >= minimumBytes && source.stat.size <= maximumBytes) {
      const raw = await readFile(source.file);
      identity = { length: raw.length, sha256: digest(raw) };
      for (const [encoding, suffix, encode, options] of encoders) {
        const encoded = await encode(raw, options);
        if (encoded.length >= raw.length) continue;
        variants[encoding] = await writeSidecar(root, relative + suffix, encoded, previous?.get(relative)?.[encoding]);
        sidecars++;
      }
    } else {
      identity = await hashFile(source.file);
    }
    entries.push({ path: relative, sha256: identity.sha256, length: identity.length, encodings: variants });
  }

  await atomicWrite(destination, JSON.stringify({ version: 1, assets: entries }, null, 2) + '\n', Boolean(previous));
  return { assets: entries.length, sidecars };
}
