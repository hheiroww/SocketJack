# HTTP asset tooling

Run the tool with Node.js 20 or later. It uses the built-in compression and crypto modules and does not need an npm install.

## Prepare public assets

Use the public output of your site build as the root. List the files or directory trees you have approved for static serving. Keep private configuration, uploaded data, build metadata and source maps outside these inputs.

~~~sh
node tools/HttpAssets/optimize.mjs prepare --root dist --manifest build/http-assets.json \
  --include assets --include favicon.svg
~~~

Create the manifest's parent directory before running the tool. The manifest belongs outside the public root. The tool records each selected asset's size and full SHA-256 and writes smaller Brotli and gzip variants next to eligible originals. It skips hidden paths and symbolic links and leaves the original assets intact. Text assets between 1 KiB and 16 MiB are candidates for compression.

Reuse the existing manifest for updates within the same isolated build directory. A different sidecar is replaced only when its previous bytes match that manifest. When a new build omits a sidecar, the new manifest excludes it. Files retained from an earlier build should be handled by the site's staging and retention workflow.

Use the first 16 lowercase hexadecimal characters of the identity SHA-256 as the query value for a versioned asset. For example, if the identity digest starts with `0123456789abcdef`, the URL is `/assets/site.css?v=0123456789abcdef`. Generate asset URLs from the manifest in your site templates or application code. Rebuild the assets and update their references together. Enable SocketJack's static asset optimizations on the appropriate explicit public disk mappings and deploy the content, sidecars and references as a single release.

~~~csharp
var assets = HttpStaticAssets.Load("dist", "build/http-assets.json");
server.MapDirectory("/assets", "dist/assets");
server.StaticAssets = assets;
string stylesheet = "/assets/site.css?" + assets.GetVersionQuery("assets/site.css");
~~~

Load the complete manifest before enabling the snapshot. This verifies file hashes and confirms each compressed variant decodes to its original asset. The default memory budget is 64 MiB for originals and compressed representations combined. Replace `StaticAssets` with a newly verified snapshot when deploying a new generation.

On public GET and HEAD requests with the matching version query, the HTTP server sends `Cache-Control`, `Expires`, a representation-specific ETag and `Vary: Accept-Encoding`. It serves a verified Brotli, gzip or identity representation according to the request's accepted encodings. Keep PHP, generated pages, uploads, and account-dependent files out of the public asset mapping. Static requests with cookies, authorization, range headers, or an unmatched version continue through the regular static handler.

## Preload critical resources

Add a response hint when the document will use an asset immediately but the browser may otherwise discover it late. Use the exact URL from the consuming element so the preload can be reused.

~~~csharp
response.PreloadResource(stylesheet, HttpPreloadDestination.Style);
response.PreloadModule("/assets/home.mjs");
~~~

For other HTTP stacks, `HttpResourceHints.Preload` and `ModulePreload` return values to append to the document's HTTP `Link` header. Font preloads include anonymous CORS, and image, stylesheet and classic script preloads can opt into the same mode as their consuming elements. Limit hints to measured rendering dependencies. Extra requests can compete with the assets the page needs to paint. [HTML resource hints](https://html.spec.whatwg.org/multipage/links.html#link-type-preload)

## Audit a staged page

~~~sh
node tools/HttpAssets/optimize.mjs audit --root dist --page index.html --page docs/index.html \
  --requests 20 --script-kib 150 --image-kib 96 --json
~~~

Audits read HTML and referenced local stylesheets. They flag potentially blocking resource tags, duplicate script or stylesheet URLs, unreserved image and frame geometry, conflicting image priorities, large image fallbacks, font-display rules and configured request and uncompressed script budgets. Repeated identical resource URLs count once toward the request budget. The estimates cover direct markup references and local files encountered by the audit. Responsive candidate selection, transferred bytes, runtime-generated requests, script coverage, layout shifts and the actual LCP element require a browser recording.

When a browser recording identifies a local image as LCP, add `--lcp-image assets/hero.webp` to an audit of one page. The report checks for that asset in `img src` and image preload `href` and reviews fetch priority and lazy loading. Check responsive source selection separately.

Use Chrome DevTools or Lighthouse on the deployed page to validate the changes at representative viewport sizes. Use Coverage to identify unused JavaScript before splitting or removing it. [Chrome Performance Insights](https://developer.chrome.com/docs/performance/insights/)

## Bundle where the page benefits

Keep entry points explicit. For CSS bundles, create an entry stylesheet that imports the stylesheets in their required order. Use your existing bundler to resolve relative `url()` references and copy font or image dependencies. For example, a project that already pins esbuild in its lockfile can add this command to its build workflow.

~~~sh
npm exec -- esbuild frontend/site.css --bundle --minify --outdir=dist/assets \
  --loader:.woff2=file --loader:.png=file --loader:.svg=file
~~~

Update the page to reference the CSS bundle after verifying the cascade, media conditions and asset URLs. Keep classic global scripts in their required order. For an existing module entry with declared imports, bundle and minify it with the same pinned tool.

~~~sh
npm exec -- esbuild frontend/main.mjs --bundle --minify --format=esm --outdir=dist/assets
~~~

Run `prepare` after bundling and `audit` against the rendered HTML. [esbuild CSS bundling](https://esbuild.github.io/content-types/#css)
