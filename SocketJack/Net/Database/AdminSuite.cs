using SocketJack.Net;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SocketJack.Net.Database {

    internal sealed partial class AdminSuite {
        private const string AdminDatabaseName = "SocketJack";
        private const string SettingsTableName = "SocketJackAdminSettings";
        private const string PagesTableName = "SocketJackAdminPages";
        private const string AssetsTableName = "SocketJackAdminAssets";
        private const string ControlsTableName = "SocketJackAdminControls";
        private const string CrudTableName = "SocketJackAdminCrudDefinitions";
        private const string AuditTableName = "SocketJackAdminAudit";
        private const string DesignerTableName = "SocketJackWebUiProjects";
        private static readonly object PageSaveGate = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        private readonly MutableTcpServer _server;
        private bool _registered;

        internal string BasePath { get; set; } = "/Admin";
        internal string SqlPath { get; set; } = "/sql";

        internal AdminSuite(MutableTcpServer server) {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        internal void Register() {
            if (_registered)
                return;
            _registered = true;

            EnsureStorage();
            string basePath = BasePath.TrimEnd('/');
            _server.Map("GET", basePath, (conn, req, ct) => ServeShell(req));
            _server.Map("GET", basePath + "/", (conn, req, ct) => ServeShell(req));
            _server.Map("GET", basePath + "/api/session", (conn, req, ct) => ApiSession(req));
            _server.Map("GET", basePath + "/api/settings", (conn, req, ct) => ApiSettings(req));
            _server.Map("POST", basePath + "/api/settings/save", (conn, req, ct) => ApiSettingsSave(req));
            _server.Map("POST", basePath + "/api/settings/override-all/preview", (conn, req, ct) => ApiOverrideAllPreview(req));
            _server.Map("POST", basePath + "/api/settings/override-all/apply", (conn, req, ct) => ApiOverrideAllApply(req));
            _server.Map("GET", basePath + "/api/pages/list", (conn, req, ct) => ApiPagesList(req));
            _server.Map("GET", basePath + "/api/pages/source", (conn, req, ct) => ApiPageSource(req));
            _server.Map("POST", basePath + "/api/pages/save", (conn, req, ct) => ApiPagesSave(req));
            _server.Map("POST", basePath + "/api/pages/delete", (conn, req, ct) => ApiPagesDelete(req));
            _server.Map("GET", basePath + "/api/assets/list", (conn, req, ct) => ApiAssetsList(req));
            _server.Map("POST", basePath + "/api/assets/save", (conn, req, ct) => ApiAssetsSave(req));
            _server.Map("POST", basePath + "/api/heirowwebui/link-code", (conn, req, ct) => ApiHeirowWebUiLinkCode(req));
            _server.Map("POST", basePath + "/api/socketjack/connect", (conn, req, ct) => ApiHeirowWebUiConnect(req));
            _server.Map("GET", basePath + "/api/socketjack/catalog", (conn, req, ct) => ApiHeirowWebUiCatalog(req));
            _server.Map("GET", basePath + "/api/socketjack/content", (conn, req, ct) => ApiHeirowWebUiContent(req));
            _server.Map("POST", basePath + "/api/socketjack/publish", (conn, req, ct) => ApiHeirowWebUiPublish(req));
            _server.Map("POST", basePath + "/api/socketjack/revoke", (conn, req, ct) => ApiHeirowWebUiRevoke(req));
            _server.Map("GET", basePath + "/api/controls/list", (conn, req, ct) => ApiControlsList(req));
            _server.Map("POST", basePath + "/api/controls/save", (conn, req, ct) => ApiControlsSave(req));
            _server.Map("GET", basePath + "/api/crud/list", (conn, req, ct) => ApiCrudList(req));
            _server.Map("POST", basePath + "/api/crud/save", (conn, req, ct) => ApiCrudSave(req));
            _server.Map("POST", basePath + "/api/crud/delete", (conn, req, ct) => ApiCrudDelete(req));
            _server.Map("GET", basePath + "/api/crud/client/*", (conn, req, ct) => ApiCrudClient(req));
            _server.Map("GET", basePath + "/api/crud/run/*", (conn, req, ct) => ApiCrudRun(req));
            _server.Map("POST", basePath + "/api/crud/run/*", (conn, req, ct) => ApiCrudRun(req));
            _server.Http.MapDocumentRoot(basePath + "/pages", ResolvePageDocument);
        }

        internal void Unregister() {
            if (!_registered)
                return;
            _registered = false;

            string basePath = BasePath.TrimEnd('/');
            foreach (var route in new[] {
                basePath, basePath + "/", basePath + "/api/session", basePath + "/api/settings",
                basePath + "/api/settings/save", basePath + "/api/settings/override-all/preview",
                basePath + "/api/settings/override-all/apply", basePath + "/api/pages/list",
                basePath + "/api/pages/save", basePath + "/api/pages/delete", basePath + "/api/assets/list",
                basePath + "/api/assets/save", basePath + "/api/heirowwebui/link-code", basePath + "/api/socketjack/connect", basePath + "/api/socketjack/catalog", basePath + "/api/socketjack/content", basePath + "/api/socketjack/publish", basePath + "/api/socketjack/revoke", basePath + "/api/controls/list", basePath + "/api/controls/save",
                basePath + "/api/crud/list", basePath + "/api/crud/save", basePath + "/api/crud/delete",
                basePath + "/api/crud/client/*", basePath + "/api/crud/run/*"
            }) {
                _server.RemoveRoute("GET", route);
                _server.RemoveRoute("POST", route);
            }
            _server.Http.RemoveDocumentRootMapping(basePath + "/pages");
        }

        private object ServeShell(HttpRequest req) {
            req.Context.ContentType = "text/html";
            string html = HtmlPageResources.GetHtml("Admin.html");
            if (string.IsNullOrWhiteSpace(html))
                html = "<!doctype html><html><body><h1>SocketJack Admin</h1><a href=\"/sql\">SQL Admin</a></body></html>";
            SqlAdminSessionContext session = null;
            _server.SqlAdminPanel?.TryGetSessionContext(req, out session);
            return html
                .Replace("$BasePath", EscapeHtml(BasePath.TrimEnd('/')))
                .Replace("$SqlPath", EscapeHtml(SqlPath.TrimEnd('/')))
                .Replace("$Username", EscapeHtml(session?.Username ?? ""))
                .Replace("$CurrentDatabase", EscapeHtml(session?.CurrentDatabase ?? "db"));
        }

        private object ApiSession(HttpRequest req) {
            req.Context.ContentType = "application/json";
            SqlAdminSessionContext session = null;
            bool ok = _server.SqlAdminPanel != null && _server.SqlAdminPanel.TryGetSessionContext(req, out session);
            return JsonSerializer.Serialize(new {
                authenticated = ok,
                username = ok ? session.Username : "",
                currentDatabase = ok ? session.CurrentDatabase : "db",
                adminPath = BasePath,
                sqlPath = SqlPath
            }, JsonOptions);
        }

        private object ApiSettings(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            req.Context.ContentType = "application/json";
            return JsonSerializer.Serialize(new {
                defaultStorageMode = GetSetting("defaultPageStorageMode", "sql"),
                adminPath = BasePath,
                sqlPath = SqlPath,
                username = session.Username
            }, JsonOptions);
        }

        private object ApiSettingsSave(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            string mode = NormalizeStorageMode(ReadBodyString(req, "defaultStorageMode", "sql"));
            SetSetting("defaultPageStorageMode", mode);
            WriteAudit(session.Username, "settings.save", "defaultPageStorageMode", mode);
            return JsonOk(new { success = true, defaultStorageMode = mode });
        }

        private object ApiOverrideAllPreview(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error))
                return error;
            string targetMode = NormalizeStorageMode(ReadBodyString(req, "targetStorageMode", ReadBodyString(req, "storageMode", "sql")));
            var pages = GetPagesTable().Rows.Select(PageFromRow).Where(p => !string.Equals(p.StorageMode, targetMode, StringComparison.OrdinalIgnoreCase)).ToList();
            var preview = pages.Select(p => new {
                id = p.Id,
                route = p.Route,
                from = p.StorageMode,
                to = targetMode,
                source = DescribeStorageSource(p, p.StorageMode),
                target = DescribeStorageSource(p, targetMode),
                missingTarget = IsTargetMissing(p, targetMode)
            }).ToList();
            return JsonOk(new { targetStorageMode = targetMode, affectedCount = preview.Count, pages = preview, countdownSeconds = 3 });
        }

        private object ApiOverrideAllApply(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            string targetMode = NormalizeStorageMode(ReadBodyString(req, "targetStorageMode", ReadBodyString(req, "storageMode", "sql")));
            var table = GetPagesTable();
            int affected = 0;
            for (int i = 0; i < table.Rows.Count; i++) {
                var p = PageFromRow(table.Rows[i]);
                if (!string.Equals(p.StorageMode, targetMode, StringComparison.OrdinalIgnoreCase)) {
                    p.StorageMode = targetMode;
                    p.UpdatedUtc = Now();
                    table.Rows[i] = PageToRow(p);
                    affected++;
                }
            }
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "settings.override-all", targetMode, affected.ToString(CultureInfo.InvariantCulture));
            return JsonOk(new { success = true, targetStorageMode = targetMode, affectedCount = affected });
        }

        private object ApiPageSource(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error)) return error;
            lock (PageSaveGate) {
                var page = GetPagesTable().Rows.Select(PageFromRow).FirstOrDefault(p => p.Id == req.QueryParameters.GetValueOrDefault("id", ""));
                if (page == null) return JsonError(req, 404, "Page not found.");
                string content = page.SqlHtml;
                if (page.StorageMode == "disk") {
                    if (!TryReadDiskContent(page.DirectoryPath, page.Route, out var bytes, out _, out _)) return JsonError(req, 404, "Source file not found.");
                    using (var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true)) content = reader.ReadToEnd();
                }
                return JsonOk(new { content, revision = page.Revision });
            }
        }

        private object ApiPagesList(HttpRequest req) { lock (PageSaveGate) return ApiPagesListCore(req); }
        private object ApiPagesListCore(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error))
                return error;
            return JsonOk(new { pages = GetPagesTable().Rows.Select(PageFromRow).OrderBy(p => p.Route).ToList() });
        }

        private object ApiPagesSave(HttpRequest req) {
            lock (PageSaveGate) return ApiPagesSaveCore(req);
        }

        private object ApiPagesSaveCore(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            var body = req.Body ?? "";
            var page = new AdminPage {
                Id = ReadJsonString(body, "id"),
                Route = NormalizePageRoute(ReadJsonString(body, "route")),
                Title = ReadJsonString(body, "title"),
                StorageMode = NormalizeStorageMode(ReadJsonString(body, "storageMode", GetSetting("defaultPageStorageMode", "sql"))),
                DirectoryPath = ReadJsonString(body, "directoryPath"),
                SqlHtml = ReadJsonString(body, "html", ReadJsonString(body, "sqlHtml", "")),
                Css = ReadJsonString(body, "css", ""),
                Js = ReadJsonString(body, "js", "")
            };
            if (string.IsNullOrWhiteSpace(page.Route))
                return JsonError(req, 400, "Page route is required.");
            if (string.IsNullOrWhiteSpace(page.Id))
                page.Id = "page_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(page.Title))
                page.Title = page.Route;

            var table = GetPagesTable();
            int existing = FindRowIndex(table, 0, page.Id);
            if (existing < 0)
                existing = FindRowIndex(table, 1, page.Route);
            string expectedRevision = ReadJsonString(body, "expectedRevision", "");
            if (!string.IsNullOrEmpty(expectedRevision) &&
                (existing < 0 || !string.Equals(PageFromRow(table.Rows[existing]).Revision, expectedRevision, StringComparison.Ordinal)))
                return JsonError(req, 409, "The page changed outside the designer. Reload before saving.");
            if (existing >= 0) page.DesignerMetadata = PageFromRow(table.Rows[existing]).DesignerMetadata;
            if (!string.IsNullOrEmpty(expectedRevision) && page.StorageMode == "disk") {
                using (var parsed = JsonDocument.Parse(body)) if (parsed.RootElement.TryGetProperty("html", out var sourceHtml)) {
                    string target = Path.GetFullPath(page.DirectoryPath);
                    if (Directory.Exists(target)) {
                        string candidate = Path.GetFullPath(Path.Combine(target, page.Route.Replace('/', Path.DirectorySeparatorChar)));
                        if (!IsPathInsideRoot(candidate, target)) return JsonError(req, 400, "Source path escapes its root.");
                        target = candidate;
                    }
                    if (!File.Exists(target)) return JsonError(req, 404, "Source file not found.");
                    var original = File.ReadAllBytes(target); string before; Encoding encoding;
                    using (var reader = new StreamReader(new MemoryStream(original), Encoding.UTF8, true)) { before = reader.ReadToEnd(); encoding = reader.CurrentEncoding; }
                    string after = sourceHtml.GetString() ?? "";
                    if (before != after) {
                        var preamble = encoding.GetPreamble(); bool hadPreamble = preamble.Length > 0 && original.Take(preamble.Length).SequenceEqual(preamble);
                        string temporary = target + ".heirow-" + Guid.NewGuid().ToString("N");
                        try { File.WriteAllBytes(temporary, (hadPreamble ? preamble : Array.Empty<byte>()).Concat(encoding.GetBytes(after)).ToArray()); File.Copy(target, target + ".heirow-recovery", true); File.Copy(temporary, target, true); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    }
                }
            }
            page.CreatedUtc = existing >= 0 ? GetRow(table.Rows[existing], 8) : Now();
            page.UpdatedUtc = Now();
            if (existing >= 0)
                table.Rows[existing] = PageToRow(page);
            else
                table.Rows.Add(PageToRow(page));
            using (var metadataBody = JsonDocument.Parse(body)) {
                if (metadataBody.RootElement.TryGetProperty("designerMetadata", out var metadata)) {
                    var designerTable = GetAdminDatabase().Tables[DesignerTableName];
                    int designerIndex = FindRowIndex(designerTable, 0, page.Id);
                    page.DesignerMetadata = metadata.ValueKind == JsonValueKind.String ? metadata.GetString() : metadata.GetRawText();
                    var row = new object[] { page.Id, page.DesignerMetadata ?? "", Now() };
                    if (designerIndex >= 0) designerTable.Rows[designerIndex] = row;
                    else designerTable.Rows.Add(row);
                }
            }
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "page.save", page.Route, page.StorageMode);
            return JsonOk(new { success = true, page });
        }

        private object ApiPagesDelete(HttpRequest req) { lock (PageSaveGate) return ApiPagesDeleteCore(req); }
        private object ApiPagesDeleteCore(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            string id = ReadBodyString(req, "id", "");
            var table = GetPagesTable();
            int index = FindRowIndex(table, 0, id);
            if (index < 0)
                return JsonError(req, 404, "Page not found.");
            string route = GetRow(table.Rows[index], 1);
            table.Rows.RemoveAt(index);
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "page.delete", route, id);
            return JsonOk(new { success = true });
        }

        private object ApiAssetsList(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error))
                return error;
            return JsonOk(new { assets = GetAssetsTable().Rows.Select(AssetFromRow).OrderBy(a => a.Name).ToList() });
        }

        private object ApiAssetsSave(HttpRequest req) { lock (PageSaveGate) return ApiAssetsSaveCore(req); }
        private object ApiAssetsSaveCore(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            var body = req.Body ?? "";
            var asset = new AdminAsset {
                Id = ReadJsonString(body, "id"),
                PageId = ReadJsonString(body, "pageId"),
                Name = NormalizePageRoute(ReadJsonString(body, "name")),
                Kind = ReadJsonString(body, "kind", "asset"),
                ContentType = ReadJsonString(body, "contentType"),
                StorageMode = NormalizeStorageMode(ReadJsonString(body, "storageMode", GetSetting("defaultPageStorageMode", "sql"))),
                DirectoryPath = ReadJsonString(body, "directoryPath"),
                Content = ReadJsonString(body, "content", "")
            };
            if (string.IsNullOrWhiteSpace(asset.Name))
                return JsonError(req, 400, "Asset name is required.");
            if (string.IsNullOrWhiteSpace(asset.Id))
                asset.Id = "asset_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(asset.ContentType))
                asset.ContentType = HttpServer.GetMimeType(asset.Name);
            var table = GetAssetsTable();
            int existing = FindRowIndex(table, 0, asset.Id);
            string expectedRevision = ReadJsonString(body, "expectedRevision", "");
            if (!string.IsNullOrEmpty(expectedRevision) && (existing < 0 || AssetFromRow(table.Rows[existing]).Revision != expectedRevision)) return JsonError(req, 409, "The asset changed outside the designer.");
            asset.CreatedUtc = existing >= 0 ? GetRow(table.Rows[existing], 8) : Now();
            asset.UpdatedUtc = Now();
            if (existing >= 0)
                table.Rows[existing] = AssetToRow(asset);
            else
                table.Rows.Add(AssetToRow(asset));
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "asset.save", asset.Name, asset.StorageMode);
            return JsonOk(new { success = true, asset });
        }

        private object ApiControlsList(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error))
                return error;
            return JsonOk(new { controls = GetControlsTable().Rows.Select(ControlFromRow).OrderBy(c => c.Name).ToList(), palette = ControlPalette() });
        }

        private object ApiControlsSave(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            var body = req.Body ?? "";
            var control = new AdminControl {
                Id = ReadJsonString(body, "id"),
                PageId = ReadJsonString(body, "pageId"),
                Type = NormalizeControlType(ReadJsonString(body, "type", "form")),
                Name = ReadJsonString(body, "name", "New Control"),
                SchemaJson = ReadJsonString(body, "schemaJson", ReadJsonString(body, "schema", "{}")),
                BindingJson = ReadJsonString(body, "bindingJson", ReadJsonString(body, "binding", "{}"))
            };
            if (string.IsNullOrWhiteSpace(control.Id))
                control.Id = "control_" + Guid.NewGuid().ToString("N");
            var table = GetControlsTable();
            int existing = FindRowIndex(table, 0, control.Id);
            control.CreatedUtc = existing >= 0 ? GetRow(table.Rows[existing], 6) : Now();
            control.UpdatedUtc = Now();
            if (existing >= 0)
                table.Rows[existing] = ControlToRow(control);
            else
                table.Rows.Add(ControlToRow(control));
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "control.save", control.Name, control.Type);
            return JsonOk(new { success = true, control });
        }

        private object ApiCrudList(HttpRequest req) {
            if (!RequireAdmin(req, out _, out var error))
                return error;
            return JsonOk(new { endpoints = GetCrudTable().Rows.Select(CrudFromRow).OrderBy(c => c.Name).ToList() });
        }

        private object ApiCrudSave(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            var body = req.Body ?? "";
            var def = new CrudDefinition {
                Id = ReadJsonString(body, "id"),
                Name = ReadJsonString(body, "name"),
                Database = ReadJsonString(body, "database", "db"),
                Table = ReadJsonString(body, "table"),
                KeyColumn = ReadJsonString(body, "keyColumn"),
                Route = NormalizeRouteSlug(ReadJsonString(body, "route")),
                Scopes = ReadJsonString(body, "scopes", "sql:read,sql:write"),
                InputSchema = ReadJsonString(body, "inputSchema"),
                Enabled = ReadJsonBool(body, "enabled", true)
            };
            if (string.IsNullOrWhiteSpace(def.Table))
                return JsonError(req, 400, "Target table is required.");
            if (!CanAccessDatabase(req, session, def.Database))
                return JsonError(req, 403, "Database access denied.");
            Database db = GetDatabase(def.Database);
            if (db == null || !db.Tables.TryGetValue(def.Table, out var targetTable))
                return JsonError(req, 404, "Target table not found.");
            if (string.IsNullOrWhiteSpace(def.KeyColumn))
                def.KeyColumn = targetTable.Columns.FirstOrDefault()?.Name ?? "Id";
            if (FindColumnIndex(targetTable, def.KeyColumn) < 0)
                return JsonError(req, 400, "Key column does not exist.");
            if (string.IsNullOrWhiteSpace(def.Id))
                def.Id = "crud_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(def.Name))
                def.Name = def.Table + " CRUD";
            if (string.IsNullOrWhiteSpace(def.Route))
                def.Route = NormalizeRouteSlug(def.Name);
            if (string.IsNullOrWhiteSpace(def.InputSchema))
                def.InputSchema = BuildInputSchema(targetTable, def.KeyColumn);

            var table = GetCrudTable();
            int existing = FindRowIndex(table, 0, def.Id);
            def.CreatedUtc = existing >= 0 ? GetRow(table.Rows[existing], 9) : Now();
            def.UpdatedUtc = Now();
            if (existing >= 0)
                table.Rows[existing] = CrudToRow(def);
            else
                table.Rows.Add(CrudToRow(def));
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "crud.save", def.Route, def.Database + "." + def.Table);
            return JsonOk(new { success = true, endpoint = def, clientUrl = BasePath.TrimEnd('/') + "/api/crud/client/" + Uri.EscapeDataString(def.Id) + ".js" });
        }

        private object ApiCrudDelete(HttpRequest req) {
            if (!RequireAdmin(req, out var session, out var error))
                return error;
            string id = ReadBodyString(req, "id", "");
            var table = GetCrudTable();
            int index = FindRowIndex(table, 0, id);
            if (index < 0)
                return JsonError(req, 404, "CRUD definition not found.");
            string route = GetRow(table.Rows[index], 5);
            table.Rows.RemoveAt(index);
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "crud.delete", route, id);
            return JsonOk(new { success = true });
        }

        private object ApiCrudClient(HttpRequest req) {
            if (!RequireSession(req, out _, out var error))
                return error;
            string id = FirstPathPart(req);
            if (id.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                id = id.Substring(0, id.Length - 3);
            var def = FindCrudDefinition(id);
            if (def == null)
                return JsonError(req, 404, "CRUD definition not found.");
            string js = GenerateCrudClient(def);
            return new FileResponse(Encoding.UTF8.GetBytes(js), "application/javascript", (def.Route ?? def.Id) + ".js");
        }

        private object ApiCrudRun(HttpRequest req) {
            if (!RequireSession(req, out var session, out var error))
                return error;
            string variable = FirstPathPart(req);
            string[] parts = variable.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return JsonError(req, 400, "CRUD route must include endpoint id and action.");
            var def = FindCrudDefinition(parts[0]);
            string action = parts[1].ToLowerInvariant();
            if (def == null || !def.Enabled)
                return JsonError(req, 404, "CRUD definition is disabled or missing.");
            if (!CanAccessDatabase(req, session, def.Database))
                return JsonError(req, 403, "Database access denied.");
            if (!CrudScopeAllows(def, IsWriteAction(action) ? "sql:write" : "sql:read"))
                return JsonError(req, 403, "CRUD scope does not allow this action.");
            var db = GetDatabase(def.Database);
            if (db == null || !db.Tables.TryGetValue(def.Table, out var table))
                return JsonError(req, 404, "Target table not found.");
            int keyIndex = FindColumnIndex(table, def.KeyColumn);
            if (keyIndex < 0)
                return JsonError(req, 400, "Configured key column does not exist.");

            try {
                switch (action) {
                    case "list":
                        return CrudList(req, table);
                    case "get":
                        return CrudGet(req, table, def, keyIndex);
                    case "create":
                        return CrudCreate(req, session, table, def);
                    case "update":
                        return CrudUpdate(req, session, table, def, keyIndex);
                    case "delete":
                        return CrudDelete(req, session, table, def, keyIndex);
                    default:
                        return JsonError(req, 400, "Unsupported CRUD action.");
                }
            } catch (Exception ex) {
                return JsonError(req, 400, ex.Message);
            }
        }

        private object CrudList(HttpRequest req, Table table) {
            int take = ParseInt(Query(req, "take"), 100);
            if (take <= 0 || take > 1000)
                take = 100;
            var rows = table.Rows.Take(take).Select(row => RowToObject(table, row)).ToList();
            return JsonOk(new { rows, totalRows = table.Rows.Count });
        }

        private object CrudGet(HttpRequest req, Table table, CrudDefinition def, int keyIndex) {
            string key = ReadKey(req, def);
            int rowIndex = FindRowIndex(table, keyIndex, key);
            if (rowIndex < 0)
                return JsonError(req, 404, "Row not found.");
            return JsonOk(new { row = RowToObject(table, table.Rows[rowIndex]) });
        }

        private object CrudCreate(HttpRequest req, SqlAdminSessionContext session, Table table, CrudDefinition def) {
            var values = ReadCrudValues(req);
            ValidateCrudValues(table, def, values, true);
            var row = new object[table.Columns.Count];
            foreach (var kv in values) {
                int index = FindColumnIndex(table, kv.Key);
                row[index] = ConvertValue(kv.Value, table.Columns[index].DataType);
            }
            table.Rows.Add(row);
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "crud.create", def.Route, def.Database + "." + def.Table);
            return JsonOk(new { success = true, row = RowToObject(table, row) });
        }

        private object CrudUpdate(HttpRequest req, SqlAdminSessionContext session, Table table, CrudDefinition def, int keyIndex) {
            string key = ReadKey(req, def);
            int rowIndex = FindRowIndex(table, keyIndex, key);
            if (rowIndex < 0)
                return JsonError(req, 404, "Row not found.");
            var values = ReadCrudValues(req);
            ValidateCrudValues(table, def, values, false);
            var row = table.Rows[rowIndex];
            if (row.Length < table.Columns.Count)
                Array.Resize(ref row, table.Columns.Count);
            foreach (var kv in values) {
                if (string.Equals(kv.Key, def.KeyColumn, StringComparison.OrdinalIgnoreCase))
                    continue;
                int index = FindColumnIndex(table, kv.Key);
                row[index] = ConvertValue(kv.Value, table.Columns[index].DataType);
            }
            table.Rows[rowIndex] = row;
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "crud.update", def.Route, key);
            return JsonOk(new { success = true, row = RowToObject(table, row) });
        }

        private object CrudDelete(HttpRequest req, SqlAdminSessionContext session, Table table, CrudDefinition def, int keyIndex) {
            string key = ReadKey(req, def);
            int rowIndex = FindRowIndex(table, keyIndex, key);
            if (rowIndex < 0)
                return JsonError(req, 404, "Row not found.");
            table.Rows.RemoveAt(rowIndex);
            DataServer.ScheduleSave();
            WriteAudit(session.Username, "crud.delete", def.Route, key);
            return JsonOk(new { success = true, rowsAffected = 1 });
        }

        private bool ResolvePageDocument(HttpRequest request, string relativePath, out HttpDocumentRootResponse response) {
            response = null;
            if (!RequireSession(request, out var session, out _)) {
                response = new HttpDocumentRootResponse {
                    Text = "{\"error\":\"SQL Admin session is required.\"}",
                    FileName = "unauthorized.json",
                    ContentType = "application/json"
                };
                request.Context.StatusCode = "401 Unauthorized";
                return true;
            }

            string route = NormalizePageRoute(string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath);
            var page = GetPagesTable().Rows.Select(PageFromRow).FirstOrDefault(p => string.Equals(p.Route, route, StringComparison.OrdinalIgnoreCase));
            if (page != null)
                return ResolvePage(request, session, page, route, out response);

            var asset = GetAssetsTable().Rows.Select(AssetFromRow).FirstOrDefault(a => string.Equals(a.Name, route, StringComparison.OrdinalIgnoreCase));
            if (asset != null)
                return ResolveAsset(asset, route, out response);

            return false;
        }

        private bool ResolvePage(HttpRequest request, SqlAdminSessionContext session, AdminPage page, string route, out HttpDocumentRootResponse response) {
            response = null;
            string extension = Path.GetExtension(route).ToLowerInvariant();
            if (string.Equals(page.StorageMode, "disk", StringComparison.OrdinalIgnoreCase)) {
                if (TryReadDiskContent(page.DirectoryPath, route, out var bytes, out var fileName, out var lastModified)) {
                    response = new HttpDocumentRootResponse {
                        Data = bytes,
                        FileName = fileName,
                        ContentType = HttpServer.GetMimeType(fileName),
                        LastModifiedUtc = lastModified
                    };
                    return true;
                }
                return false;
            }

            string content = extension == ".css" ? page.Css : extension == ".js" ? page.Js : page.SqlHtml;
            if (extension == ".html" || extension == ".htm" || string.IsNullOrWhiteSpace(extension))
                content = InjectContext(content, session, route);
            response = new HttpDocumentRootResponse {
                Text = content ?? "",
                FileName = string.IsNullOrWhiteSpace(route) ? "index.html" : Path.GetFileName(route),
                ContentType = HttpServer.GetMimeType(route)
            };
            return true;
        }

        private bool ResolveAsset(AdminAsset asset, string route, out HttpDocumentRootResponse response) {
            response = null;
            if (string.Equals(asset.StorageMode, "disk", StringComparison.OrdinalIgnoreCase)) {
                if (TryReadDiskContent(asset.DirectoryPath, route, out var bytes, out var fileName, out var lastModified)) {
                    response = new HttpDocumentRootResponse {
                        Data = bytes,
                        FileName = fileName,
                        ContentType = string.IsNullOrWhiteSpace(asset.ContentType) ? HttpServer.GetMimeType(fileName) : asset.ContentType,
                        LastModifiedUtc = lastModified
                    };
                    return true;
                }
                return false;
            }
            response = new HttpDocumentRootResponse {
                Text = asset.Content ?? "",
                FileName = Path.GetFileName(route),
                ContentType = string.IsNullOrWhiteSpace(asset.ContentType) ? HttpServer.GetMimeType(route) : asset.ContentType
            };
            return true;
        }

        private bool RequireAdmin(HttpRequest req, out SqlAdminSessionContext session, out string error) {
            if (!RequireSession(req, out session, out error))
                return false;
            if (!DataServer.IsSqlAdminAccount(session.Username) || _server.SqlAdminPanel == null || !_server.SqlAdminPanel.IsLocalSqlAdminRequest(req)) {
                error = JsonError(req, 403, "SocketJack Admin requires a local SQL administrator session.") as string;
                return false;
            }
            return true;
        }

        private bool RequireSession(HttpRequest req, out SqlAdminSessionContext session, out string error) {
            session = null;
            error = null;
            if (_server.SqlAdminPanel == null || !_server.SqlAdminPanel.TryGetSessionContext(req, out session)) {
                error = JsonError(req, 401, "SQL Admin session is required.") as string;
                return false;
            }
            return true;
        }

        private object JsonError(HttpRequest req, int statusCode, string message) {
            req.Context.ContentType = "application/json";
            req.Context.StatusCodeNumber = statusCode;
            req.Context.ReasonPhrase = ReasonPhrase(statusCode);
            return JsonSerializer.Serialize(new { error = message }, JsonOptions);
        }

        private object JsonOk(object value) {
            return JsonSerializer.Serialize(value, JsonOptions);
        }

        private DataServer DataServer => _server.GetOrCreateDataServer();

        private Database GetAdminDatabase() {
            var db = DataServer.Databases.GetOrAdd(AdminDatabaseName, name => new Database(name));
            return db;
        }

        private Database GetDatabase(string name) {
            if (string.IsNullOrWhiteSpace(name))
                name = "db";
            DataServer.Databases.TryGetValue(name, out var db);
            return db;
        }

        private void EnsureStorage() {
            var db = GetAdminDatabase();
            EnsureTable(db, SettingsTableName, new[] {
                ColumnSpec("Key"), ColumnSpec("Value"), ColumnSpec("UpdatedUtc", 80)
            });
            EnsureTable(db, PagesTableName, new[] {
                ColumnSpec("Id"), ColumnSpec("Route"), ColumnSpec("Title"), ColumnSpec("StorageMode", 20),
                ColumnSpec("DirectoryPath"), ColumnSpec("SqlHtml"), ColumnSpec("Css"), ColumnSpec("Js"),
                ColumnSpec("CreatedUtc", 80), ColumnSpec("UpdatedUtc", 80)
            });
            EnsureTable(db, AssetsTableName, new[] {
                ColumnSpec("Id"), ColumnSpec("PageId"), ColumnSpec("Name"), ColumnSpec("Kind", 40),
                ColumnSpec("ContentType", 120), ColumnSpec("StorageMode", 20), ColumnSpec("DirectoryPath"),
                ColumnSpec("Content"), ColumnSpec("CreatedUtc", 80), ColumnSpec("UpdatedUtc", 80)
            });
            EnsureTable(db, ControlsTableName, new[] {
                ColumnSpec("Id"), ColumnSpec("PageId"), ColumnSpec("Type", 40), ColumnSpec("Name"),
                ColumnSpec("SchemaJson"), ColumnSpec("BindingJson"), ColumnSpec("CreatedUtc", 80), ColumnSpec("UpdatedUtc", 80)
            });
            EnsureTable(db, CrudTableName, new[] {
                ColumnSpec("Id"), ColumnSpec("Name"), ColumnSpec("Database", 160), ColumnSpec("Table"),
                ColumnSpec("KeyColumn"), ColumnSpec("Route"), ColumnSpec("Scopes"), ColumnSpec("InputSchema"),
                ColumnSpec("Enabled", 8), ColumnSpec("CreatedUtc", 80), ColumnSpec("UpdatedUtc", 80)
            });
            EnsureTable(db, AuditTableName, new[] {
                ColumnSpec("Id"), ColumnSpec("Username", 160), ColumnSpec("EventType", 120), ColumnSpec("Target"),
                ColumnSpec("Detail"), ColumnSpec("CreatedUtc", 80)
            });
            EnsureTable(db, DesignerTableName, new[] { ColumnSpec("PageId"), ColumnSpec("Metadata"), ColumnSpec("UpdatedUtc", 80) });
            if (string.IsNullOrWhiteSpace(GetSetting("defaultPageStorageMode", "")))
                SetSetting("defaultPageStorageMode", "sql");
            DataServer.ScheduleSave();
        }

        private Table GetSettingsTable() => GetAdminDatabase().Tables[SettingsTableName];
        private Table GetPagesTable() => GetAdminDatabase().Tables[PagesTableName];
        private Table GetAssetsTable() => GetAdminDatabase().Tables[AssetsTableName];
        private Table GetControlsTable() => GetAdminDatabase().Tables[ControlsTableName];
        private Table GetCrudTable() => GetAdminDatabase().Tables[CrudTableName];
        private Table GetAuditTable() => GetAdminDatabase().Tables[AuditTableName];

        private static Column ColumnSpec(string name, int maxLength = -1) {
            return new Column(name, typeof(string), maxLength);
        }

        private static void EnsureTable(Database db, string name, IEnumerable<Column> columns) {
            var table = db.Tables.GetOrAdd(name, _ => new Table(name));
            foreach (var col in columns) {
                if (table.Columns.All(c => !string.Equals(c.Name, col.Name, StringComparison.OrdinalIgnoreCase)))
                    table.Columns.Add(col);
            }
            for (int i = 0; i < table.Rows.Count; i++) {
                var row = table.Rows[i];
                if (row.Length >= table.Columns.Count)
                    continue;
                Array.Resize(ref row, table.Columns.Count);
                table.Rows[i] = row;
            }
        }

        private string GetSetting(string key, string fallback) {
            var table = GetSettingsTable();
            foreach (var row in table.Rows) {
                if (string.Equals(GetRow(row, 0), key, StringComparison.OrdinalIgnoreCase))
                    return GetRow(row, 1);
            }
            return fallback;
        }

        private void SetSetting(string key, string value) {
            var table = GetSettingsTable();
            int index = FindRowIndex(table, 0, key);
            var row = new object[] { key, value ?? "", Now() };
            if (index >= 0)
                table.Rows[index] = row;
            else
                table.Rows.Add(row);
            DataServer.ScheduleSave();
        }

        private bool CanAccessDatabase(HttpRequest req, SqlAdminSessionContext session, string databaseName) {
            if (session == null || string.IsNullOrWhiteSpace(databaseName))
                return false;
            var ds = DataServer;
            if (!ds.Databases.TryGetValue(databaseName, out var db))
                return false;
            if (ds.IsSqlAdminAccount(session.Username))
                return _server.SqlAdminPanel != null && _server.SqlAdminPanel.IsLocalSqlAdminRequest(req);
            string user = NormalizeTenant(session.Username);
            string owner = NormalizeTenant(FirstNonEmpty(db.OwnerUsername, db.SqlAdminUsername));
            string dbName = NormalizeTenant(databaseName);
            return !string.IsNullOrWhiteSpace(owner)
                ? string.Equals(owner, user, StringComparison.OrdinalIgnoreCase)
                : string.Equals(dbName, user, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> RowToObject(Table table, object[] row) {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < table.Columns.Count; i++)
                dict[table.Columns[i].Name] = i < row.Length ? row[i] : null;
            return dict;
        }

        private Dictionary<string, string> ReadCrudValues(HttpRequest req) {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string body = req.Body ?? "";
            if (string.IsNullOrWhiteSpace(body))
                return values;
            using (var doc = JsonDocument.Parse(body)) {
                var root = doc.RootElement;
                JsonElement source = root;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("values", out var valuesElement) && valuesElement.ValueKind == JsonValueKind.Object)
                    source = valuesElement;
                if (source.ValueKind != JsonValueKind.Object)
                    return values;
                foreach (var prop in source.EnumerateObject()) {
                    if (IsRuntimeField(prop.Name))
                        continue;
                    values[prop.Name] = JsonValueToString(prop.Value);
                }
            }
            return values;
        }

        private void ValidateCrudValues(Table table, CrudDefinition def, Dictionary<string, string> values, bool create) {
            foreach (var key in values.Keys) {
                if (FindColumnIndex(table, key) < 0)
                    throw new InvalidOperationException("Unknown column: " + key);
            }
            if (!create)
                return;
            foreach (string required in ReadRequiredFields(def.InputSchema)) {
                if (!values.TryGetValue(required, out var value) || string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException("Missing required field: " + required);
            }
        }

        private static List<string> ReadRequiredFields(string schema) {
            var fields = new List<string>();
            if (string.IsNullOrWhiteSpace(schema))
                return fields;
            try {
                using (var doc = JsonDocument.Parse(schema)) {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return fields;
                    if (doc.RootElement.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array) {
                        foreach (var item in required.EnumerateArray()) {
                            if (item.ValueKind == JsonValueKind.String)
                                fields.Add(item.GetString());
                        }
                    }
                }
            } catch {
            }
            return fields;
        }

        private static object ConvertValue(string value, Type type) {
            if (value == null)
                return null;
            type = type ?? typeof(string);
            if (type == typeof(string))
                return value;
            if (type == typeof(int) || type == typeof(short) || type == typeof(byte))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(long))
                return long.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(double))
                return double.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(decimal))
                return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (type == typeof(bool))
                return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            if (type == typeof(DateTime))
                return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            return value;
        }

        private string ReadKey(HttpRequest req, CrudDefinition def) {
            string key = Query(req, def.KeyColumn);
            if (string.IsNullOrWhiteSpace(key))
                key = Query(req, "id");
            if (!string.IsNullOrWhiteSpace(key))
                return key;
            string body = req.Body ?? "";
            key = ReadJsonString(body, def.KeyColumn);
            if (string.IsNullOrWhiteSpace(key))
                key = ReadJsonString(body, "id");
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidOperationException("Key value is required.");
            return key;
        }

        private static bool CrudScopeAllows(CrudDefinition def, string required) {
            string scopes = def?.Scopes ?? "";
            return scopes.Split(',').Any(item => string.Equals(item.Trim(), required, StringComparison.OrdinalIgnoreCase))
                || (required == "sql:read" && scopes.Split(',').Any(item => string.Equals(item.Trim(), "sql:write", StringComparison.OrdinalIgnoreCase)))
                || scopes.Split(',').Any(item => string.Equals(item.Trim(), "sql:admin", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsWriteAction(string action) {
            return action == "create" || action == "update" || action == "delete";
        }

        private CrudDefinition FindCrudDefinition(string idOrRoute) {
            idOrRoute = (idOrRoute ?? "").Trim();
            foreach (var def in GetCrudTable().Rows.Select(CrudFromRow)) {
                if (string.Equals(def.Id, idOrRoute, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(def.Route, idOrRoute, StringComparison.OrdinalIgnoreCase))
                    return def;
            }
            return null;
        }

        private static string GenerateCrudClient(CrudDefinition def) {
            string id = EscapeJs(def.Id);
            string route = EscapeJs(def.Route);
            string baseUrl = "/Admin/api/crud/run/" + Uri.EscapeDataString(def.Id);
            var sb = new StringBuilder();
            sb.Append("(function(root){\n");
            sb.Append("  root.SocketJackCrud=root.SocketJackCrud||{};\n");
            sb.Append("  async function send(action,data,query){\n");
            sb.Append("    var url='").Append(EscapeJs(baseUrl)).Append("/'+encodeURIComponent(action);\n");
            sb.Append("    if(query){var qs=new URLSearchParams(query);var q=qs.toString();if(q)url+='?'+q;}\n");
            sb.Append("    var init={method:(action==='list'||action==='get')?'GET':'POST',credentials:'include',headers:{'Content-Type':'application/json'}};\n");
            sb.Append("    if(init.method==='POST')init.body=JSON.stringify(data||{});\n");
            sb.Append("    var res=await fetch(url,init);var text=await res.text();var json;try{json=JSON.parse(text);}catch(e){json={text:text};}\n");
            sb.Append("    if(!res.ok)throw new Error(json.error||('HTTP '+res.status));return json;\n");
            sb.Append("  }\n");
            sb.Append("  root.SocketJackCrud['").Append(id).Append("']={id:'").Append(id).Append("',route:'").Append(route).Append("',");
            sb.Append("list:function(q){return send('list',null,q);},");
            sb.Append("get:function(key){var q={};q['").Append(EscapeJs(def.KeyColumn)).Append("']=key;return send('get',null,q);},");
            sb.Append("create:function(values){return send('create',values);},");
            sb.Append("update:function(key,values){values=Object.assign({},values||{});values['").Append(EscapeJs(def.KeyColumn)).Append("']=key;return send('update',values);},");
            sb.Append("\"delete\":function(key){var v={};v['").Append(EscapeJs(def.KeyColumn)).Append("']=key;return send('delete',v);}};\n");
            sb.Append("})(typeof window!=='undefined'?window:globalThis);\n");
            return sb.ToString();
        }

        private string BuildInputSchema(Table table, string keyColumn) {
            var props = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in table.Columns)
                props[col.Name] = new { type = JsonTypeFor(col.DataType), maxLength = col.MaxLength };
            return JsonSerializer.Serialize(new { type = "object", keyColumn, properties = props }, JsonOptions);
        }

        private static string JsonTypeFor(Type type) {
            type = type ?? typeof(string);
            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
                return "integer";
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return "number";
            if (type == typeof(bool))
                return "boolean";
            return "string";
        }

        private void WriteAudit(string username, string eventType, string target, string detail) {
            try {
                GetAuditTable().Rows.Add(new object[] {
                    "audit_" + Guid.NewGuid().ToString("N"),
                    username ?? "",
                    eventType ?? "",
                    target ?? "",
                    detail ?? "",
                    Now()
                });
            } catch {
            }
        }

        private static bool TryReadDiskContent(string configuredPath, string route, out byte[] bytes, out string fileName, out DateTime? lastModifiedUtc) {
            bytes = null;
            fileName = null;
            lastModifiedUtc = null;
            if (string.IsNullOrWhiteSpace(configuredPath))
                return false;
            string fullPath = Path.GetFullPath(configuredPath);
            if (Directory.Exists(fullPath)) {
                string relative = NormalizePageRoute(route).Replace('/', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(fullPath, relative));
                if (!IsPathInsideRoot(candidate, fullPath))
                    return false;
                fullPath = candidate;
            }
            if (!File.Exists(fullPath))
                return false;
            bytes = File.ReadAllBytes(fullPath);
            fileName = Path.GetFileName(fullPath);
            try { lastModifiedUtc = File.GetLastWriteTimeUtc(fullPath); } catch { }
            return true;
        }

        private static bool IsPathInsideRoot(string path, string root) {
            string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase) || string.Equals(p.TrimEnd(Path.DirectorySeparatorChar), r.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private string InjectContext(string html, SqlAdminSessionContext session, string route) {
            html = html ?? "";
            string basePath = BasePath.TrimEnd('/');
            html = html
                .Replace("$Username", EscapeHtml(session?.Username ?? ""))
                .Replace("$CurrentDatabase", EscapeHtml(session?.CurrentDatabase ?? "db"))
                .Replace("$BasePath", EscapeHtml(basePath))
                .Replace("$PageRoute", EscapeHtml(route ?? ""));
            string contextScript = "<script>window.SocketJackContext={username:\"" + EscapeJson(session?.Username ?? "") + "\",currentDatabase:\"" + EscapeJson(session?.CurrentDatabase ?? "db") + "\",basePath:\"" + EscapeJson(basePath) + "\",pageRoute:\"" + EscapeJson(route ?? "") + "\"};</script>";
            int head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            if (head >= 0)
                return html.Substring(0, head) + contextScript + html.Substring(head);
            int body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
            if (body >= 0) {
                int close = html.IndexOf('>', body);
                if (close >= 0)
                    return html.Substring(0, close + 1) + contextScript + html.Substring(close + 1);
            }
            return contextScript + html;
        }

        private string DescribeStorageSource(AdminPage page, string mode) {
            mode = NormalizeStorageMode(mode);
            if (mode == "disk")
                return string.IsNullOrWhiteSpace(page.DirectoryPath) ? "(no disk path configured)" : page.DirectoryPath;
            return AdminDatabaseName + "." + PagesTableName + "[" + page.Route + "]";
        }

        private bool IsTargetMissing(AdminPage page, string mode) {
            mode = NormalizeStorageMode(mode);
            if (mode == "sql")
                return string.IsNullOrWhiteSpace(page.SqlHtml);
            if (string.IsNullOrWhiteSpace(page.DirectoryPath))
                return true;
            if (Directory.Exists(page.DirectoryPath)) {
                string candidate = Path.Combine(page.DirectoryPath, page.Route.Replace('/', Path.DirectorySeparatorChar));
                return !File.Exists(candidate);
            }
            return !File.Exists(page.DirectoryPath);
        }

        private static object[] PageToRow(AdminPage p) {
            return new object[] { p.Id, p.Route, p.Title, p.StorageMode, p.DirectoryPath, p.SqlHtml, p.Css, p.Js, p.CreatedUtc, p.UpdatedUtc };
        }

        private AdminPage PageFromRow(object[] row) {
            var designerTable = GetAdminDatabase().Tables[DesignerTableName];
            int designerIndex = FindRowIndex(designerTable, 0, GetRow(row, 0));
            return new AdminPage {
                Id = GetRow(row, 0),
                Route = GetRow(row, 1),
                Title = GetRow(row, 2),
                StorageMode = NormalizeStorageMode(GetRow(row, 3)),
                DirectoryPath = GetRow(row, 4),
                SqlHtml = GetRow(row, 5),
                Css = GetRow(row, 6),
                Js = GetRow(row, 7),
                CreatedUtc = GetRow(row, 8),
                UpdatedUtc = GetRow(row, 9),
                DesignerMetadata = designerIndex >= 0 ? GetRow(designerTable.Rows[designerIndex], 1) : ""
            };
        }

        private static object[] AssetToRow(AdminAsset a) {
            return new object[] { a.Id, a.PageId, a.Name, a.Kind, a.ContentType, a.StorageMode, a.DirectoryPath, a.Content, a.CreatedUtc, a.UpdatedUtc };
        }

        private static AdminAsset AssetFromRow(object[] row) {
            return new AdminAsset {
                Id = GetRow(row, 0),
                PageId = GetRow(row, 1),
                Name = GetRow(row, 2),
                Kind = GetRow(row, 3),
                ContentType = GetRow(row, 4),
                StorageMode = NormalizeStorageMode(GetRow(row, 5)),
                DirectoryPath = GetRow(row, 6),
                Content = GetRow(row, 7),
                CreatedUtc = GetRow(row, 8),
                UpdatedUtc = GetRow(row, 9)
            };
        }

        private static object[] ControlToRow(AdminControl c) {
            return new object[] { c.Id, c.PageId, c.Type, c.Name, c.SchemaJson, c.BindingJson, c.CreatedUtc, c.UpdatedUtc };
        }

        private static AdminControl ControlFromRow(object[] row) {
            return new AdminControl {
                Id = GetRow(row, 0),
                PageId = GetRow(row, 1),
                Type = GetRow(row, 2),
                Name = GetRow(row, 3),
                SchemaJson = GetRow(row, 4),
                BindingJson = GetRow(row, 5),
                CreatedUtc = GetRow(row, 6),
                UpdatedUtc = GetRow(row, 7)
            };
        }

        private static object[] CrudToRow(CrudDefinition c) {
            return new object[] { c.Id, c.Name, c.Database, c.Table, c.KeyColumn, c.Route, c.Scopes, c.InputSchema, c.Enabled ? "true" : "false", c.CreatedUtc, c.UpdatedUtc };
        }

        private static CrudDefinition CrudFromRow(object[] row) {
            return new CrudDefinition {
                Id = GetRow(row, 0),
                Name = GetRow(row, 1),
                Database = GetRow(row, 2),
                Table = GetRow(row, 3),
                KeyColumn = GetRow(row, 4),
                Route = GetRow(row, 5),
                Scopes = GetRow(row, 6),
                InputSchema = GetRow(row, 7),
                Enabled = !string.Equals(GetRow(row, 8), "false", StringComparison.OrdinalIgnoreCase),
                CreatedUtc = GetRow(row, 9),
                UpdatedUtc = GetRow(row, 10)
            };
        }

        private static object[] ControlPalette() {
            return new object[] {
                new { type = "form", label = "Form" },
                new { type = "table", label = "Table/Grid" },
                new { type = "button", label = "Button Action" },
                new { type = "text", label = "Text Input" },
                new { type = "select", label = "Select" },
                new { type = "checkbox", label = "Checkbox" },
                new { type = "hidden", label = "Hidden Field" }
            };
        }

        private static int FindColumnIndex(Table table, string columnName) {
            if (table == null || string.IsNullOrWhiteSpace(columnName))
                return -1;
            return table.Columns.FindIndex(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        }

        private static int FindRowIndex(Table table, int columnIndex, string value) {
            if (table == null || columnIndex < 0)
                return -1;
            for (int i = 0; i < table.Rows.Count; i++) {
                if (string.Equals(GetRow(table.Rows[i], columnIndex), value ?? "", StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        private static string GetRow(object[] row, int index) {
            return row != null && index >= 0 && index < row.Length ? row[index]?.ToString() ?? "" : "";
        }

        private static string Query(HttpRequest req, string key) {
            if (req?.QueryParameters != null && key != null && req.QueryParameters.TryGetValue(key, out var value))
                return value;
            return "";
        }

        private static string ReadBodyString(HttpRequest req, string key, string fallback) {
            return ReadJsonString(req?.Body ?? "", key, fallback);
        }

        private static string ReadJsonString(string json, string key, string fallback = "") {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
                return fallback;
            try {
                using (var doc = JsonDocument.Parse(json)) {
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var value))
                        return JsonValueToString(value);
                }
            } catch {
            }
            return fallback;
        }

        private static bool ReadJsonBool(string json, string key, bool fallback) {
            string value = ReadJsonString(json, key, fallback ? "true" : "false");
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1" || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string JsonValueToString(JsonElement value) {
            switch (value.ValueKind) {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetRawText().Trim('"');
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return "";
                default:
                    return value.GetRawText();
            }
        }

        private static string NormalizeStorageMode(string mode) {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode == "disk" || mode == "directory" || mode == "file")
                return "disk";
            return "sql";
        }

        private static string NormalizeControlType(string type) {
            type = (type ?? "form").Trim().ToLowerInvariant();
            switch (type) {
                case "form":
                case "table":
                case "grid":
                case "button":
                case "text":
                case "select":
                case "checkbox":
                case "hidden":
                    return type == "grid" ? "table" : type;
                default:
                    return "form";
            }
        }

        private static string NormalizePageRoute(string route) {
            route = (route ?? "").Trim().Replace('\\', '/').TrimStart('/');
            while (route.Contains("//"))
                route = route.Replace("//", "/");
            return route.Contains("..") ? "" : route;
        }

        private static string NormalizeRouteSlug(string value) {
            value = (value ?? "").Trim().Trim('/').ToLowerInvariant();
            var sb = new StringBuilder();
            foreach (char ch in value) {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                    sb.Append(ch);
                else if (ch == '-' || ch == '_' || ch == '.')
                    sb.Append(ch);
                else if (char.IsWhiteSpace(ch))
                    sb.Append('-');
            }
            return sb.ToString().Trim('-');
        }

        private static string NormalizeTenant(string value) {
            return (value ?? "").Trim().Trim('[', ']', '"', '\'').ToLowerInvariant();
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (var value in values ?? Array.Empty<string>())
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            return "";
        }

        private static string FirstPathPart(HttpRequest req) {
            return req?.PathVariables != null && req.PathVariables.Count > 0 ? Uri.UnescapeDataString(req.PathVariables[0] ?? "") : "";
        }

        private static bool IsRuntimeField(string name) {
            return string.Equals(name, "csrf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "csrfToken", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value, int fallback) {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private static string ReasonPhrase(int code) {
            switch (code) {
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 429: return "Too Many Requests";
                default: return code >= 500 ? "Internal Server Error" : "OK";
            }
        }

        private static string Now() {
            return DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        private static string EscapeHtml(string value) {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static string EscapeJson(string value) {
            if (string.IsNullOrEmpty(value))
                return "";
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static string EscapeJs(string value) {
            return EscapeJson(value ?? "");
        }

        private sealed class AdminPage {
            public string DesignerMetadata { get; set; }
            public string Revision {
                get {
                    using (var sha = System.Security.Cryptography.SHA256.Create()) {
                        var content = JsonSerializer.Serialize(new[] { Id, Route, Title, StorageMode, DirectoryPath, SqlHtml, Css, Js, DesignerMetadata, StorageMode == "disk" && TryReadDiskContent(DirectoryPath, Route, out var diskBytes, out _, out _) ? Convert.ToBase64String(diskBytes) : "" });
                        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content))).Replace("-", "").ToLowerInvariant();
                    }
                }
            }
            public string Id { get; set; }
            public string Route { get; set; }
            public string Title { get; set; }
            public string StorageMode { get; set; }
            public string DirectoryPath { get; set; }
            public string SqlHtml { get; set; }
            public string Css { get; set; }
            public string Js { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class AdminAsset {
            public string Revision { get { using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { Id, PageId, Name, Kind, ContentType, StorageMode, DirectoryPath, Content })))).Replace("-", "").ToLowerInvariant(); } }

            public string Id { get; set; }
            public string PageId { get; set; }
            public string Name { get; set; }
            public string Kind { get; set; }
            public string ContentType { get; set; }
            public string StorageMode { get; set; }
            public string DirectoryPath { get; set; }
            public string Content { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class AdminControl {
            public string Id { get; set; }
            public string PageId { get; set; }
            public string Type { get; set; }
            public string Name { get; set; }
            public string SchemaJson { get; set; }
            public string BindingJson { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class CrudDefinition {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Database { get; set; }
            public string Table { get; set; }
            public string KeyColumn { get; set; }
            public string Route { get; set; }
            public string Scopes { get; set; }
            public string InputSchema { get; set; }
            public bool Enabled { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }
    }
}
