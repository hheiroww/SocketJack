import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { localEntry, validAssetPath } from './assets.mjs';

const auditOrigin = 'https://public-assets.invalid';
const tags = /<!--[\s\S]*?-->|<(script|style|textarea|title|template|noscript)\b(?:[^>"']|"[^"]*"|'[^']*')*>(?:[\s\S]*?)<\/\1\s*>|<\/?[a-z][\w:-]*(?:[^>"']|"[^"]*"|'[^']*')*>/gi;
const opening = /^<(\/?)([a-z][\w:-]*)((?:[^>"']|"[^"]*"|'[^']*')*)>/i;
const attributes = /([^\s"'<>/=]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'=<>]+)))?/g;
const maxPageBytes = 8 * 1024 * 1024;
const maxStylesheetBytes = 2 * 1024 * 1024;

function decodeAttribute(value) {
  const entities = { amp: '&', quot: '"', apos: "'", lt: '<', gt: '>' };
  return value.replace(/&(#x[0-9a-f]+|#[0-9]+|amp|quot|apos|lt|gt);/gi, (match, entity) => {
    if (!entity.startsWith('#')) return entities[entity.toLowerCase()] ?? match;
    const code = entity[1].toLowerCase() === 'x' ? Number.parseInt(entity.slice(2), 16) : Number.parseInt(entity.slice(1), 10);
    return code > 0 && code <= 0x10ffff ? String.fromCodePoint(code) : match;
  });
}

function localPath(url) {
  if (url.origin !== auditOrigin) return null;
  try {
    const parts = url.pathname.slice(1).split('/').map(decodeURIComponent);
    if (parts.some(part => part.includes('/'))) return null;
    const candidate = parts.join('/');
    return validAssetPath(candidate) ? candidate : null;
  } catch (error) {
    if (!(error instanceof URIError)) throw error;
    return null;
  }
}

function resourceUrl(value, base) {
  if (!value || value.trim().startsWith('#')) return null;
  try {
    const url = new URL(value, base);
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
    url.hash = '';
    return url;
  } catch (error) {
    if (!(error instanceof TypeError)) throw error;
    return null;
  }
}

function executableScript(type) {
  return !type || /^(?:module|(?:text|application)\/(?:java|ecma)script)$/i.test(type.trim());
}

function hasDimension(value) {
  return /^\d+$/u.test(value ?? '') && Number(value) > 0;
}

function readAttributes(source) {
  const result = new Map();
  for (const match of source.matchAll(attributes)) {
    const name = match[1].toLowerCase();
    if (!result.has(name)) result.set(name, decodeAttribute(match[2] ?? match[3] ?? match[4] ?? ''));
  }
  return result;
}

export async function audit(root, pages, budget) {
  if (budget.lcpImage && (new Set(pages).size !== 1 || !(await localEntry(root, budget.lcpImage))?.stat.isFile())) {
    throw new Error('Use --lcp-image with one page and a regular image file relative to the public root.');
  }
  const reports = [];
  for (const page of [...new Set(pages)]) {
    const entry = await localEntry(root, page);
    if (!entry?.stat.isFile() || !/\.html?$/iu.test(page)) {
      throw new Error('The audit page must be a regular HTML file within the public root: ' + page);
    }
    if (entry.stat.size > maxPageBytes) throw new Error('The selected HTML page exceeds the audit size limit: ' + page);
    reports.push(await auditPage(root, page, await readFile(entry.file, 'utf8'), budget));
  }
  return reports;
}

async function auditPage(root, page, html, budget) {
  const pageUrl = new URL('/' + page.split('/').map(encodeURIComponent).join('/'), auditOrigin);
  let baseUrl = pageUrl;
  let firstBaseSeen = false;
  let line = 1;
  let nextLine = html.indexOf('\n');
  let highPriorityImages = 0;
  let inlineScriptBytes = 0;
  let measuredLcpImageFound = false;
  const requests = new Map();
  const findings = [];
  const duplicateKeys = new Set();

  for (const tag of html.matchAll(tags)) {
    if (tag[0].startsWith('<!--')) continue;
    const match = tag[0].match(opening);
    if (!match || match[1]) continue;
    while (nextLine !== -1 && nextLine < tag.index) {
      line++;
      nextLine = html.indexOf('\n', nextLine + 1);
    }
    const name = match[2].toLowerCase();
    const attrs = readAttributes(match[3]);
    if (name === 'base' && !firstBaseSeen && attrs.has('href')) {
      firstBaseSeen = true;
      baseUrl = resourceUrl(attrs.get('href'), pageUrl) ?? pageUrl;
    } else if (name === 'script' && executableScript(attrs.get('type'))) {
      if (attrs.has('src')) {
        addRequest('script', attrs.get('src'));
        if (!attrs.has('defer') && !attrs.has('async') && !attrs.has('nomodule') && attrs.get('type')?.trim().toLowerCase() !== 'module') {
          hint('render-blocking-script', 'Classic script can block parsing. Review dependency order before using defer or async.');
        }
      } else {
        const body = tag[0].slice(match[0].length, tag[0].lastIndexOf('</'));
        inlineScriptBytes += Buffer.byteLength(body);
      }
    } else if (name === 'link') {
      const rel = (attrs.get('rel') ?? '').toLowerCase().split(/\s+/u);
      if (rel.includes('stylesheet')) {
        addRequest('stylesheet', attrs.get('href'));
        if (!rel.includes('alternate') && !/^\s*(?:print|not all)\s*$/iu.test(attrs.get('media') ?? '')) {
          hint('render-blocking-style', 'Review this stylesheet against the page’s critical rendering path and existing cascade order.');
        }
      } else if (rel.includes('preload') || rel.includes('modulepreload')) {
        const preload = addRequest('preload', attrs.get('href'));
        if (budget.lcpImage && preload?.path === budget.lcpImage && attrs.get('as')?.toLowerCase() === 'image') {
          measuredLcpImageFound = true;
          if (attrs.get('fetchpriority')?.toLowerCase() !== 'high') {
            hint('lcp-preload-priority', 'The selected LCP image is preloaded without high fetch priority.');
          }
        }
      }
    } else if (name === 'img' && (attrs.has('src') || attrs.has('srcset'))) {
      const image = addRequest('image', attrs.get('src'));
      if (image && attrs.has('srcset')) image.responsive = true;
      if (!hasDimension(attrs.get('width')) || !hasDimension(attrs.get('height'))) {
        hint('image-dimensions', 'Reserve image space with width and height attributes or a verified CSS aspect ratio.');
      }
      if (attrs.get('fetchpriority')?.toLowerCase() === 'high') {
        highPriorityImages++;
        if (attrs.get('loading')?.toLowerCase() === 'lazy') {
          hint('lcp-priority', 'A high-priority image also requests lazy loading. Check the measured LCP image before changing these attributes.');
        }
      }
      if (budget.lcpImage && image?.path === budget.lcpImage) {
        measuredLcpImageFound = true;
        if (attrs.get('fetchpriority')?.toLowerCase() !== 'high' || attrs.get('loading')?.toLowerCase() === 'lazy') {
          hint('measured-lcp-image', 'Give the measured LCP image high fetch priority and avoid lazy loading it.');
        }
      }
    } else if (name === 'iframe' && attrs.has('src')) {
      addRequest('frame', attrs.get('src'));
      if (!hasDimension(attrs.get('width')) || !hasDimension(attrs.get('height'))) {
        hint('frame-dimensions', 'Verify that the frame’s dimensions are reserved before it loads.');
      }
    }
  }

  let scriptBytes = inlineScriptBytes;
  let imageBytes = 0;
  let externalRequests = 0;
  for (const request of requests.values()) {
    if (!request.path) {
      externalRequests += request.external ? 1 : 0;
      continue;
    }
    const entry = await localEntry(root, request.path);
    if (!entry?.stat.isFile()) continue;
    request.bytes = entry.stat.size;
    if (request.kinds.has('script')) scriptBytes += request.bytes;
    if (request.kinds.has('image')) {
      imageBytes += request.bytes;
      if (request.bytes > budget.imageBytes) {
        hintAt(request.line, 'image-budget', 'Large image fallback (' + request.bytes + ' bytes). Review its displayed size and format.', request.path);
        if (!request.responsive) {
          hintAt(request.line, 'responsive-image', 'Large image fallback has no srcset on the img. Check picture sources and responsive variants.', request.path);
        }
      }
    }
    if (request.kinds.has('stylesheet') && /\.css$/iu.test(request.path) && request.bytes <= maxStylesheetBytes) {
      const css = (await readFile(entry.file, 'utf8')).replace(/\/\*[\s\S]*?\*\//g, '');
      if ([...css.matchAll(/@font-face\s*\{([^{}]*)\}/gi)].some(match => !/\bfont-display\s*:\s*(?:swap|fallback|optional)\b/iu.test(match[1]))) {
        hintAt(request.line, 'font-display', 'Review font-display for this stylesheet’s @font-face rules.', request.path);
      }
    }
  }
  if (highPriorityImages > 1) hintAt(1, 'competing-image-priority', 'Multiple images use high fetch priority. Confirm the measured LCP before prioritizing assets.');
  if (budget.lcpImage && !measuredLcpImageFound) {
    hintAt(1, 'lcp-discovery', 'The selected LCP image is absent from img src and image preload href. Check responsive sources and browser request discovery.', budget.lcpImage);
  }
  if (requests.size > budget.requests) hintAt(1, 'request-budget', 'Direct resource URL count exceeds the configured budget. Review duplicate imports, bundling, and page-specific resources.');
  if (scriptBytes > budget.scriptBytes) hintAt(1, 'script-budget', 'Local JavaScript exceeds the configured uncompressed byte budget. Use browser Coverage before removing or splitting code.');
  return {
    page,
    summary: { directUrls: requests.size, externalUrls: externalRequests, localScriptBytes: scriptBytes, localImageFallbackBytes: imageBytes },
    findings: findings.sort((first, second) => first.line - second.line),
  };

  function addRequest(kind, value) {
    const url = resourceUrl(value, baseUrl);
    if (!url) return null;
    let request = requests.get(url.href);
    if (!request) {
      request = { path: localPath(url), external: url.origin !== auditOrigin, kinds: new Set(), line };
      requests.set(url.href, request);
    }
    if (request.kinds.has(kind) && (kind === 'script' || kind === 'stylesheet')) {
      const key = kind + ':' + url.href;
      if (!duplicateKeys.has(key)) {
        hintAt(line, 'duplicate-' + kind, 'This resource URL appears in multiple ' + kind + ' tags. Review whether each inclusion is required.', request.path);
        duplicateKeys.add(key);
      }
    }
    request.kinds.add(kind);
    return request;
  }

  function hint(code, message) {
    hintAt(line, code, message);
  }

  function hintAt(location, code, message, asset = null) {
    findings.push(asset ? { line: location, code, message, asset } : { line: location, code, message });
  }
}
