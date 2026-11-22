import { prepare, resolveRoot } from './assets.mjs';
import { audit } from './audit.mjs';

const usage = [
  'SocketJack HTTP asset tooling',
  '',
  'node tools/HttpAssets/optimize.mjs prepare --root DIST --manifest MANIFEST --include FILE_OR_DIR [--include FILE_OR_DIR ...]',
  'node tools/HttpAssets/optimize.mjs audit --root DIST --page FILE [--page FILE ...] [--json]',
  '  Audit options: --requests 20 --script-kib 150 --image-kib 96 --lcp-image RELATIVE_PATH',
  '',
  'Inputs are relative to the public root and use forward slashes.',
  'Write the manifest outside the public root.',
].join('\n');

try {
  const [command, ...args] = process.argv.slice(2);
  if (!command || command === '--help' || (args.length === 1 && args[0] === '--help')) {
    console.log(usage);
  } else {
    const options = parse(command, args);
    const root = await resolveRoot(options.root);
    if (command === 'prepare') {
      const result = await prepare(root, options.include, options.manifest);
      console.log('Prepared ' + result.assets + ' assets and ' + result.sidecars + ' compressed sidecars.');
    } else {
      const reports = await audit(root, options.page, {
        requests: integer(options.requests, 20),
        scriptBytes: integer(options['script-kib'], 150) * 1024,
        imageBytes: integer(options['image-kib'], 96) * 1024,
        lcpImage: options['lcp-image'],
      });
      if (options.json) {
        console.log(JSON.stringify(reports, null, 2));
      } else {
        for (const report of reports) {
          const { directUrls, externalUrls, localScriptBytes, localImageFallbackBytes } = report.summary;
          console.log(report.page + '  ' + directUrls + ' direct resource URLs (' + externalUrls + ' external)' +
            ', ' + localScriptBytes + ' local JS bytes, ' + localImageFallbackBytes + ' local image fallback bytes');
          for (const finding of report.findings) {
            console.log('  line ' + finding.line + '  ' + finding.code + '  ' + finding.message +
              (finding.asset ? ' [' + finding.asset + ']' : ''));
          }
        }
      }
    }
  }
} catch (error) {
  console.error('SocketJack HTTP assets: ' + error.message);
  process.exitCode = 1;
}

function parse(command, args) {
  if (command !== 'prepare' && command !== 'audit') throw new Error('Choose prepare or audit. Use --help for syntax.');
  const repeated = command === 'prepare' ? 'include' : 'page';
  const names = command === 'prepare' ? new Set(['root', 'manifest']) : new Set(['root', 'requests', 'script-kib', 'image-kib', 'lcp-image']);
  const result = { [repeated]: [] };
  for (let index = 0; index < args.length; index++) {
    const arg = args[index];
    if (arg === '--json' && command === 'audit' && !result.json) {
      result.json = true;
      continue;
    }
    const key = arg.slice(2);
    if (!arg.startsWith('--') || (!names.has(key) && key !== repeated) || Object.hasOwn(result, key) && key !== repeated) {
      throw new Error('Unexpected or repeated argument. Use --help for syntax.');
    }
    const value = args[++index];
    if (!value || value.startsWith('--')) throw new Error('An argument is missing its value.');
    if (key === repeated) result[key].push(value);
    else result[key] = value;
  }

  if (!result.root || !result[repeated].length || (command === 'prepare' && !result.manifest)) {
    throw new Error('Required arguments are missing. Use --help for syntax.');
  }
  return result;
}

function integer(value, fallback) {
  if (value === undefined) return fallback;
  const number = Number(value);
  if (!/^[0-9]+$/u.test(value) || !Number.isSafeInteger(number) || number < 1 || number > 1_000_000) {
    throw new Error('Audit budgets must be positive integers no larger than 1000000.');
  }
  return number;
}
