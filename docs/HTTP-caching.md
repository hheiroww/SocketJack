# HTTP caching

SocketJack `HttpServer` and `MutableTcpServer.Http` cache only explicit static disk mappings (`MapDirectory` and `MapFile`). The same rules apply to the socket listener and `DispatchMappedHttpRequestAsync`.

Static GET and HEAD responses supply a SHA-256 content ETag and `Last-Modified`. They use `Cache-Control: public, max-age=0, must-revalidate`, so a browser may keep the bytes but must validate them before reuse. Matching `If-None-Match` returns `304` without the body. A content edit changes the ETag even when file length and modification time are preserved; an old ETag returns the new body. A deleted file returns the normal uncached error. `If-None-Match` takes precedence over `If-Modified-Since`.

Authenticated requests, requests carrying cookies or authorization headers, and explicit private policies use `private` instead of `public`. Access checks still execute before conditional validation. `CacheControl = "no-store"` disables reuse of static files too. Range downloads keep their normal range behavior.

Rendered pages, templates, API handlers, generated document-root responses, directory listings, generated ZIPs, errors and streaming routes use `no-store`. A `.js` URL or JavaScript MIME type alone does not make a handler static. Repeated identical dynamic output is never promoted into the HTTP pattern cache. `RouteFile` and `RouteString` reevaluate variables on every request; their legacy cache arguments remain accepted for source compatibility but do not cache rendered output. Streaming output cannot opt into a public cache through `ChunkedStream.SetHeader`.

Static validation hashes the current file through a shared-read stream, without retaining a second server-side copy of its contents. This avoids serving stale data from a file metadata cache, at the cost of reading the file when it is validated. Large files still stream to the caller. Browser byte reuse and a server-side RAM file cache are distinct; this policy implements HTTP conditional reuse. Advanced handlers that write an entire raw HTTP response directly remain responsible for their own headers.

Real HTTP listener and hosted-dispatch tests are in `SocketJack.Tests/HttpStaticCachingTests.cs`, including same-length edits with preserved timestamps, conditional requests, dynamic and templated content, private validation, cache opt-out, deletion and streaming output.
