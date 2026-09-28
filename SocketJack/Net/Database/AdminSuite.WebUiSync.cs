using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocketJack.Net.Database {
    internal sealed partial class AdminSuite {
        private sealed class WebUiLinkCode {
            public string UserName;
            public DateTime ExpiresUtc;
        }
        private sealed class WebUiGrant {
            public string UserName;
        }
        private sealed class WebUiSyncEntry {
            public string Id;
            public string Name;
            public string ContentType;
            public string Origin;
            public bool Publishable;
            public byte[] Data;
            public string DiskPath;
        }
        private sealed class WebUiPublishEntry {
            public string Id { get; set; }
            public string Base64 { get; set; }
            public string ContentType { get; set; }
            public string ExpectedRevision { get; set; }
        }

        private readonly ConcurrentDictionary<string, WebUiLinkCode> _webUiLinkCodes = new ConcurrentDictionary<string, WebUiLinkCode>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, WebUiGrant> _webUiGrants = new ConcurrentDictionary<string, WebUiGrant>(StringComparer.Ordinal);
        private readonly object _webUiLinkAttemptGate = new object();
        private DateTime _webUiLinkAttemptWindow = DateTime.UtcNow;
        private int _webUiLinkFailedAttempts;

        private object ApiHeirowWebUiLinkCode(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error)) return error;
            foreach (var item in _webUiLinkCodes.Where(item => item.Value.ExpiresUtc <= DateTime.UtcNow).ToArray()) _webUiLinkCodes.TryRemove(item.Key, out _);
            string code = WebUiRandomHex(5);
            var value = new WebUiLinkCode { UserName = session.Username, ExpiresUtc = DateTime.UtcNow.AddMinutes(5) };
            _webUiLinkCodes[code] = value;
            WriteAudit(session.Username, "heirowwebui.link-code", "source", value.ExpiresUtc.ToString("O"));
            return JsonOk(new { code, expiresUtc = value.ExpiresUtc });
        }

        private object ApiHeirowWebUiConnect(HttpRequest req) {
            string code = ReadBodyString(req, "code", "").Trim().ToUpperInvariant();
            lock (_webUiLinkAttemptGate) {
                if (DateTime.UtcNow - _webUiLinkAttemptWindow >= TimeSpan.FromMinutes(1)) { _webUiLinkAttemptWindow = DateTime.UtcNow; _webUiLinkFailedAttempts = 0; }
                if (_webUiLinkFailedAttempts >= 20) return JsonError(req, 429, "Link attempts are temporarily limited.");
                if (!_webUiLinkCodes.TryRemove(code, out var pending) || pending.ExpiresUtc <= DateTime.UtcNow) { _webUiLinkFailedAttempts++; return JsonError(req, 403, "The SocketJack link code is invalid or expired."); }
                string token = WebUiRandomHex(32);
                _webUiGrants[token] = new WebUiGrant { UserName = pending.UserName };
                return JsonOk(new { token, sourceName = "SocketJack website", connectedUtc = DateTime.UtcNow });
            }
        }

        private object ApiHeirowWebUiCatalog(HttpRequest req) {
            if (!TryGetWebUiGrant(req, out _, out var error)) return error;
            lock (PageSaveGate) {
                var entries = GetWebUiSyncEntries().Select(entry => new {
                    id = entry.Id,
                    name = entry.Name,
                    contentType = entry.ContentType,
                    revision = WebUiHash(entry.Data),
                    hash = WebUiHash(entry.Data),
                    length = entry.Data.LongLength,
                    publishable = entry.Publishable,
                    origin = entry.Origin
                }).OrderBy(entry => entry.id, StringComparer.OrdinalIgnoreCase).ToArray();
                return JsonOk(new { sourceName = "SocketJack website", entries });
            }
        }

        private object ApiHeirowWebUiContent(HttpRequest req) {
            if (!TryGetWebUiGrant(req, out _, out var error)) return error;
            lock (PageSaveGate) {
                string id = req.QueryParameters.GetValueOrDefault("id", "");
                WebUiSyncEntry entry = GetWebUiSyncEntries().FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
                if (entry == null) return JsonError(req, 404, "SocketJack source entry was not found.");
                string revision = WebUiHash(entry.Data);
                return JsonOk(new { id = entry.Id, base64 = Convert.ToBase64String(entry.Data), contentType = entry.ContentType, revision, hash = revision });
            }
        }

        private object ApiHeirowWebUiPublish(HttpRequest req) {
            if (!TryGetWebUiGrant(req, out var grant, out var error)) return error;
            List<WebUiPublishEntry> requested;
            try { requested = JsonSerializer.Deserialize<List<WebUiPublishEntry>>(req.Body ?? "[]", JsonOptions) ?? new List<WebUiPublishEntry>(); }
            catch (JsonException ex) { return JsonError(req, 400, ex.Message); }
            if (requested.Count == 0) return JsonError(req, 400, "Choose at least one source file to publish.");
            if (requested.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requested.Count) return JsonError(req, 400, "A publish batch cannot contain duplicate files.");
            lock (PageSaveGate) {
                var catalog = GetWebUiSyncEntries().ToDictionary(entry => entry.Id, StringComparer.Ordinal);
                var writes = new List<Tuple<WebUiSyncEntry, byte[]>>();
                foreach (WebUiPublishEntry item in requested) {
                    if (!catalog.TryGetValue(item.Id ?? "", out var entry)) return JsonError(req, 404, "SocketJack source entry was not found: " + item.Id);
                    if (!entry.Publishable || string.IsNullOrWhiteSpace(entry.DiskPath)) return JsonError(req, 403, "Compiled and database-only entries are read-only: " + item.Id);
                    if (!string.Equals(WebUiHash(entry.Data), item.ExpectedRevision, StringComparison.Ordinal)) return JsonError(req, 409, "The SocketJack source changed: " + item.Id);
                    try { writes.Add(Tuple.Create(entry, Convert.FromBase64String(item.Base64 ?? ""))); }
                    catch (FormatException) { return JsonError(req, 400, "Published content is not valid base64: " + item.Id); }
                }
                var temporaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var before = writes.ToDictionary(item => item.Item1.DiskPath, item => item.Item1.Data, StringComparer.OrdinalIgnoreCase);
                try {
                    foreach (var write in writes) { Directory.CreateDirectory(Path.GetDirectoryName(write.Item1.DiskPath)); string temporary = write.Item1.DiskPath + ".heirow-" + Guid.NewGuid().ToString("N"); File.WriteAllBytes(temporary, write.Item2); temporaries[write.Item1.DiskPath] = temporary; }
                    foreach (var write in writes) { if (!File.Exists(write.Item1.DiskPath) || WebUiHash(File.ReadAllBytes(write.Item1.DiskPath)) != WebUiHash(write.Item1.Data)) return JsonError(req, 409, "The SocketJack source changed while publishing: " + write.Item1.Id); }
                    foreach (var pair in temporaries) { File.Copy(pair.Key, pair.Key + ".heirow-recovery", true); File.Copy(pair.Value, pair.Key, true); }
                    WriteAudit(grant.UserName, "heirowwebui.publish", string.Join(",", requested.Select(item => item.Id)), requested.Count.ToString());
                } catch (Exception ex) {
                    foreach (var pair in before) { try { File.WriteAllBytes(pair.Key, pair.Value); } catch { } }
                    return JsonError(req, 500, "SocketJack Publish was rolled back: " + ex.Message);
                } finally { foreach (string temporary in temporaries.Values) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } } }
                var result = requested.Select(item => { WebUiSyncEntry entry = GetWebUiSyncEntries().First(value => value.Id == item.Id); string revision = WebUiHash(entry.Data); return new { id = item.Id, revision, hash = revision }; }).ToArray();
                return JsonOk(new { ok = true, entries = result });
            }
        }

        private object ApiHeirowWebUiRevoke(HttpRequest req) { _webUiGrants.TryRemove(WebUiBearer(req), out _); return JsonOk(new { ok = true }); }

        private List<WebUiSyncEntry> GetWebUiSyncEntries() {
            var result = new List<WebUiSyncEntry>();
            foreach (AdminPage page in GetPagesTable().Rows.Select(PageFromRow)) {
                byte[] html = Encoding.UTF8.GetBytes(page.SqlHtml ?? ""); string htmlPath = null;
                if (string.Equals(page.StorageMode, "disk", StringComparison.OrdinalIgnoreCase) && TryReadDiskContent(page.DirectoryPath, page.Route, out var diskBytes, out _, out _)) { html = diskBytes; htmlPath = WebUiDiskPath(page.DirectoryPath, page.Route); }
                string prefix = "pages/" + page.Id + "/page.";
                result.Add(new WebUiSyncEntry { Id = prefix + "html", Name = page.Route + " · HTML", ContentType = "text/html", Origin = htmlPath == null ? "database-page" : "source-page", Publishable = htmlPath != null, Data = html, DiskPath = htmlPath });
                result.Add(new WebUiSyncEntry { Id = prefix + "css", Name = page.Route + " · CSS", ContentType = "text/css", Origin = "database-page", Publishable = false, Data = Encoding.UTF8.GetBytes(page.Css ?? "") });
                result.Add(new WebUiSyncEntry { Id = prefix + "js", Name = page.Route + " · JavaScript", ContentType = "text/javascript", Origin = "database-page", Publishable = false, Data = Encoding.UTF8.GetBytes(page.Js ?? "") });
                result.Add(new WebUiSyncEntry { Id = prefix + "heirowwebui", Name = page.Route + " · designer metadata", ContentType = "application/json", Origin = "database-page", Publishable = false, Data = Encoding.UTF8.GetBytes(page.DesignerMetadata ?? "") });
            }
            foreach (AdminAsset asset in GetAssetsTable().Rows.Select(AssetFromRow)) {
                byte[] data = Encoding.UTF8.GetBytes(asset.Content ?? ""); string diskPath = null;
                if (string.Equals(asset.StorageMode, "disk", StringComparison.OrdinalIgnoreCase) && TryReadDiskContent(asset.DirectoryPath, asset.Name, out var diskBytes, out _, out _)) { data = diskBytes; diskPath = WebUiDiskPath(asset.DirectoryPath, asset.Name); }
                result.Add(new WebUiSyncEntry { Id = "assets/" + asset.Id + "/" + asset.Name, Name = asset.Name, ContentType = string.IsNullOrWhiteSpace(asset.ContentType) ? HttpServer.GetMimeType(asset.Name) : asset.ContentType, Origin = diskPath == null ? "database-asset" : "source-asset", Publishable = diskPath != null, Data = data, DiskPath = diskPath });
            }
            Assembly assembly = typeof(HtmlPageResources).Assembly;
            foreach (string resourceName in assembly.GetManifestResourceNames().Where(name => name.StartsWith("SocketJack.Html.", StringComparison.Ordinal) && !name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase))) {
                using Stream stream = assembly.GetManifestResourceStream(resourceName); if (stream == null) continue; using var memory = new MemoryStream(); stream.CopyTo(memory); string name = resourceName.Substring("SocketJack.Html.".Length);
                result.Add(new WebUiSyncEntry { Id = "assembly/" + name, Name = name, ContentType = HttpServer.GetMimeType(name), Origin = "assembly", Publishable = false, Data = memory.ToArray() });
            }
            return result;
        }

        private bool TryGetWebUiGrant(HttpRequest req, out WebUiGrant grant, out object error) { if (!_webUiGrants.TryGetValue(WebUiBearer(req), out grant)) { error = JsonError(req, 403, "The SocketJack source connection is invalid or revoked."); return false; } error = null; return true; }
        private static string WebUiDiskPath(string configuredPath, string route) { string fullPath = Path.GetFullPath(configuredPath); if (!Directory.Exists(fullPath)) return fullPath; string candidate = Path.GetFullPath(Path.Combine(fullPath, NormalizePageRoute(route).Replace('/', Path.DirectorySeparatorChar))); if (!IsPathInsideRoot(candidate, fullPath)) throw new UnauthorizedAccessException("Source path escapes its root."); return candidate; }
        private static string WebUiBearer(HttpRequest req) { req.Headers.TryGetValue("Authorization", out string value); return !string.IsNullOrWhiteSpace(value) && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value.Substring(7).Trim() : ""; }
        private static string WebUiRandomHex(int length) { byte[] bytes = new byte[length]; using RandomNumberGenerator random = RandomNumberGenerator.Create(); random.GetBytes(bytes); return BitConverter.ToString(bytes).Replace("-", ""); }
        private static string WebUiHash(byte[] bytes) { using SHA256 hash = SHA256.Create(); return BitConverter.ToString(hash.ComputeHash(bytes ?? Array.Empty<byte>())).Replace("-", "").ToLowerInvariant(); }
    }
}
