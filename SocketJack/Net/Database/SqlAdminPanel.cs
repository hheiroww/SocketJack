using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using SocketJack.Serialization;

namespace SocketJack.Net.Database {

    /// <summary>
    /// Carries authenticated SQL Admin session values used by the Admin suite.
    /// </summary>
    internal sealed class SqlAdminSessionContext {
        public string Username { get; set; }
        public string SessionId { get; set; }
        public string CurrentDatabase { get; set; }
        public string CsrfToken { get; set; }
        public string OwnerKey { get; set; }
    }
    /// <summary>
    /// An HTTP-based SQL administration panel (similar to SQL Server Management Studio)
    /// that can be enabled on a <see cref="MutableTcpServer"/>. When enabled, it registers
    /// HTTP routes under <c>/sql</c> that provide:
    /// <list type="bullet">
    /// <item>A login page authenticated against <see cref="DataServer.Users"/>.</item>
    /// <item>An Object Explorer tree showing databases, tables and columns.</item>
    /// <item>A SQL query editor with syntax highlighting.</item>
    /// <item>A results grid for query output.</item>
    /// </list>
    /// <para>
    /// Enable via <see cref="MutableTcpServer.SqlAdminPanelEnabled"/> or access the
    /// panel instance through <see cref="MutableTcpServer.SqlAdminPanel"/>.
    /// </para>
    /// </summary>
    internal class SqlAdminPanel {

        private readonly MutableTcpServer _server;
        private readonly ConcurrentDictionary<string, SqlAdminSession> _sessions = new ConcurrentDictionary<string, SqlAdminSession>();
        private readonly ConcurrentDictionary<string, ApiEndpointDef> _apiEndpoints = new ConcurrentDictionary<string, ApiEndpointDef>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ApiEndpointDef> _apiRouteSettings = new ConcurrentDictionary<string, ApiEndpointDef>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, EventDef> _eventDefs = new ConcurrentDictionary<string, EventDef>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, List<DateTime>> _dynamicEndpointRateWindows = new ConcurrentDictionary<string, List<DateTime>>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _eventCircuitFailures = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _pageTemplateCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _pageHashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions _reflectJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        private const string ErrorDiagnosticsTableName = "SocketJackErrorLog";
        private const string SqlAdminAuditTableName = "SqlAdminAuditLog";
        private const string SqlAdminRestorePointsTableName = "SqlAdminRestorePoints";
        private const int MaxSqlRestorePointRows = 32;
        private const int MaxSqlRestorePointBytes = 24 * 1024 * 1024;
        private const int MaxSingleSqlRestorePointBytes = 8 * 1024 * 1024;
        private bool _registered;

        /// <summary>
        /// Directory containing HTML page templates for the SQL Admin Panel.
        /// Templates use <c>$VariableName</c> placeholders that are replaced at runtime.
        /// Defaults to a <c>html</c> folder next to the running application.
        /// </summary>
        internal static string PagesFolder { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "html");

        /// <summary>
        /// Base URL path for all admin panel routes.
        /// </summary>
        internal string BasePath { get; set; } = "/sql";

        internal SqlAdminPanel(MutableTcpServer server) {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        #region Route Registration

        internal void Register() {
            if (_registered) return;
            _registered = true;

            var basePath = BasePath.TrimEnd('/');

            // Main page (login or panel)
            _server.Map("GET", basePath, (conn, req, ct) => ServePage(req));
            _server.Map("GET", basePath + "/", (conn, req, ct) => ServePage(req));

            // Login / Logout
            _server.Map("POST", basePath + "/login", (conn, req, ct) => HandleLogin(req));
            _server.Map("POST", basePath + "/api/bootstrap/sa", (conn, req, ct) => HandleSaBootstrap(req));
            _server.Map("GET", basePath + "/logout", (conn, req, ct) => HandleLogout(req));

            // API endpoints
            _server.Map("GET", basePath + "/api/databases", (conn, req, ct) => ApiDatabases(req));
            _server.Map("GET", basePath + "/api/tables", (conn, req, ct) => ApiTables(req));
            _server.Map("GET", basePath + "/api/columns", (conn, req, ct) => ApiColumns(req));
            _server.Map("GET", basePath + "/api/rows", (conn, req, ct) => ApiRows(req));
            _server.Map("POST", basePath + "/api/query", (conn, req, ct) => ApiQuery(req));
            _server.Map("GET", basePath + "/api/users", (conn, req, ct) => ApiUsers(req));
            _server.Map("GET", basePath + "/api/sessions", (conn, req, ct) => ApiSessions(req));
            _server.Map("GET", basePath + "/api/audit/recent", (conn, req, ct) => ApiAuditRecent(req));
            _server.Map("GET", basePath + "/api/backups/list", (conn, req, ct) => ApiBackupsList(req));
            _server.Map("POST", basePath + "/api/backups/restore", (conn, req, ct) => ApiBackupsRestore(req));
            _server.Map("GET", basePath + "/api/operations/dashboard", (conn, req, ct) => ApiOperationsDashboard(req));
            _server.Map("GET", basePath + "/api/errors/recent", (conn, req, ct) => ApiErrorDiagnosticsRecent(req));

            // Table Designer API endpoints
            _server.Map("POST", basePath + "/api/designer/create-table", (conn, req, ct) => ApiDesignerCreateTable(req));
            _server.Map("POST", basePath + "/api/designer/drop-table", (conn, req, ct) => ApiDesignerDropTable(req));
            _server.Map("POST", basePath + "/api/designer/rename-table", (conn, req, ct) => ApiDesignerRenameTable(req));
            _server.Map("POST", basePath + "/api/designer/save-schema", (conn, req, ct) => ApiDesignerSaveSchema(req));
            _server.Map("POST", basePath + "/api/designer/add-column", (conn, req, ct) => ApiDesignerAddColumn(req));
            _server.Map("POST", basePath + "/api/designer/remove-column", (conn, req, ct) => ApiDesignerRemoveColumn(req));
            _server.Map("POST", basePath + "/api/designer/update-column", (conn, req, ct) => ApiDesignerUpdateColumn(req));
            _server.Map("POST", basePath + "/api/designer/insert-row", (conn, req, ct) => ApiDesignerInsertRow(req));
            _server.Map("POST", basePath + "/api/designer/update-cell", (conn, req, ct) => ApiDesignerUpdateCell(req));
            _server.Map("POST", basePath + "/api/designer/delete-row", (conn, req, ct) => ApiDesignerDeleteRow(req));
            _server.Map("GET", basePath + "/api/designer/viewport", (conn, req, ct) => ApiDesignerViewport(req));

            // API Creator endpoints
            _server.Map("GET", basePath + "/api/endpoints/list", (conn, req, ct) => ApiEndpointsList(req));
            _server.Map("POST", basePath + "/api/endpoints/save", (conn, req, ct) => ApiEndpointsSave(req));
            _server.Map("POST", basePath + "/api/endpoints/delete", (conn, req, ct) => ApiEndpointsDelete(req));
            _server.Map("POST", basePath + "/api/endpoints/test", (conn, req, ct) => ApiEndpointsTest(req));
            _server.Map("POST", basePath + "/api/endpoints/settings/save", (conn, req, ct) => ApiEndpointSettingsSave(req));
            _server.Map("GET", basePath + "/api/endpoints/reflect", (conn, req, ct) => ApiEndpointsReflect(req));

            // Query Builder saved trees
            _server.Map("GET", basePath + "/api/qb/list-trees", (conn, req, ct) => QbListTrees(req));
            _server.Map("GET", basePath + "/api/qb/load-tree", (conn, req, ct) => QbLoadTree(req));
            _server.Map("POST", basePath + "/api/qb/save-tree", (conn, req, ct) => QbSaveTree(req));
            _server.Map("POST", basePath + "/api/qb/delete-tree", (conn, req, ct) => QbDeleteTree(req));

            // Events system
            _server.Map("GET", basePath + "/api/events/list", (conn, req, ct) => ApiEventsList(req));
            _server.Map("POST", basePath + "/api/events/save", (conn, req, ct) => ApiEventsSave(req));
            _server.Map("POST", basePath + "/api/events/delete", (conn, req, ct) => ApiEventsDelete(req));
            _server.Map("POST", basePath + "/api/events/execute", (conn, req, ct) => ApiEventsExecute(req));
            _server.Map("GET", basePath + "/api/events/reflect", (conn, req, ct) => ApiEventsReflect(req));

            // Load and register saved API endpoints
            LoadApiEndpoints();
            LoadApiRouteSettings();
            LoadEventDefs();
            PruneSqlAdminStorage();
        }

        internal void Unregister() {
            if (!_registered) return;
            _registered = false;

            var basePath = BasePath.TrimEnd('/');
            _server.RemoveRoute("GET", basePath);
            _server.RemoveRoute("GET", basePath + "/");
            _server.RemoveRoute("POST", basePath + "/login");
            _server.RemoveRoute("POST", basePath + "/api/bootstrap/sa");
            _server.RemoveRoute("GET", basePath + "/logout");
            _server.RemoveRoute("GET", basePath + "/api/databases");
            _server.RemoveRoute("GET", basePath + "/api/tables");
            _server.RemoveRoute("GET", basePath + "/api/columns");
            _server.RemoveRoute("GET", basePath + "/api/rows");
            _server.RemoveRoute("POST", basePath + "/api/query");
            _server.RemoveRoute("GET", basePath + "/api/users");
            _server.RemoveRoute("GET", basePath + "/api/sessions");
            _server.RemoveRoute("GET", basePath + "/api/audit/recent");
            _server.RemoveRoute("GET", basePath + "/api/backups/list");
            _server.RemoveRoute("POST", basePath + "/api/backups/restore");
            _server.RemoveRoute("GET", basePath + "/api/operations/dashboard");
            _server.RemoveRoute("GET", basePath + "/api/errors/recent");

            // Table Designer routes
            _server.RemoveRoute("POST", basePath + "/api/designer/create-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/drop-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/rename-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/save-schema");
            _server.RemoveRoute("POST", basePath + "/api/designer/add-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/remove-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/update-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/insert-row");
            _server.RemoveRoute("POST", basePath + "/api/designer/update-cell");
            _server.RemoveRoute("POST", basePath + "/api/designer/delete-row");
            _server.RemoveRoute("GET", basePath + "/api/designer/viewport");

            // API Creator routes
            _server.RemoveRoute("GET", basePath + "/api/endpoints/list");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/save");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/delete");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/test");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/settings/save");
            _server.RemoveRoute("GET", basePath + "/api/endpoints/reflect");

            // Query Builder tree routes
            _server.RemoveRoute("GET", basePath + "/api/qb/list-trees");
            _server.RemoveRoute("GET", basePath + "/api/qb/load-tree");
            _server.RemoveRoute("POST", basePath + "/api/qb/save-tree");
            _server.RemoveRoute("POST", basePath + "/api/qb/delete-tree");

            // Events routes
            _server.RemoveRoute("GET", basePath + "/api/events/list");
            _server.RemoveRoute("POST", basePath + "/api/events/save");
            _server.RemoveRoute("POST", basePath + "/api/events/delete");
            _server.RemoveRoute("POST", basePath + "/api/events/execute");
            _server.RemoveRoute("GET", basePath + "/api/events/reflect");

            // Remove dynamic API routes
            foreach (var ep in _apiEndpoints.Values) {
                try { _server.RemoveRoute(ep.HttpMethod.ToUpperInvariant(), ep.Route); } catch { }
            }
            _apiEndpoints.Clear();
            _apiRouteSettings.Clear();
            _eventDefs.Clear();

            _sessions.Clear();
        }

        #endregion

        #region Session Management

        private class SqlAdminSession {
            public string Token;
            public string SessionId;
            public string Username;
            public string OwnerKey;
            public string CsrfToken;
            public DateTime Created;
            public DateTime LastActivity;
            public string CurrentDatabase;
            public bool Revoked;
        }

        private class ApiEndpointDef {
            public string Id;
            public string Name;
            public string Route;
            public string HttpMethod;
            public string Database;
            public string SqlQuery;
            public string QuerySteps; // JSON array of query steps
            public string ResponseFormat;
            public string ContentType;
            public string Variables; // Comma-separated list of $variable names
            public bool Enabled;
            public bool Approved = false;
            public string ApprovedBy = null;
            public string ApprovedUtc = null;
            public bool DryRunByDefault = false;
            public string BodyType;
            public string BodySchema;
            public string Parameters;
            public string OutputSchema;
            public string Description;
            public string Source;
            public bool ReadOnly;
            public string HandlerTypeName;
            public string HandlerMethodName;
            public string HandlerArguments;
            public string AuthMode;
            public string Scopes;
            public int RateLimitPerMinute;
            public string CorsPolicy;
            public string InputSchema;
            public bool PublicEnabled;
            public string EndpointSecret;
        }

        /// <summary>
        /// Represents a saved event definition.
        /// Each event has an array of action nodes serialized as JSON.
        /// </summary>
        private class EventDef {
            public string Id;
            public string Name;
            public string Description;
            public bool Enabled;
            public bool Approved;
            public string ApprovedBy;
            public string ApprovedUtc;
            public bool DryRunByDefault;
            public string Nodes; // JSON – array of node objects (trigger, action, condition, etc.)
        }

        private class SqlRiskAssessment {
            public string Operation = "read";
            public string Severity = "low";
            public string Reason = "";
            public bool RequiresConfirmation;
        }

        private class SqlDatabaseSnapshotDto {
            public List<SqlTableSnapshotDto> Tables { get; set; } = new List<SqlTableSnapshotDto>();
        }

        private class SqlTableSnapshotDto {
            public string Name { get; set; } = "";
            public List<SqlColumnSnapshotDto> Columns { get; set; } = new List<SqlColumnSnapshotDto>();
            public List<List<string>> Rows { get; set; } = new List<List<string>>();
        }

        private class SqlColumnSnapshotDto {
            public string Name { get; set; } = "";
            public string TypeName { get; set; } = "";
            public int MaxLength { get; set; }
        }

        private string CreateSession(string username) {
            var token = GenerateToken();
            var session = new SqlAdminSession {
                Token = token,
                SessionId = "sqlsess_" + Guid.NewGuid().ToString("N"),
                Username = username,
                OwnerKey = BuildSqlOwnerKey(username),
                CsrfToken = GenerateToken(),
                Created = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                CurrentDatabase = GetDefaultDatabaseForUser(username)
            };
            _sessions[token] = session;
            return token;
        }

        private SqlAdminSession GetSession(HttpRequest req) {
            var cookie = GetCookie(req, "sqladmin_token");
            if (string.IsNullOrEmpty(cookie)) return null;
            if (_sessions.TryGetValue(cookie, out var session)) {
                if (session.Revoked) {
                    _sessions.TryRemove(cookie, out _);
                    return null;
                }
                // Expire sessions after 8 hours of inactivity
                if ((DateTime.UtcNow - session.LastActivity).TotalHours > 8) {
                    _sessions.TryRemove(cookie, out _);
                    return null;
                }
                session.LastActivity = DateTime.UtcNow;
                return session;
            }
            return null;
        }

        internal bool TryGetSessionContext(HttpRequest req, out SqlAdminSessionContext context) {
            context = null;
            var session = GetSession(req);
            if (session == null)
                return false;
            context = new SqlAdminSessionContext {
                Username = session.Username,
                SessionId = session.SessionId,
                CurrentDatabase = session.CurrentDatabase,
                CsrfToken = session.CsrfToken,
                OwnerKey = session.OwnerKey
            };
            return true;
        }

        internal bool IsLocalSqlAdminRequest(HttpRequest req) {
            if (IsLocalhostRequest(req))
                return true;
            var ds = GetDataServer();
            return ds != null && ds.IsSqlLoginIpExplicitlyAllowed(ExtractSqlAdminClientIp(req));
        }
        private static string GetCookie(HttpRequest req, string name) {
            if (req?.Headers == null) return null;
            if (!req.Headers.TryGetValue("Cookie", out var cookieHeader)) return null;
            if (string.IsNullOrEmpty(cookieHeader)) return null;
            foreach (var part in cookieHeader.Split(';')) {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) {
                    return trimmed.Substring(name.Length + 1);
                }
            }
            return null;
        }

        private static string GenerateToken() {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        private string GetDefaultDatabaseForUser(string username) {
            var ds = GetDataServer();
            if (ds == null)
                return "db";

            if (ds.IsSqlAdminAccount(username))
                return ds.Databases.ContainsKey("db") ? "db" : ds.Databases.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "db";

            foreach (var kvp in ds.Databases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (CanSqlUserAccessDatabase(ds, username, kvp.Key, kvp.Value))
                    return kvp.Key;
            }
            return "";
        }

        private bool CanSqlAdminSessionAccessDatabase(HttpRequest req, SqlAdminSession session, DataServer ds, string databaseName) {
            if (session == null || ds == null || string.IsNullOrWhiteSpace(databaseName))
                return false;
            if (!ds.Databases.TryGetValue(databaseName, out var db))
                return false;

            if (ds.IsSqlAdminAccount(session.Username))
                return IsLocalSqlAdminRequest(req);

            return CanSqlUserAccessDatabase(ds, session.Username, databaseName, db);
        }

        private static bool CanSqlSessionAccessDatabase(DataServer ds, SqlSession session, string databaseName) {
            if (session == null || ds == null || string.IsNullOrWhiteSpace(databaseName))
                return false;
            if (!ds.Databases.TryGetValue(databaseName, out var db))
                return false;
            if (ds.IsSqlAdminAccount(session.Username))
                return true;
            return CanSqlUserAccessDatabase(ds, session.Username, databaseName, db);
        }

        private static bool CanSqlUserAccessDatabase(DataServer ds, string username, string databaseName, Database db) {
            if (db == null || string.IsNullOrWhiteSpace(username))
                return false;

            string user = NormalizeSqlTenantName(username);
            string owner = NormalizeSqlTenantName(FirstNonEmpty(db.OwnerUsername, db.SqlAdminUsername));
            string name = NormalizeSqlTenantName(databaseName);

            if (!string.IsNullOrWhiteSpace(owner))
                return string.Equals(owner, user, StringComparison.OrdinalIgnoreCase);

            return string.Equals(name, user, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSqlTenantName(string value) {
            return (value ?? "").Trim().Trim('[', ']', '"', '\'').ToLowerInvariant();
        }

        private static string BuildSqlOwnerKey(string username) {
            return "sql:" + NormalizeSqlTenantName(username);
        }

        private static string FirstNonEmpty(params string[] values) {
            foreach (var value in values ?? Array.Empty<string>()) {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private string SqlTenantForbiddenJson(HttpRequest req, string databaseName) {
            req.Context.StatusCode = "403 Forbidden";
            return "{\"error\":\"SQL Admin tenant isolation blocked database access.\",\"database\":\"" + EscapeJson(databaseName ?? "") + "\",\"tenantIsolation\":true}";
        }

        #endregion

        #region DataServer Access

        private DataServer GetDataServer() {
            return FindDataServer();
        }

        private DataServer FindDataServer() {
            try { return _server?.FindDataServer(); } catch { return null; }
        }
        private bool Authenticate(string username, string password) {
            var ds = GetDataServer();
            if (ds == null) return false;
            return ds.Authenticate(username, password);
        }

        private bool IsSaBootstrapRequired() {
            var ds = GetDataServer();
            return ds != null && ds.RequiresSaPasswordSetup;
        }

        private bool IsLocalhostRequest(HttpRequest req) {
            var address = req?.Context?.Connection?.EndPoint?.Address;
            if (address == null)
                return false;
            return IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Parse("127.0.0.1"))
                || address.Equals(IPAddress.IPv6Loopback);
        }

        private static string GetHeader(HttpRequest req, string name) {
            if (req?.Headers == null || string.IsNullOrWhiteSpace(name))
                return null;
            if (req.Headers.TryGetValue(name, out var direct))
                return direct;
            foreach (var kvp in req.Headers) {
                if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value;
            }
            return null;
        }

        private static bool IsHttpsRequest(HttpRequest req) {
            if (req?.Context?.Connection?.Parent?.Options?.UseSsl == true)
                return true;

            bool trustForwardedScheme = req?.Context?.Connection?.Parent?.Options?.EndpointSecurity?.TrustForwardedForHeaders == true;
            if (!trustForwardedScheme)
                return false;

            var forwarded = GetHeader(req, "X-Forwarded-Proto");
            if (string.Equals(forwarded, "https", StringComparison.OrdinalIgnoreCase))
                return true;
            var scheme = GetHeader(req, "X-Scheme") ?? GetHeader(req, "X-Forwarded-Scheme");
            return string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase);
        }

        private string BuildSqlAdminCookie(HttpRequest req, string token, bool expired = false) {
            var sb = new StringBuilder();
            sb.Append("sqladmin_token=").Append(token ?? "")
              .Append("; Path=/; HttpOnly; SameSite=Strict");
            if (expired)
                sb.Append("; Max-Age=0");
            if (IsHttpsRequest(req))
                sb.Append("; Secure");
            return sb.ToString();
        }

        private bool ValidateSqlAdminMutation(HttpRequest req, SqlAdminSession session, string scope, out string errorJson) {
            errorJson = null;
            if (session == null) {
                req.Context.StatusCode = "401 Unauthorized";
                errorJson = "{\"error\":\"Not authenticated.\"}";
                return false;
            }

            if (string.IsNullOrWhiteSpace(session.CsrfToken))
                session.CsrfToken = GenerateToken();

            // Local developer use stays frictionless; remote browser/API mutations must prove same-session intent.
            if (IsLocalhostRequest(req))
                return true;

            string supplied = GetHeader(req, "X-SqlAdmin-Csrf")
                ?? GetHeader(req, "X-CSRF-Token");
            if (string.IsNullOrWhiteSpace(supplied) && req?.QueryParameters != null && req.QueryParameters.TryGetValue("csrf", out var qsToken))
                supplied = qsToken;
            if (string.IsNullOrWhiteSpace(supplied))
                supplied = ExtractJsonString(req?.Body ?? "", "csrfToken") ?? ExtractJsonString(req?.Body ?? "", "csrf");

            if (string.Equals(supplied, session.CsrfToken, StringComparison.Ordinal))
                return true;

            req.Context.StatusCode = "403 Forbidden";
            WriteSqlAudit(req, session, "auth.csrf", session.CurrentDatabase ?? "", scope ?? "", "blocked", 0, "Missing or invalid SQL Admin CSRF token.", "");
            errorJson = "{\"error\":\"SQL Admin mutation requires a valid CSRF token.\",\"csrfRequired\":true,\"csrfToken\":\"" + EscapeJson(session.CsrfToken ?? "") + "\"}";
            return false;
        }

        #endregion

        #region Route Handlers

        private object ServePage(HttpRequest req) {
            var session = GetSession(req);
            req.Context.ContentType = "text/html";
            if (session == null) {
                return LoginPageHtml(req);
            }
            return PanelPageHtml(session);
        }

        private object HandleLogin(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var body = req.Body ?? "";
            string username = null, password = null;

            // Parse JSON body: {"username":"...","password":"..."}
            // Simple manual parse to avoid dependency on System.Text.Json (netstandard2.1 compat)
            username = ExtractJsonString(body, "username");
            password = ExtractJsonString(body, "password");

            if (string.IsNullOrEmpty(username)) {
                req.Context.StatusCode = "401 Unauthorized";
                WriteSqlAudit(req, null, "login.failed", "", "auth", "error", 0, "Username is required.", "");
                return "{\"error\":\"Username is required.\"}";
            }

            var ds = GetDataServer();
            string clientIp = ExtractSqlAdminClientIp(req);
            if (ds != null && !ds.IsSqlLoginIpAllowed(clientIp)) {
                req.Context.StatusCode = "403 Forbidden";
                WriteSqlAudit(req, null, "login.blocked", "", username, "blocked", 0, "SQL Admin login from this IP address is not allowed.", clientIp);
                return "{\"error\":\"SQL Admin login from this IP address is not allowed.\"}";
            }
            if (ds != null && ds.IsSqlAdminAccount(username)) {
                if (!IsLocalSqlAdminRequest(req)) {
                    req.Context.StatusCode = "403 Forbidden";
                    WriteSqlAudit(req, null, "login.blocked", "", "sa", "blocked", 0, "The sa account can only log in from localhost or a SQL Admin whitelisted IP address.", ExtractSqlAdminClientIp(req));
                    return "{\"error\":\"The sa account can only log in from localhost, 127.0.0.1, or a SQL Admin whitelisted IP address.\"}";
                }
                if (ds.RequiresSaPasswordSetup) {
                    req.Context.StatusCode = "428 Precondition Required";
                    WriteSqlAudit(req, null, "login.blocked", "", "sa", "blocked", 0, "The default sa password must be set locally before login.", "");
                    return "{\"error\":\"The default sa password is blank. Set a new sa password from localhost before logging in.\",\"requireSaPasswordSetup\":true}";
                }
            }

            if (!Authenticate(username, password ?? "")) {
                req.Context.StatusCode = "401 Unauthorized";
                WriteSqlAudit(req, null, "login.failed", "", username, "error", 0, "Invalid username or password.", "");
                return "{\"error\":\"Invalid username or password.\"}";
            }

            string defaultDatabase = GetDefaultDatabaseForUser(username);
            if (ds != null && !ds.IsSqlAdminAccount(username) && string.IsNullOrWhiteSpace(defaultDatabase)) {
                req.Context.StatusCode = "403 Forbidden";
                WriteSqlAudit(req, null, "login.blocked", "", username, "blocked", 0, "No SQL Admin tenant database is owned by this username.", "");
                return "{\"error\":\"No SQL Admin tenant database is owned by this username.\",\"tenantIsolation\":true}";
            }

            var token = CreateSession(username);
            _sessions.TryGetValue(token, out var createdSession);
            var resp = req.Context.Response;
            resp.Headers["Set-Cookie"] = BuildSqlAdminCookie(req, token);
            WriteSqlAudit(req, createdSession, "login.success", createdSession?.CurrentDatabase ?? defaultDatabase, "auth", "success", 0, "SQL Admin session created.", "");
            return "{\"success\":true,\"username\":\"" + EscapeJson(username) + "\",\"currentDatabase\":\"" + EscapeJson(createdSession?.CurrentDatabase ?? defaultDatabase) + "\",\"csrfToken\":\"" + EscapeJson(createdSession?.CsrfToken ?? "") + "\",\"sessionId\":\"" + EscapeJson(createdSession?.SessionId ?? "") + "\",\"tenantIsolation\":true}";
        }

        private object HandleSaBootstrap(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var ds = GetDataServer();
            if (ds == null) {
                req.Context.StatusCode = "503 Service Unavailable";
                return "{\"error\":\"DataServer not available.\"}";
            }
            if (!IsLocalhostRequest(req)) {
                req.Context.StatusCode = "403 Forbidden";
                return "{\"error\":\"The sa password can only be set from localhost or 127.0.0.1.\"}";
            }
            if (!ds.RequiresSaPasswordSetup) {
                return "{\"success\":true,\"alreadyConfigured\":true}";
            }

            var body = req.Body ?? "";
            var username = ExtractJsonString(body, "username") ?? "sa";
            var password = ExtractJsonString(body, "password");
            var confirmPassword = ExtractJsonString(body, "confirmPassword");
            if (!string.Equals(username, "sa", StringComparison.OrdinalIgnoreCase)) {
                req.Context.StatusCode = "400 Bad Request";
                return "{\"error\":\"The bootstrap dialog can only set the sa account.\"}";
            }
            if (string.IsNullOrEmpty(password) || password.Length < 8) {
                req.Context.StatusCode = "400 Bad Request";
                return "{\"error\":\"Password must be at least 8 characters.\"}";
            }
            if (confirmPassword != null && !string.Equals(password, confirmPassword, StringComparison.Ordinal)) {
                req.Context.StatusCode = "400 Bad Request";
                return "{\"error\":\"Passwords do not match.\"}";
            }

            ds.SetSqlAdminAccount("sa", password);
            WriteSqlAudit(req, null, "sa.bootstrap", "", "sa", "success", 0, "The sa account password was set from localhost.", "");
            return "{\"success\":true}";
        }

        private object HandleLogout(HttpRequest req) {
            var cookie = GetCookie(req, "sqladmin_token");
            SqlAdminSession session = null;
            if (!string.IsNullOrEmpty(cookie) && _sessions.TryRemove(cookie, out session))
                session.Revoked = true;
            req.Context.ContentType = "text/html";
            var resp = req.Context.Response;
            resp.Headers["Set-Cookie"] = BuildSqlAdminCookie(req, "", expired: true);
            resp.Headers["Location"] = BasePath;
            req.Context.StatusCode = "302 Found";
            WriteSqlAudit(req, session, "logout", session?.CurrentDatabase ?? "", "auth", "success", 0, "SQL Admin session revoked.", "");
            return "<html><body>Redirecting...</body></html>";
        }

        private object ApiDatabases(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"databases\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"databases\":[");
            bool first = true;
            foreach (var kvp in ds.Databases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!CanSqlAdminSessionAccessDatabase(req, session, ds, kvp.Key))
                    continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(kvp.Key))
                  .Append("\",\"ownerUsername\":\"").Append(EscapeJson(FirstNonEmpty(kvp.Value.OwnerUsername, kvp.Value.SqlAdminUsername, kvp.Key)))
                  .Append("\",\"tenantScoped\":true")
                  .Append(",\"tableCount\":").Append(kvp.Value.Tables.Count).Append("}");
            }
            sb.Append("],\"tenantIsolation\":true}");
            return sb.ToString();
        }

        private object ApiTables(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"tables\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"tables\":[]}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName))
                return SqlTenantForbiddenJson(req, dbName);

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName)).Append("\",\"tables\":[");
            bool first = true;
            foreach (var kvp in db.Tables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(kvp.Key))
                  .Append("\",\"columnCount\":").Append(kvp.Value.Columns.Count)
                  .Append(",\"rowCount\":").Append(kvp.Value.Rows.Count)
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiColumns(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            if (tableName == null) { return "{\"columns\":[]}"; }

            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"columns\":[]}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName))
                return SqlTenantForbiddenJson(req, dbName);
            if (!db.Tables.TryGetValue(tableName, out var table)) { return "{\"columns\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"columns\":[");
            bool first = true;
            foreach (var col in table.Columns) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(col.Name))
                  .Append("\",\"type\":\"").Append(EscapeJson(col.DataType?.Name ?? "string"))
                  .Append("\",\"maxLength\":").Append(col.MaxLength)
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiRows(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[],\"rows\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            if (tableName == null) { return "{\"columns\":[],\"rows\":[]}"; }

            int top = 200;
            if (req.QueryParameters.ContainsKey("top") && int.TryParse(req.QueryParameters["top"], out var parsedTop))
                top = Math.Min(Math.Max(parsedTop, 1), 10000);

            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"columns\":[],\"rows\":[]}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName))
                return SqlTenantForbiddenJson(req, dbName);
            if (!db.Tables.TryGetValue(tableName, out var table)) { return "{\"columns\":[],\"rows\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"totalRows\":").Append(table.Rows.Count)
              .Append(",\"columns\":[");
            for (int c = 0; c < table.Columns.Count; c++) {
                if (c > 0) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(table.Columns[c].Name)).Append("\"");
            }
            sb.Append("],\"rows\":[");
            int rowCount = Math.Min(table.Rows.Count, top);
            for (int r = 0; r < rowCount; r++) {
                if (r > 0) sb.Append(",");
                sb.Append("[");
                var row = table.Rows[r];
                for (int c = 0; c < row.Length; c++) {
                    if (c > 0) sb.Append(",");
                    if (row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("]");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiQuery(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "query", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var sql = ExtractJsonString(body, "query");
            var dbName = ExtractJsonString(body, "database");
            if (!string.IsNullOrEmpty(dbName)) {
                if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName))
                    return SqlTenantForbiddenJson(req, dbName);
                session.CurrentDatabase = dbName;
            }
            if (string.IsNullOrWhiteSpace(session.CurrentDatabase) ||
                !CanSqlAdminSessionAccessDatabase(req, session, ds, session.CurrentDatabase))
                return SqlTenantForbiddenJson(req, session.CurrentDatabase);

            if (string.IsNullOrWhiteSpace(sql)) {
                return "{\"error\":\"No query provided.\"}";
            }

            var risk = ClassifySqlRisk(sql);
            bool confirmed = ExtractJsonBool(body, "confirmDestructive") || ExtractJsonBool(body, "confirmRisk");
            string confirmText = (ExtractJsonString(body, "confirmText") ?? "").Trim();
            string requiredConfirmText = risk.Operation.ToUpperInvariant();
            long affectedRowsPreview = EstimateSqlAffectedRows(ds, session.CurrentDatabase ?? "db", sql, risk.Operation);
            if (risk.RequiresConfirmation && (!confirmed || !string.Equals(confirmText, requiredConfirmText, StringComparison.OrdinalIgnoreCase))) {
                req.Context.StatusCode = "409 Conflict";
                WriteSqlAudit(req, session, "query.blocked", session.CurrentDatabase ?? "db", risk.Operation, "blocked", 0, risk.Reason, sql);
                return "{\"error\":\"" + EscapeJson(risk.Reason) + "\",\"requiresConfirmation\":true,\"risk\":{\"operation\":\"" +
                       EscapeJson(risk.Operation) + "\",\"severity\":\"" + EscapeJson(risk.Severity) + "\",\"reason\":\"" + EscapeJson(risk.Reason) +
                       "\",\"typedConfirmation\":\"" + EscapeJson(requiredConfirmText) + "\",\"affectedRowsPreview\":" + affectedRowsPreview + "}}";
            }

            string restorePointId = "";
            string beforeHash = "";
            if (risk.RequiresConfirmation && ds.Databases.TryGetValue(session.CurrentDatabase ?? "db", out var currentDb)) {
                beforeHash = ComputeSqlDatabaseSnapshotHash(currentDb);
                restorePointId = CreateSqlRestorePoint(ds, session, session.CurrentDatabase ?? "db", risk.Operation, "query." + risk.Operation, sql);
            }

            // Create a temporary SqlSession for query execution
            var sqlSession = new SqlSession {
                ConnectionId = Guid.NewGuid(),
                Username = session.Username,
                CurrentDatabase = session.CurrentDatabase ?? "db",
                ServerName = ds.ServerName,
                ServerVersion = ds.ServerVersion,
                IsAuthenticated = true
            };

            try {
                var result = ds.ExecuteQuery(sqlSession, sql);

                // If the QueryExecuting event didn't handle the query,
                // fall back to a basic in-memory table query.
                // DataServer owns execution for embedded, web, and wire clients.

                var sb = new StringBuilder();
                sb.Append("{");

                if (result.HasResultSet && result.Columns.Count > 0) {
                    sb.Append("\"columns\":[");
                    for (int c = 0; c < result.Columns.Count; c++) {
                        if (c > 0) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(result.Columns[c])).Append("\"");
                    }
                    sb.Append("],\"rows\":[");
                    for (int r = 0; r < result.Rows.Count; r++) {
                        if (r > 0) sb.Append(",");
                        sb.Append("[");
                        var row = result.Rows[r];
                        for (int c = 0; c < row.Length; c++) {
                            if (c > 0) sb.Append(",");
                            if (row[c] == null) {
                                sb.Append("null");
                            } else {
                                sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                            }
                        }
                        sb.Append("]");
                    }
                    sb.Append("],\"rowsAffected\":").Append(result.RowsAffected);
                } else {
                    sb.Append("\"rowsAffected\":").Append(result.RowsAffected);
                    sb.Append(",\"message\":\"Query executed successfully.\"");
                }

                sb.Append(",\"risk\":{\"operation\":\"").Append(EscapeJson(risk.Operation))
                  .Append("\",\"severity\":\"").Append(EscapeJson(risk.Severity))
                  .Append("\",\"confirmed\":").Append(confirmed ? "true" : "false")
                  .Append(",\"affectedRowsPreview\":").Append(affectedRowsPreview)
                  .Append("}");
                if (!string.IsNullOrWhiteSpace(restorePointId))
                    sb.Append(",\"restorePointId\":\"").Append(EscapeJson(restorePointId)).Append("\"");
                sb.Append("}");
                long auditRowsAffected = result.RowsAffected;
                string afterHash = beforeHash;
                if (!string.IsNullOrWhiteSpace(beforeHash) && ds.Databases.TryGetValue(sqlSession.CurrentDatabase ?? "db", out var afterDb))
                    afterHash = ComputeSqlDatabaseSnapshotHash(afterDb);
                WriteSqlAudit(req, session, "query.execute", sqlSession.CurrentDatabase ?? "db", risk.Operation, "success", auditRowsAffected, "", sql, beforeHash, afterHash, restorePointId, "query");
                return sb.ToString();
            } catch (Exception ex) {
                WriteSqlAudit(req, session, "query.execute", sqlSession.CurrentDatabase ?? "db", risk.Operation, "error", 0, ex.Message, sql);
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// Basic in-memory SQL handler for SELECT, INSERT, UPDATE, DELETE,
        /// CREATE TABLE, and DROP TABLE when no <see cref="DataServer.QueryExecuting"/>
        /// handler is wired up.
        /// </summary>
        private static QueryResult ExecuteInMemoryQuery(DataServer ds, SqlSession session, string sql) {
            var result = new QueryResult();
            // Normalize whitespace so keyword searches (e.g. " FROM ") match
            // regardless of whether the SQL uses spaces, tabs or newlines.
            string trimmed = sql.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            string upper = trimmed.ToUpperInvariant();

            // ---- USE database ----
            if (upper.StartsWith("USE ")) {
                var dbName = trimmed.Substring(4).Trim().TrimEnd(';').Trim();
                dbName = StripBrackets(dbName);
                if (ds.Databases.ContainsKey(dbName) && CanSqlSessionAccessDatabase(ds, session, dbName)) {
                    session.CurrentDatabase = dbName;
                    result.RowsAffected = 0;
                } else {
                    throw new Exception("Database '" + dbName + "' does not exist or is blocked by SQL Admin tenant isolation.");
                }
                return result;
            }

            // ---- SELECT ----
            if (upper.StartsWith("SELECT")) {
                // Parse TOP N
                int topN = int.MaxValue;
                string afterSelect = trimmed.Substring(6).TrimStart();
                string afterSelectUpper = afterSelect.ToUpperInvariant();
                if (afterSelectUpper.StartsWith("TOP ")) {
                    afterSelect = afterSelect.Substring(4).TrimStart();
                    // Handle TOP (N) or TOP N
                    string numStr;
                    if (afterSelect.StartsWith("(")) {
                        int closeParen = afterSelect.IndexOf(')');
                        if (closeParen > 0) {
                            numStr = afterSelect.Substring(1, closeParen - 1).Trim();
                            afterSelect = afterSelect.Substring(closeParen + 1).TrimStart();
                        } else {
                            numStr = "";
                        }
                    } else {
                        int spaceIdx = afterSelect.IndexOf(' ');
                        numStr = spaceIdx > 0 ? afterSelect.Substring(0, spaceIdx) : afterSelect;
                        afterSelect = spaceIdx > 0 ? afterSelect.Substring(spaceIdx).TrimStart() : "";
                    }
                    if (int.TryParse(numStr, out var parsed)) topN = parsed;
                }

                // Find table name from the FROM clause
                string tableName = null;
                string dbQualifier = null;
                int fromIdx = upper.IndexOf(" FROM ", StringComparison.Ordinal);
                if (fromIdx >= 0) {
                    string afterFrom = trimmed.Substring(fromIdx + 6).TrimStart();
                    tableName = ParseTableName(afterFrom, out dbQualifier);
                }

                if (tableName == null) {
                    // Could be something like SELECT 1, SELECT @@VERSION, etc.
                    result.HasResultSet = true;
                    result.Columns.Add("result");
                    result.RowsAffected = 0;
                    return result;
                }

                Database db;
                var table = ResolveTable(ds, session, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Parse column list (between SELECT [TOP N] and FROM)
                string colsPart = null;
                string selectSection = trimmed.Substring(6); // after "SELECT"
                int fromInSelect = selectSection.ToUpperInvariant().IndexOf(" FROM ", StringComparison.Ordinal);
                if (fromInSelect >= 0) {
                    colsPart = selectSection.Substring(0, fromInSelect).Trim();
                    // Strip TOP N from colsPart
                    string colsUpper = colsPart.ToUpperInvariant().TrimStart();
                    if (colsUpper.StartsWith("TOP ")) {
                        colsPart = colsPart.TrimStart();
                        colsPart = colsPart.Substring(4).TrimStart();
                        if (colsPart.StartsWith("(")) {
                            int cp = colsPart.IndexOf(')');
                            if (cp > 0) colsPart = colsPart.Substring(cp + 1).TrimStart();
                        } else {
                            int sp = colsPart.IndexOf(' ');
                            if (sp > 0) colsPart = colsPart.Substring(sp).TrimStart();
                            else colsPart = "*";
                        }
                    }
                }

                bool selectAll = string.IsNullOrWhiteSpace(colsPart) || colsPart.Trim() == "*";
                int[] colIndices;

                if (selectAll) {
                    result.Columns.AddRange(table.Columns.ConvertAll(c => c.Name));
                    colIndices = new int[table.Columns.Count];
                    for (int i = 0; i < colIndices.Length; i++) colIndices[i] = i;
                } else {
                    var requestedCols = colsPart.Split(',');
                    var indices = new List<int>();
                    foreach (var rc in requestedCols) {
                        var colName = StripBrackets(rc.Trim().TrimEnd(';'));
                        // Handle alias: col AS alias
                        int asIdx = colName.ToUpperInvariant().IndexOf(" AS ", StringComparison.Ordinal);
                        string alias = null;
                        if (asIdx >= 0) {
                            alias = colName.Substring(asIdx + 4).Trim();
                            colName = colName.Substring(0, asIdx).Trim();
                            colName = StripBrackets(colName);
                            alias = StripBrackets(alias);
                        }
                        int ci = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                        if (ci >= 0) {
                            indices.Add(ci);
                            result.Columns.Add(alias ?? table.Columns[ci].Name);
                        }
                    }
                    colIndices = indices.ToArray();
                }

                result.HasResultSet = true;

                // Parse WHERE clause if present
                string selectWhereClause = null;
                int selectWhereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                if (selectWhereIdx >= 0) {
                    string afterWhere = trimmed.Substring(selectWhereIdx + 7);
                    // Trim trailing ORDER BY, GROUP BY, HAVING clauses
                    string afterWhereUpper = afterWhere.ToUpperInvariant();
                    int endIdx = afterWhere.Length;
                    foreach (var kw in new[] { " ORDER BY ", " GROUP BY ", " HAVING " }) {
                        int kwIdx = afterWhereUpper.IndexOf(kw, StringComparison.Ordinal);
                        if (kwIdx >= 0 && kwIdx < endIdx) endIdx = kwIdx;
                    }
                    selectWhereClause = afterWhere.Substring(0, endIdx).Trim().TrimEnd(';');
                }

                // Parse ORDER BY clause if present
                string orderByClause = null;
                int orderByIdx = upper.IndexOf(" ORDER BY ", StringComparison.Ordinal);
                if (orderByIdx >= 0) {
                    string afterOrder = trimmed.Substring(orderByIdx + 10).Trim().TrimEnd(';');
                    orderByClause = afterOrder;
                }

                // Collect all matching rows (without TOP limit) so ORDER BY works correctly.
                // Simple equality predicates can use the DataServer key/value cache first.
                List<int> cachedRowIndexes = null;
                IEnumerable<int> rowIndexes = Enumerable.Range(0, table.Rows.Count);
                if (selectWhereClause != null && ds.TryGetCachedRowIndexes(
                    db?.Name ?? session.CurrentDatabase,
                    table.Name,
                    table,
                    selectWhereClause,
                    out cachedRowIndexes)) {
                    rowIndexes = cachedRowIndexes;
                }

                foreach (int r in rowIndexes) {
                    if (r < 0 || r >= table.Rows.Count)
                        continue;

                    var srcRow = table.Rows[r];
                    if (selectWhereClause != null && !RowMatchesWhere(table, srcRow, selectWhereClause))
                        continue;
                    var outRow = new object[colIndices.Length];
                    for (int c = 0; c < colIndices.Length; c++) {
                        int ci = colIndices[c];
                        outRow[c] = ci < srcRow.Length ? srcRow[ci] : null;
                    }
                    result.Rows.Add(outRow);
                }

                // Apply ORDER BY sorting before TOP limit
                if (!string.IsNullOrWhiteSpace(orderByClause)) {
                    ApplyOrderBy(result, table, orderByClause);
                }

                // Apply TOP N limit after sorting
                if (topN < result.Rows.Count) {
                    result.Rows.RemoveRange(topN, result.Rows.Count - topN);
                }

                result.RowsAffected = result.Rows.Count;
                return result;
            }

            // ---- INSERT ----
            if (upper.StartsWith("INSERT")) {
                string tableName = null;
                string dbQualifier = null;
                int intoIdx = upper.IndexOf("INTO ", StringComparison.Ordinal);
                if (intoIdx >= 0) {
                    string afterInto = trimmed.Substring(intoIdx + 5).TrimStart();
                    tableName = ParseTableName(afterInto, out dbQualifier);
                }
                if (tableName == null) throw new Exception("Could not parse INSERT statement.");
                Database db;
                var table = ResolveTable(ds, session, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Parse VALUES(...)
                int valuesIdx = upper.IndexOf("VALUES", StringComparison.Ordinal);
                if (valuesIdx < 0) throw new Exception("INSERT without VALUES is not supported.");
                string valuesSection = trimmed.Substring(valuesIdx + 6).Trim();
                int openParen = valuesSection.IndexOf('(');
                int closeParen = valuesSection.LastIndexOf(')');
                if (openParen < 0 || closeParen < 0) throw new Exception("Could not parse VALUES clause.");
                string valuesList = valuesSection.Substring(openParen + 1, closeParen - openParen - 1);
                var values = SplitValuesList(valuesList);

                var newRow = new object[table.Columns.Count];
                for (int i = 0; i < Math.Min(values.Count, table.Columns.Count); i++) {
                    string v = values[i].Trim().TrimStart('N'); // handle N'...'
                    if (v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) {
                        newRow[i] = null;
                    } else if (v.StartsWith("'") && v.EndsWith("'")) {
                        newRow[i] = ConvertValue(v.Substring(1, v.Length - 2), table.Columns[i].DataType);
                    } else {
                        newRow[i] = ConvertValue(v, table.Columns[i].DataType);
                    }
                }
                table.Rows.Add(newRow);
                ds.ScheduleSave();
                result.RowsAffected = 1;
                return result;
            }

            // ---- DELETE ----
            if (upper.StartsWith("DELETE")) {
                int fromIdx = upper.IndexOf("FROM ", StringComparison.Ordinal);
                string afterFrom = fromIdx >= 0 ? trimmed.Substring(fromIdx + 5).TrimStart() : trimmed.Substring(6).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterFrom, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse DELETE statement.");
                Database db;
                var table = ResolveTable(ds, session, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Simple WHERE support: DELETE FROM T WHERE col = 'value'
                int whereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                if (whereIdx >= 0) {
                    string whereClause = trimmed.Substring(whereIdx + 7).Trim().TrimEnd(';');
                    int removed = RemoveMatchingRows(ds, db?.Name ?? session.CurrentDatabase, table, whereClause);
                    result.RowsAffected = removed;
                } else {
                    result.RowsAffected = table.Rows.Count;
                    table.Rows.Clear();
                }
                ds.ScheduleSave();
                return result;
            }

            // ---- UPDATE ----
            if (upper.StartsWith("UPDATE")) {
                string afterUpdate = trimmed.Substring(6).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterUpdate, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse UPDATE statement.");
                Database db;
                var table = ResolveTable(ds, session, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                int setIdx = upper.IndexOf(" SET ", StringComparison.Ordinal);
                if (setIdx < 0) throw new Exception("UPDATE without SET clause.");
                int whereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                string setClause = whereIdx >= 0
                    ? trimmed.Substring(setIdx + 5, whereIdx - setIdx - 5).Trim()
                    : trimmed.Substring(setIdx + 5).Trim().TrimEnd(';');

                // Parse SET assignments: col1 = val1, col2 = val2
                var assignments = ParseAssignments(setClause, table);

                int updated = 0;
                List<int> updateRowIndexes = null;
                IEnumerable<int> updateCandidates = Enumerable.Range(0, table.Rows.Count);
                if (whereIdx >= 0) {
                    string whereClause = trimmed.Substring(whereIdx + 7).Trim().TrimEnd(';');
                    if (ds.TryGetCachedRowIndexes(
                        db?.Name ?? session.CurrentDatabase,
                        table.Name,
                        table,
                        whereClause,
                        out updateRowIndexes)) {
                        updateCandidates = updateRowIndexes;
                    }
                }

                foreach (int r in updateCandidates) {
                    if (r < 0 || r >= table.Rows.Count)
                        continue;

                    bool match = true;
                    if (whereIdx >= 0) {
                        string whereClause = trimmed.Substring(whereIdx + 7).Trim().TrimEnd(';');
                        match = RowMatchesWhere(table, table.Rows[r], whereClause);
                    }
                    if (match) {
                        var row = table.Rows[r];
                        if (row.Length < table.Columns.Count) {
                            var expanded = new object[table.Columns.Count];
                            Array.Copy(row, expanded, row.Length);
                            row = expanded;
                            table.Rows[r] = row;
                        }
                        foreach (var kvp in assignments) {
                            if (kvp.Key < row.Length) row[kvp.Key] = kvp.Value;
                        }
                        updated++;
                    }
                }
                ds.ScheduleSave();
                result.RowsAffected = updated;
                return result;
            }

            // ---- CREATE TABLE ----
            if (upper.StartsWith("CREATE TABLE")) {
                string afterCreate = trimmed.Substring(12).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterCreate, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse CREATE TABLE statement.");
                Database db;
                if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) { }
                else if (!ds.Databases.TryGetValue(session.CurrentDatabase, out db))
                    throw new Exception("Database '" + session.CurrentDatabase + "' not found.");
                if (db.Tables.ContainsKey(tableName))
                    throw new Exception("There is already an object named '" + tableName + "' in the database.");

                var newTable = new Table(tableName);
                int pOpen = afterCreate.IndexOf('(');
                int pClose = afterCreate.LastIndexOf(')');
                if (pOpen >= 0 && pClose > pOpen) {
                    string colDefs = afterCreate.Substring(pOpen + 1, pClose - pOpen - 1);
                    foreach (var colDef in SplitValuesList(colDefs)) {
                        var parts = colDef.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) {
                            string colName = StripBrackets(parts[0]);
                            string typeName = parts[1].TrimEnd(',');
                            int maxLen = -1;
                            if (typeName.Contains("(")) {
                                int lp = typeName.IndexOf('(');
                                int rp = typeName.IndexOf(')');
                                if (rp > lp) {
                                    string lenStr = typeName.Substring(lp + 1, rp - lp - 1);
                                    if (lenStr.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                                        maxLen = -1;
                                    else
                                        int.TryParse(lenStr, out maxLen);
                                    typeName = typeName.Substring(0, lp);
                                }
                            }
                            newTable.Columns.Add(new Column(colName, ResolveType(typeName), maxLen));
                        }
                    }
                }
                if (newTable.Columns.Count == 0)
                    newTable.Columns.Add(new Column("Id", typeof(int), -1));

                db.Tables[tableName] = newTable;
                ds.ScheduleSave();
                result.RowsAffected = 0;
                return result;
            }

            // ---- DROP TABLE ----
            if (upper.StartsWith("DROP TABLE")) {
                string afterDrop = trimmed.Substring(10).TrimStart();
                if (afterDrop.ToUpperInvariant().StartsWith("IF EXISTS "))
                    afterDrop = afterDrop.Substring(10).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterDrop, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse DROP TABLE statement.");
                Database db;
                if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) { }
                else if (!ds.Databases.TryGetValue(session.CurrentDatabase, out db))
                    throw new Exception("Database '" + session.CurrentDatabase + "' not found.");
                db.Tables.TryRemove(tableName, out _);
                ds.ScheduleSave();
                result.RowsAffected = 0;
                return result;
            }

            // ---- TRUNCATE TABLE ----
            if (upper.StartsWith("TRUNCATE TABLE")) {
                string afterTrunc = trimmed.Substring(14).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterTrunc, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse TRUNCATE TABLE statement.");
                Database db;
                var table = ResolveTable(ds, session, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");
                result.RowsAffected = table.Rows.Count;
                table.Rows.Clear();
                ds.ScheduleSave();
                return result;
            }

            return result;
        }

        #region In-Memory SQL Helpers

        private static string StripBrackets(string name) {
            if (string.IsNullOrEmpty(name)) return name;
            name = name.Trim().TrimEnd(';');
            if (name.StartsWith("[") && name.EndsWith("]"))
                name = name.Substring(1, name.Length - 2);
            if (name.StartsWith("\"") && name.EndsWith("\""))
                name = name.Substring(1, name.Length - 2);
            return name;
        }

        private static string ParseTableName(string fragment) {
            return ParseTableName(fragment, out _);
        }

        private static string ParseTableName(string fragment, out string dbQualifier) {
            dbQualifier = null;
            if (string.IsNullOrWhiteSpace(fragment)) return null;
            fragment = fragment.TrimStart();

            // Collect all dot-separated identifier parts: e.g. [Accounts].[dbo].[Uploads] or Accounts.Uploads
            var parts = new List<string>();
            int pos = 0;
            while (pos < fragment.Length) {
                string part;
                if (fragment[pos] == '[') {
                    int close = fragment.IndexOf(']', pos + 1);
                    if (close < 0) break;
                    part = fragment.Substring(pos + 1, close - pos - 1);
                    pos = close + 1;
                } else {
                    int end = pos;
                    while (end < fragment.Length && fragment[end] != '.' && fragment[end] != ' '
                           && fragment[end] != '\t' && fragment[end] != '(' && fragment[end] != ';'
                           && fragment[end] != ',' && fragment[end] != '\r' && fragment[end] != '\n')
                        end++;
                    part = fragment.Substring(pos, end - pos);
                    pos = end;
                }
                parts.Add(StripBrackets(part));
                // Consume a dot separator
                if (pos < fragment.Length && fragment[pos] == '.') { pos++; continue; }
                break;
            }

            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) {
                // Could be schema.table or database.table ? return the first
                // part as the qualifier so the caller can check it as a db name.
                dbQualifier = parts[0];
                return parts[1];
            }
            // 3+ parts: database.schema.table ? first is db, last is table
            dbQualifier = parts[0];
            return parts[parts.Count - 1];
        }

        /// <summary>
        /// Resolves a potentially qualified table reference against the DataServer.
        /// When <paramref name="dbQualifier"/> names an existing database, the table
        /// is looked up there; otherwise the qualifier is treated as a schema (ignored)
        /// and the table is looked up in the session's current database.
        /// Automatically tries <c>dbo.</c>-prefixed and unprefixed variants so that
        /// <c>SELECT * FROM Uploads</c> finds <c>dbo.Uploads</c> and vice-versa.
        /// </summary>
        private static Table ResolveTable(DataServer ds, SqlSession session, string dbQualifier, string tableName, out Database db) {
            db = null;
            if (tableName == null) return null;
            string defaultDbName = session?.CurrentDatabase ?? "";

            // If there's a qualifier and it matches a known database, use that database.
            if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) {
                if (!CanSqlSessionAccessDatabase(ds, session, dbQualifier))
                    return null;
                var tbl = FindTableFuzzy(db, tableName);
                return tbl;
            }

            // Fall back to the default (current session) database.
            if (!ds.Databases.TryGetValue(defaultDbName, out db)) {
                // Also search all databases if the default doesn't contain the table
                foreach (var kvp in ds.Databases) {
                    if (!CanSqlSessionAccessDatabase(ds, session, kvp.Key))
                        continue;
                    var found = FindTableFuzzy(kvp.Value, tableName);
                    if (found != null) { db = kvp.Value; return found; }
                }
                return null;
            }
            if (!CanSqlSessionAccessDatabase(ds, session, defaultDbName))
                return null;
            var fallback = FindTableFuzzy(db, tableName);
            if (fallback != null) return fallback;

            // Table not in default database — search all databases
            foreach (var kvp in ds.Databases) {
                if (kvp.Key.Equals(defaultDbName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!CanSqlSessionAccessDatabase(ds, session, kvp.Key))
                    continue;
                var found = FindTableFuzzy(kvp.Value, tableName);
                if (found != null) { db = kvp.Value; return found; }
            }
            return null;
        }

        /// <summary>
        /// Looks up a table by name, automatically trying <c>dbo.</c>-prefixed
        /// and unprefixed variants to handle schema-qualified storage.
        /// </summary>
        private static Table FindTableFuzzy(Database db, string tableName) {
            // Exact match first
            if (db.Tables.TryGetValue(tableName, out var table)) return table;

            // Try with dbo. prefix
            if (!tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) {
                if (db.Tables.TryGetValue("dbo." + tableName, out table)) return table;
            }

            // Try without dbo. prefix
            if (tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) {
                if (db.Tables.TryGetValue(tableName.Substring(4), out table)) return table;
            }

            return null;
        }

        private static List<string> SplitValuesList(string list) {
            var result = new List<string>();
            int depth = 0;
            bool inString = false;
            var current = new StringBuilder();
            for (int i = 0; i < list.Length; i++) {
                char ch = list[i];
                if (ch == '\'' && !inString) { inString = true; current.Append(ch); }
                else if (ch == '\'' && inString) {
                    current.Append(ch);
                    // Check for escaped quote ''
                    if (i + 1 < list.Length && list[i + 1] == '\'') { current.Append('\''); i++; }
                    else inString = false;
                }
                else if (inString) { current.Append(ch); }
                else if (ch == '(') { depth++; current.Append(ch); }
                else if (ch == ')') { depth--; current.Append(ch); }
                else if (ch == ',' && depth == 0) { result.Add(current.ToString()); current.Clear(); }
                else { current.Append(ch); }
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private static bool RowMatchesWhere(Table table, object[] row, string whereClause) {
            if (string.IsNullOrWhiteSpace(whereClause)) return true;
            whereClause = whereClause.Trim().TrimEnd(';');

            // Split on AND / OR (top-level only, outside quotes)
            var parts = SplitWhereOnLogical(whereClause, out var operators);
            if (parts.Count == 0) return true;

            bool result = EvalSingleCondition(table, row, parts[0]);
            for (int i = 0; i < operators.Count; i++) {
                bool next = EvalSingleCondition(table, row, parts[i + 1]);
                if (operators[i]) // true = AND, false = OR
                    result = result && next;
                else
                    result = result || next;
            }
            return result;
        }

        /// <summary>
        /// Splits a WHERE clause on top-level AND / OR keywords (outside string literals).
        /// Returns the condition parts and a parallel list of operators (true=AND, false=OR).
        /// </summary>
        private static List<string> SplitWhereOnLogical(string clause, out List<bool> operators) {
            operators = new List<bool>();
            var parts = new List<string>();
            int depth = 0;
            bool inStr = false;
            var current = new StringBuilder();

            for (int i = 0; i < clause.Length; i++) {
                char ch = clause[i];
                if (ch == '\'' && !inStr) { inStr = true; current.Append(ch); continue; }
                if (ch == '\'' && inStr) {
                    current.Append(ch);
                    if (i + 1 < clause.Length && clause[i + 1] == '\'') { current.Append('\''); i++; }
                    else inStr = false;
                    continue;
                }
                if (inStr) { current.Append(ch); continue; }
                if (ch == '(') { depth++; current.Append(ch); continue; }
                if (ch == ')') { depth--; current.Append(ch); continue; }

                if (depth == 0) {
                    // Check for AND / OR keyword boundary
                    string remaining = clause.Substring(i);
                    string remainUpper = remaining.ToUpperInvariant();
                    if (remainUpper.StartsWith("AND ") || remainUpper.StartsWith("AND\t") || remainUpper.StartsWith("AND\r") || remainUpper.StartsWith("AND\n")) {
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                        operators.Add(true);
                        i += 2; // skip "AND", loop will advance past the last char
                        continue;
                    }
                    if (remainUpper.StartsWith("OR ") || remainUpper.StartsWith("OR\t") || remainUpper.StartsWith("OR\r") || remainUpper.StartsWith("OR\n")) {
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                        operators.Add(false);
                        i += 1; // skip "OR", loop will advance past the last char
                        continue;
                    }
                }
                current.Append(ch);
            }
            if (current.Length > 0) parts.Add(current.ToString().Trim());
            return parts;
        }

        /// <summary>
        /// Evaluates a single WHERE condition (no AND/OR) against a row.
        /// Supports: =, !=, &lt;&gt;, &gt;, &lt;, &gt;=, &lt;=, LIKE, NOT LIKE, IS NULL, IS NOT NULL, IN.
        /// </summary>
        private static bool EvalSingleCondition(Table table, object[] row, string cond) {
            if (string.IsNullOrWhiteSpace(cond)) return true;
            cond = cond.Trim();

            // Strip surrounding parentheses
            while (cond.StartsWith("(") && cond.EndsWith(")")) {
                cond = cond.Substring(1, cond.Length - 2).Trim();
            }

            string condUpper = cond.ToUpperInvariant();

            // IS NOT NULL / IS NULL
            int isIdx = FindKeyword(condUpper, " IS ");
            if (isIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, isIdx).Trim());
                string rest = cond.Substring(isIdx + 4).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length) return false;
                bool isNull = row[colIdx] == null;
                if (rest.ToUpperInvariant().StartsWith("NOT"))
                    return !isNull;
                return isNull;
            }

            // NOT LIKE
            int notLikeIdx = FindKeyword(condUpper, " NOT LIKE ");
            if (notLikeIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, notLikeIdx).Trim());
                string pattern = cond.Substring(notLikeIdx + 10).Trim();
                pattern = UnquoteString(pattern);
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                return !MatchLikePattern(row[colIdx].ToString(), pattern);
            }

            // LIKE
            int likeIdx = FindKeyword(condUpper, " LIKE ");
            if (likeIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, likeIdx).Trim());
                string pattern = cond.Substring(likeIdx + 6).Trim();
                pattern = UnquoteString(pattern);
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                return MatchLikePattern(row[colIdx].ToString(), pattern);
            }

            // NOT IN
            int notInIdx = FindKeyword(condUpper, " NOT IN ");
            if (notInIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, notInIdx).Trim());
                string rest = cond.Substring(notInIdx + 8).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                var inValues = ParseInList(rest);
                string cellStr = row[colIdx].ToString();
                return !inValues.Any(v => v.Equals(cellStr, StringComparison.OrdinalIgnoreCase));
            }

            // IN
            int inIdx = FindKeyword(condUpper, " IN ");
            if (inIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, inIdx).Trim());
                string rest = cond.Substring(inIdx + 4).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                var inValues = ParseInList(rest);
                string cellStr = row[colIdx].ToString();
                return inValues.Any(v => v.Equals(cellStr, StringComparison.OrdinalIgnoreCase));
            }

            // Comparison operators: !=, <>, >=, <=, >, <, =
            string op;
            int opIdx;
            if ((opIdx = FindOperator(cond, "!=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<>", out op)) >= 0 ||
                (opIdx = FindOperator(cond, ">=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, ">", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "=", out op)) >= 0) {

                string colName = StripBrackets(cond.Substring(0, opIdx).Trim());
                string valStr = cond.Substring(opIdx + op.Length).Trim().TrimEnd(';');

                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length) return false;

                valStr = UnquoteString(valStr);
                var cellVal = row[colIdx];

                if (cellVal == null)
                    return (op == "=" || op == "==") && valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        || (op == "!=" || op == "<>") && !valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase);

                string cellStr = cellVal.ToString();

                switch (op) {
                    case "=":
                        return cellStr.Equals(valStr, StringComparison.OrdinalIgnoreCase);
                    case "!=":
                    case "<>":
                        return !cellStr.Equals(valStr, StringComparison.OrdinalIgnoreCase);
                    case ">":
                    case ">=":
                    case "<":
                    case "<=":
                        if (double.TryParse(cellStr, out var cellNum) && double.TryParse(valStr, out var valNum)) {
                            switch (op) {
                                case ">": return cellNum > valNum;
                                case ">=": return cellNum >= valNum;
                                case "<": return cellNum < valNum;
                                case "<=": return cellNum <= valNum;
                            }
                        }
                        int cmp = string.Compare(cellStr, valStr, StringComparison.OrdinalIgnoreCase);
                        switch (op) {
                            case ">": return cmp > 0;
                            case ">=": return cmp >= 0;
                            case "<": return cmp < 0;
                            case "<=": return cmp <= 0;
                        }
                        break;
                }
            }

            return true; // can't parse, match all
        }

        /// <summary>
        /// Finds the position of a comparison operator in a condition string,
        /// skipping characters inside string literals.
        /// </summary>
        private static int FindOperator(string cond, string op, out string foundOp) {
            foundOp = op;
            bool inStr = false;
            for (int i = 0; i < cond.Length - op.Length + 1; i++) {
                char ch = cond[i];
                if (ch == '\'') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (cond.Substring(i, op.Length) == op) {
                    foundOp = op;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Finds a keyword (e.g. " LIKE ") in the uppercase condition, skipping string literals.
        /// </summary>
        private static int FindKeyword(string condUpper, string keyword) {
            bool inStr = false;
            for (int i = 0; i <= condUpper.Length - keyword.Length; i++) {
                char ch = condUpper[i];
                if (ch == '\'') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (condUpper.Substring(i, keyword.Length) == keyword)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Removes surrounding single quotes or N'' prefix from a SQL value string.
        /// </summary>
        private static string UnquoteString(string val) {
            if (string.IsNullOrEmpty(val)) return val;
            val = val.TrimEnd(';').Trim();
            if (val.StartsWith("N'") && val.EndsWith("'"))
                val = val.Substring(2, val.Length - 3);
            else if (val.StartsWith("'") && val.EndsWith("'"))
                val = val.Substring(1, val.Length - 2);
            return val.Replace("''", "'");
        }

        /// <summary>
        /// SQL LIKE pattern matching. Supports % (any characters) and _ (single character).
        /// </summary>
        private static bool MatchLikePattern(string value, string pattern) {
            // Convert SQL LIKE pattern to simple matching
            // % = match any sequence, _ = match single char
            int vi = 0, pi = 0;
            int starVi = -1, starPi = -1;

            while (vi < value.Length) {
                if (pi < pattern.Length && pattern[pi] == '%') {
                    starPi = pi++;
                    starVi = vi;
                } else if (pi < pattern.Length && (pattern[pi] == '_' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(value[vi]))) {
                    pi++;
                    vi++;
                } else if (starPi >= 0) {
                    pi = starPi + 1;
                    vi = ++starVi;
                } else {
                    return false;
                }
            }
            while (pi < pattern.Length && pattern[pi] == '%') pi++;
            return pi == pattern.Length;
        }

        /// <summary>
        /// Parses an IN (...) value list, returning unquoted string values.
        /// </summary>
        private static List<string> ParseInList(string rest) {
            var values = new List<string>();
            rest = rest.Trim();
            if (rest.StartsWith("(")) rest = rest.Substring(1);
            if (rest.EndsWith(")")) rest = rest.Substring(0, rest.Length - 1);
            foreach (var item in SplitValuesList(rest)) {
                values.Add(UnquoteString(item.Trim()));
            }
            return values;
        }

        private static int RemoveMatchingRows(DataServer ds, string dbName, Table table, string whereClause) {
            int removed = 0;
            List<int> cachedRowIndexes = null;
            IEnumerable<int> removeCandidates = Enumerable.Range(0, table.Rows.Count);
            if (ds != null && ds.TryGetCachedRowIndexes(dbName, table.Name, table, whereClause, out cachedRowIndexes))
                removeCandidates = cachedRowIndexes;

            foreach (int i in removeCandidates.Distinct().OrderByDescending(i => i)) {
                if (i < 0 || i >= table.Rows.Count)
                    continue;

                if (RowMatchesWhere(table, table.Rows[i], whereClause)) {
                    table.Rows.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Sorts result rows by an ORDER BY clause. Supports multiple columns
        /// and ASC/DESC direction per column. Uses the original table column names
        /// for lookup and falls back to the result column list when needed.
        /// </summary>
        private static void ApplyOrderBy(QueryResult result, Table table, string orderByClause) {
            if (result.Rows.Count <= 1 || string.IsNullOrWhiteSpace(orderByClause)) return;

            var parts = SplitValuesList(orderByClause);
            var orderSpecs = new List<KeyValuePair<int, bool>>(); // result column index, descending

            foreach (var part in parts) {
                var token = part.Trim().TrimEnd(';');
                if (string.IsNullOrWhiteSpace(token)) continue;

                bool desc = false;
                string colToken = token;
                string tokenUpper = token.ToUpperInvariant();

                if (tokenUpper.EndsWith(" DESC")) {
                    desc = true;
                    colToken = token.Substring(0, token.Length - 5).Trim();
                } else if (tokenUpper.EndsWith(" ASC")) {
                    colToken = token.Substring(0, token.Length - 4).Trim();
                }

                colToken = StripBrackets(colToken);

                // Find column in result set by name
                int colIdx = -1;
                for (int i = 0; i < result.Columns.Count; i++) {
                    if (result.Columns[i].Equals(colToken, StringComparison.OrdinalIgnoreCase)) {
                        colIdx = i;
                        break;
                    }
                }
                if (colIdx >= 0) {
                    orderSpecs.Add(new KeyValuePair<int, bool>(colIdx, desc));
                }
            }

            if (orderSpecs.Count == 0) return;

            result.Rows.Sort((a, b) => {
                foreach (var spec in orderSpecs) {
                    int ci = spec.Key;
                    bool descending = spec.Value;
                    var va = ci < a.Length ? a[ci] : null;
                    var vb = ci < b.Length ? b[ci] : null;

                    int cmp;
                    if (va == null && vb == null) cmp = 0;
                    else if (va == null) cmp = -1;
                    else if (vb == null) cmp = 1;
                    else if (double.TryParse(va.ToString(), out var na) && double.TryParse(vb.ToString(), out var nb))
                        cmp = na.CompareTo(nb);
                    else
                        cmp = string.Compare(va.ToString(), vb.ToString(), StringComparison.OrdinalIgnoreCase);

                    if (cmp != 0) return descending ? -cmp : cmp;
                }
                return 0;
            });
        }

        private static Dictionary<int, object> ParseAssignments(string setClause, Table table) {
            var assignments = new Dictionary<int, object>();
            var parts = SplitValuesList(setClause);
            foreach (var part in parts) {
                int eqIdx = part.IndexOf('=');
                if (eqIdx < 0) continue;
                string colName = StripBrackets(part.Substring(0, eqIdx).Trim());
                string valStr = part.Substring(eqIdx + 1).Trim().TrimEnd(';');
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0) continue;

                object val;
                if (valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase)) {
                    val = null;
                } else if (valStr.StartsWith("'") && valStr.EndsWith("'")) {
                    val = ConvertValue(valStr.Substring(1, valStr.Length - 2), table.Columns[colIdx].DataType);
                } else if (valStr.StartsWith("N'") && valStr.EndsWith("'")) {
                    val = ConvertValue(valStr.Substring(2, valStr.Length - 3), table.Columns[colIdx].DataType);
                } else {
                    val = ConvertValue(valStr, table.Columns[colIdx].DataType);
                }
                assignments[colIdx] = val;
            }
            return assignments;
        }

        #endregion

        private object ApiUsers(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"users\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"users\":[");
            bool first = true;
            foreach (var user in ds.Users.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(EscapeJson(user)).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiSessions(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"sessions\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"tdsSessions\":[");
            bool first = true;
            foreach (var kvp in ds.Sessions) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"connectionId\":\"").Append(kvp.Key.ToString("N").Substring(0, 8))
                  .Append("\",\"username\":\"").Append(EscapeJson(kvp.Value.Username ?? ""))
                  .Append("\",\"database\":\"").Append(EscapeJson(kvp.Value.CurrentDatabase ?? ""))
                  .Append("\",\"authenticated\":").Append(kvp.Value.IsAuthenticated ? "true" : "false")
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiAuditRecent(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) return "{\"entries\":[]}";

            int take = 100;
            if (req.QueryParameters != null && req.QueryParameters.ContainsKey("take") && int.TryParse(req.QueryParameters["take"], out var parsedTake))
                take = Math.Min(Math.Max(parsedTake, 1), 500);

            var table = GetSqlAuditTable(ds);
            var rows = new List<object[]>();
            lock (table) {
                for (int i = table.Rows.Count - 1; i >= 0 && rows.Count < take; i--)
                    rows.Add(NormalizeSqlAuditRow(table.Rows[i]));
            }

            var sb = new StringBuilder();
            sb.Append("{\"entries\":[");
            for (int i = 0; i < rows.Count; i++) {
                if (i > 0) sb.Append(",");
                var row = rows[i];
                sb.Append("{\"id\":\"").Append(EscapeJson(GetRow(row, 0)))
                  .Append("\",\"createdUtc\":\"").Append(EscapeJson(GetRow(row, 1)))
                  .Append("\",\"username\":\"").Append(EscapeJson(GetRow(row, 2)))
                  .Append("\",\"clientIp\":\"").Append(EscapeJson(GetRow(row, 3)))
                  .Append("\",\"action\":\"").Append(EscapeJson(GetRow(row, 4)))
                  .Append("\",\"database\":\"").Append(EscapeJson(GetRow(row, 5)))
                  .Append("\",\"target\":\"").Append(EscapeJson(GetRow(row, 6)))
                  .Append("\",\"status\":\"").Append(EscapeJson(GetRow(row, 7)))
                  .Append("\",\"rowsAffected\":\"").Append(EscapeJson(GetRow(row, 8)))
                  .Append("\",\"message\":\"").Append(EscapeJson(GetRow(row, 9)))
                  .Append("\",\"sqlHash\":\"").Append(EscapeJson(GetRow(row, 10)))
                  .Append("\",\"sqlPreview\":\"").Append(EscapeJson(GetRow(row, 11)))
                  .Append("\",\"ownerKey\":\"").Append(EscapeJson(GetRow(row, 12)))
                  .Append("\",\"sessionId\":\"").Append(EscapeJson(GetRow(row, 13)))
                  .Append("\",\"beforeHash\":\"").Append(EscapeJson(GetRow(row, 14)))
                  .Append("\",\"afterHash\":\"").Append(EscapeJson(GetRow(row, 15)))
                  .Append("\",\"correlationId\":\"").Append(EscapeJson(GetRow(row, 16)))
                  .Append("\",\"scope\":\"").Append(EscapeJson(GetRow(row, 17)))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiOperationsDashboard(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) return "{\"databases\":[],\"activeSqlAdminSessions\":0}";

            int databaseCount = 0;
            int tableCount = 0;
            long rowCount = 0;
            var hotTables = new List<object>();
            foreach (var dbKvp in ds.Databases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbKvp.Key))
                    continue;
                databaseCount++;
                foreach (var tableKvp in dbKvp.Value.Tables) {
                    tableCount++;
                    long rows = tableKvp.Value?.Rows?.Count ?? 0;
                    rowCount += rows;
                    hotTables.Add(new {
                        database = dbKvp.Key,
                        table = tableKvp.Key,
                        rows,
                        columns = tableKvp.Value?.Columns?.Count ?? 0
                    });
                }
            }

            var audit = GetSqlAuditTable(ds);
            int destructiveOperations = 0;
            int failedOperations = 0;
            int apiInvocations = 0;
            int eventFailures = _eventCircuitFailures.Values.Sum();
            lock (audit) {
                foreach (var row in audit.Rows.Select(NormalizeSqlAuditRow).Reverse().Take(500)) {
                    string database = GetRow(row, 5);
                    if (!string.IsNullOrWhiteSpace(database) && !CanSqlAdminSessionAccessDatabase(req, session, ds, database))
                        continue;
                    string action = GetRow(row, 4);
                    string status = GetRow(row, 7);
                    string target = GetRow(row, 6);
                    if (action.IndexOf("drop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        action.IndexOf("delete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        action.IndexOf("schema", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        target.Equals("drop", StringComparison.OrdinalIgnoreCase) ||
                        target.Equals("truncate", StringComparison.OrdinalIgnoreCase))
                        destructiveOperations++;
                    if (status.Equals("error", StringComparison.OrdinalIgnoreCase) || status.Equals("blocked", StringComparison.OrdinalIgnoreCase))
                        failedOperations++;
                    if (action.StartsWith("api-endpoint.", StringComparison.OrdinalIgnoreCase))
                        apiInvocations++;
                }
            }

            var backups = GetSqlRestorePointTable(ds);
            int restorePoints = 0;
            lock (backups) {
                restorePoints = backups.Rows.Select(NormalizeSqlRestorePointRow)
                    .Count(row => CanSqlAdminSessionAccessDatabase(req, session, ds, GetRow(row, 5)));
            }
            int errorDiagnostics = 0;
            if (CanSqlAdminSessionAccessDatabase(req, session, ds, "SocketJack")) {
                var errorTable = GetErrorDiagnosticsTable(ds);
                lock (errorTable)
                    errorDiagnostics = errorTable.Rows.Count;
            }

            var payload = new {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                ownerKey = session.OwnerKey ?? "",
                username = session.Username ?? "",
                activeSqlAdminSessions = _sessions.Values.Count(s => s != null && !s.Revoked),
                activeTdsSessions = ds.Sessions?.Count ?? 0,
                databases = databaseCount,
                tables = tableCount,
                rows = rowCount,
                hotTables = hotTables.OrderByDescending(item => (long)item.GetType().GetProperty("rows").GetValue(item)).Take(10).ToList(),
                apiEndpoints = _apiEndpoints.Count,
                runtimeApiRoutes = _apiRouteSettings.Count,
                events = _eventDefs.Count,
                eventCircuitFailures = eventFailures,
                recentDestructiveOperations = destructiveOperations,
                recentFailedOrBlockedOperations = failedOperations,
                recentApiInvocations = apiInvocations,
                errorDiagnostics,
                restorePoints,
                safety = new {
                    ownerUsernameIsolation = true,
                    csrfForRemoteMutations = true,
                    restorePointsBeforeRiskyActions = true,
                    dynamicEndpointPolicies = true,
                    eventCircuitBreakers = true
                }
            };
            return JsonSerializer.Serialize(payload);
        }

        private object ApiErrorDiagnosticsRecent(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) return "{\"entries\":[]}";
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, "SocketJack"))
                return SqlTenantForbiddenJson(req, "SocketJack");

            int take = ReadTakeQuery(req, 100, 500);
            var rows = ReadRecentErrorDiagnosticsRows(ds, take);
            return JsonSerializer.Serialize(new {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                database = "SocketJack",
                table = ErrorDiagnosticsTableName,
                entries = rows.Select(ErrorDiagnosticsRowToDto).ToList()
            });
        }

        private List<object[]> ReadRecentErrorDiagnosticsRows(DataServer ds, int take) {
            var table = GetErrorDiagnosticsTable(ds);
            var rows = new List<object[]>();
            lock (table) {
                var normalized = table.Rows.Select(NormalizeErrorDiagnosticsRow)
                    .OrderByDescending(row => GetRow(row, 2))
                    .Take(Math.Min(Math.Max(take, 1), 500));
                rows.AddRange(normalized);
            }
            return rows;
        }

        private object ErrorDiagnosticsRowToDto(object[] row) {
            row = NormalizeErrorDiagnosticsRow(row);
            return new {
                id = GetRow(row, 0),
                createdUtc = GetRow(row, 1),
                lastSeenUtc = GetRow(row, 2),
                severity = GetRow(row, 3),
                category = GetRow(row, 4),
                source = GetRow(row, 5),
                route = GetRow(row, 6),
                ownerKey = GetRow(row, 7),
                message = GetRow(row, 8),
                detail = GetRow(row, 9),
                exceptionType = GetRow(row, 10),
                stackTrace = GetRow(row, 11),
                fingerprint = GetRow(row, 12),
                count = ParsePositiveInt(GetRow(row, 13), 1)
            };
        }

        private static int ReadTakeQuery(HttpRequest req, int defaultValue, int maxValue) {
            int take = defaultValue;
            if (req?.QueryParameters != null && req.QueryParameters.ContainsKey("take"))
                take = ParsePositiveInt(req.QueryParameters["take"], defaultValue);
            return Math.Min(Math.Max(take, 1), Math.Max(1, maxValue));
        }

        private Table GetErrorDiagnosticsTable(DataServer ds) {
            var db = ds.Databases.GetOrAdd("SocketJack", _ => new Database("SocketJack"));
            var table = db.Tables.GetOrAdd(ErrorDiagnosticsTableName, _ => new Table(ErrorDiagnosticsTableName));
            EnsureErrorDiagnosticsColumns(table);
            if (table.Rows == null) table.Rows = new List<object[]>();
            lock (table) {
                for (int i = 0; i < table.Rows.Count; i++)
                    table.Rows[i] = NormalizeErrorDiagnosticsRow(table.Rows[i]);
            }
            return table;
        }

        private static void EnsureErrorDiagnosticsColumns(Table table) {
            if (table.Columns == null) table.Columns = new List<Column>();
            EnsureColumn(table, 0, "Id", 80);
            EnsureColumn(table, 1, "CreatedUtc", 80);
            EnsureColumn(table, 2, "LastSeenUtc", 80);
            EnsureColumn(table, 3, "Severity", 48);
            EnsureColumn(table, 4, "Category", 120);
            EnsureColumn(table, 5, "Source", 240);
            EnsureColumn(table, 6, "Route", 240);
            EnsureColumn(table, 7, "OwnerKey", 180);
            EnsureColumn(table, 8, "Message", -1);
            EnsureColumn(table, 9, "Detail", -1);
            EnsureColumn(table, 10, "ExceptionType", 240);
            EnsureColumn(table, 11, "StackTrace", -1);
            EnsureColumn(table, 12, "Fingerprint", 96);
            EnsureColumn(table, 13, "Count", 32);
        }

        private static object[] NormalizeErrorDiagnosticsRow(object[] row) {
            var normalized = new object[14];
            if (row != null) {
                int copy = Math.Min(row.Length, normalized.Length);
                for (int i = 0; i < copy; i++)
                    normalized[i] = row[i];
            }
            string now = DateTime.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(normalized[0]?.ToString())) normalized[0] = "err_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(normalized[1]?.ToString())) normalized[1] = now;
            if (string.IsNullOrWhiteSpace(normalized[2]?.ToString())) normalized[2] = normalized[1];
            for (int i = 3; i <= 12; i++) {
                if (normalized[i] == null) normalized[i] = "";
            }
            if (string.IsNullOrWhiteSpace(normalized[13]?.ToString())) normalized[13] = "1";
            return normalized;
        }

        private Table GetSqlAuditTable(DataServer ds) {
            var db = ds.Databases.GetOrAdd("SocketJack", _ => new Database("SocketJack"));
            var table = db.Tables.GetOrAdd(SqlAdminAuditTableName, _ => new Table(SqlAdminAuditTableName));
            EnsureSqlAuditColumns(table);
            if (table.Rows == null) table.Rows = new List<object[]>();
            lock (table) {
                for (int i = 0; i < table.Rows.Count; i++)
                    table.Rows[i] = NormalizeSqlAuditRow(table.Rows[i]);
            }
            return table;
        }

        private static void EnsureSqlAuditColumns(Table table) {
            if (table.Columns == null) table.Columns = new List<Column>();
            EnsureColumn(table, 0, "Id", 80);
            EnsureColumn(table, 1, "CreatedUtc", 80);
            EnsureColumn(table, 2, "Username", 160);
            EnsureColumn(table, 3, "ClientIp", 160);
            EnsureColumn(table, 4, "Action", 80);
            EnsureColumn(table, 5, "DatabaseName", 160);
            EnsureColumn(table, 6, "Target", 240);
            EnsureColumn(table, 7, "Status", 32);
            EnsureColumn(table, 8, "RowsAffected", 32);
            EnsureColumn(table, 9, "Message", 1024);
            EnsureColumn(table, 10, "SqlHash", 96);
            EnsureColumn(table, 11, "SqlPreview", 2048);
            EnsureColumn(table, 12, "OwnerKey", 180);
            EnsureColumn(table, 13, "SessionId", 120);
            EnsureColumn(table, 14, "BeforeHash", 96);
            EnsureColumn(table, 15, "AfterHash", 96);
            EnsureColumn(table, 16, "CorrelationId", 120);
            EnsureColumn(table, 17, "Scope", 160);
        }

        private static void EnsureColumn(Table table, int index, string name, int maxLength) {
            while (table.Columns.Count <= index)
                table.Columns.Add(new Column());
            table.Columns[index] = new Column(name, typeof(string), maxLength);
        }

        private static object[] NormalizeSqlAuditRow(object[] row) {
            var normalized = new object[18];
            if (row != null) {
                int copy = Math.Min(row.Length, normalized.Length);
                for (int i = 0; i < copy; i++)
                    normalized[i] = row[i];
            }
            if (string.IsNullOrWhiteSpace(normalized[0]?.ToString())) normalized[0] = "audit_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(normalized[1]?.ToString())) normalized[1] = DateTime.UtcNow.ToString("O");
            for (int i = 2; i < normalized.Length; i++) {
                if (normalized[i] == null) normalized[i] = "";
            }
            return normalized;
        }

        private static string GetRow(object[] row, int index) {
            return row != null && index >= 0 && index < row.Length && row[index] != null ? row[index].ToString() : "";
        }

        private void WriteSqlAudit(HttpRequest req, SqlAdminSession session, string action, string database, string target, string status, long rowsAffected, string message, string sql, string beforeHash = "", string afterHash = "", string correlationId = "", string scope = "") {
            try {
                var ds = GetDataServer();
                if (ds == null) return;
                var table = GetSqlAuditTable(ds);
                var row = NormalizeSqlAuditRow(new object[] {
                    "audit_" + Guid.NewGuid().ToString("N"),
                    DateTime.UtcNow.ToString("O"),
                    session?.Username ?? "",
                    ExtractSqlAdminClientIp(req),
                    action ?? "",
                    database ?? "",
                    target ?? "",
                    status ?? "",
                    rowsAffected.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    TruncateAuditText(message, 1000),
                    ComputeSqlAuditHash(sql),
                    TruncateAuditText(NormalizeSqlPreview(sql), 2000),
                    session?.OwnerKey ?? BuildSqlOwnerKey(session?.Username ?? ""),
                    session?.SessionId ?? "",
                    beforeHash ?? "",
                    afterHash ?? "",
                    string.IsNullOrWhiteSpace(correlationId) ? "corr_" + Guid.NewGuid().ToString("N") : correlationId,
                    scope ?? ""
                });
                lock (table) {
                    table.Rows.Add(row);
                    while (table.Rows.Count > 2000)
                        table.Rows.RemoveAt(0);
                }
                ds.ScheduleSave();
            } catch { }
        }

        private Table GetSqlRestorePointTable(DataServer ds) {
            var db = ds.Databases.GetOrAdd("SocketJack", _ => new Database("SocketJack"));
            var table = db.Tables.GetOrAdd(SqlAdminRestorePointsTableName, _ => new Table(SqlAdminRestorePointsTableName));
            EnsureSqlRestorePointColumns(table);
            if (table.Rows == null) table.Rows = new List<object[]>();
            bool changed;
            lock (table) {
                for (int i = 0; i < table.Rows.Count; i++)
                    table.Rows[i] = NormalizeSqlRestorePointRow(table.Rows[i]);
                changed = TrimSqlRestorePointRows(table);
            }
            if (changed)
                ds.ScheduleSave();
            return table;
        }

        private static void EnsureSqlRestorePointColumns(Table table) {
            if (table.Columns == null) table.Columns = new List<Column>();
            EnsureColumn(table, 0, "Id", 80);
            EnsureColumn(table, 1, "CreatedUtc", 80);
            EnsureColumn(table, 2, "Username", 160);
            EnsureColumn(table, 3, "OwnerKey", 180);
            EnsureColumn(table, 4, "SessionId", 120);
            EnsureColumn(table, 5, "DatabaseName", 160);
            EnsureColumn(table, 6, "Target", 240);
            EnsureColumn(table, 7, "Reason", 120);
            EnsureColumn(table, 8, "SqlHash", 96);
            EnsureColumn(table, 9, "SnapshotHash", 96);
            EnsureColumn(table, 10, "DataJson", -1);
            EnsureColumn(table, 11, "AuditId", 120);
        }

        private static object[] NormalizeSqlRestorePointRow(object[] row) {
            var normalized = new object[12];
            if (row != null) {
                int copy = Math.Min(row.Length, normalized.Length);
                for (int i = 0; i < copy; i++)
                    normalized[i] = row[i];
            }
            if (string.IsNullOrWhiteSpace(normalized[0]?.ToString())) normalized[0] = "restore_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(normalized[1]?.ToString())) normalized[1] = DateTime.UtcNow.ToString("O");
            for (int i = 2; i < normalized.Length; i++) {
                if (normalized[i] == null) normalized[i] = "";
            }
            return normalized;
        }

        private void PruneSqlAdminStorage() {
            try {
                var ds = GetDataServer();
                if (ds == null)
                    return;
                GetSqlRestorePointTable(ds);
            } catch {
            }
        }

        private static bool IsSqlAdminMaintenanceTable(string tableName) {
            return string.Equals(tableName, SqlAdminRestorePointsTableName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tableName, SqlAdminAuditTableName, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tableName, ErrorDiagnosticsTableName, StringComparison.OrdinalIgnoreCase);
        }

        private static int Utf8ByteCount(string text) {
            return string.IsNullOrEmpty(text) ? 0 : Encoding.UTF8.GetByteCount(text);
        }

        private static bool TrimSqlRestorePointRows(Table table) {
            if (table?.Rows == null || table.Rows.Count == 0)
                return false;

            int originalCount = table.Rows.Count;
            int totalBytes = 0;
            var keptNewestFirst = new List<object[]>(Math.Min(originalCount, MaxSqlRestorePointRows));

            for (int i = table.Rows.Count - 1; i >= 0; i--) {
                var row = NormalizeSqlRestorePointRow(table.Rows[i]);
                int rowBytes = Utf8ByteCount(GetRow(row, 10));
                if (rowBytes > MaxSingleSqlRestorePointBytes)
                    continue;
                if (keptNewestFirst.Count >= MaxSqlRestorePointRows)
                    continue;
                if (totalBytes + rowBytes > MaxSqlRestorePointBytes)
                    continue;

                keptNewestFirst.Add(row);
                totalBytes += rowBytes;
            }

            keptNewestFirst.Reverse();
            bool changed = keptNewestFirst.Count != originalCount;
            if (!changed)
                return false;

            table.Rows = keptNewestFirst;
            return true;
        }

        private string CreateSqlRestorePoint(DataServer ds, SqlAdminSession session, string databaseName, string target, string reason, string sql) {
            if (ds == null || string.IsNullOrWhiteSpace(databaseName) || !ds.Databases.TryGetValue(databaseName, out var db))
                return "";
            var snapshot = BuildSqlDatabaseSnapshot(db);
            var dataJson = JsonSerializer.Serialize(snapshot);
            if (Utf8ByteCount(dataJson) > MaxSingleSqlRestorePointBytes)
                return "";

            var snapshotHash = ComputeSqlAuditHash(dataJson);
            var id = "restore_" + Guid.NewGuid().ToString("N");
            var auditId = "audit_" + Guid.NewGuid().ToString("N");
            var table = GetSqlRestorePointTable(ds);
            var row = NormalizeSqlRestorePointRow(new object[] {
                id,
                DateTime.UtcNow.ToString("O"),
                session?.Username ?? "",
                session?.OwnerKey ?? BuildSqlOwnerKey(session?.Username ?? ""),
                session?.SessionId ?? "",
                databaseName ?? "",
                target ?? "",
                reason ?? "",
                ComputeSqlAuditHash(sql),
                snapshotHash,
                dataJson,
                auditId
            });
            lock (table) {
                table.Rows.Add(row);
                TrimSqlRestorePointRows(table);
            }
            ds.ScheduleSave();
            return id;
        }

        private string CreateSqlDesignerRestorePoint(DataServer ds, SqlAdminSession session, string databaseName, string target, string reason, out string beforeHash) {
            beforeHash = "";
            if (ds == null || string.IsNullOrWhiteSpace(databaseName) || !ds.Databases.TryGetValue(databaseName, out var db))
                return "";
            beforeHash = ComputeSqlDatabaseSnapshotHash(db);
            return CreateSqlRestorePoint(ds, session, databaseName, target, reason, "");
        }

        private static SqlDatabaseSnapshotDto BuildSqlDatabaseSnapshot(Database db) {
            var snapshot = new SqlDatabaseSnapshotDto();
            if (db == null)
                return snapshot;

            foreach (var tableKvp in db.Tables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (IsSqlAdminMaintenanceTable(tableKvp.Key))
                    continue;

                var table = tableKvp.Value;
                if (table == null)
                    continue;
                var tableSnapshot = new SqlTableSnapshotDto { Name = tableKvp.Key };
                foreach (var column in table.Columns ?? new List<Column>()) {
                    tableSnapshot.Columns.Add(new SqlColumnSnapshotDto {
                        Name = column?.Name ?? "",
                        TypeName = column?.DataType?.AssemblyQualifiedName ?? typeof(string).AssemblyQualifiedName,
                        MaxLength = column?.MaxLength ?? -1
                    });
                }
                foreach (var row in table.Rows ?? new List<object[]>()) {
                    var values = new List<string>();
                    int width = Math.Max(tableSnapshot.Columns.Count, row?.Length ?? 0);
                    for (int i = 0; i < width; i++) {
                        object value = row != null && i < row.Length ? row[i] : null;
                        values.Add(value?.ToString());
                    }
                    tableSnapshot.Rows.Add(values);
                }
                snapshot.Tables.Add(tableSnapshot);
            }
            return snapshot;
        }

        private string ComputeSqlDatabaseSnapshotHash(Database db) {
            return ComputeSqlAuditHash(JsonSerializer.Serialize(BuildSqlDatabaseSnapshot(db)));
        }

        private void RestoreSqlDatabaseSnapshot(Database db, string dataJson) {
            if (db == null || string.IsNullOrWhiteSpace(dataJson))
                throw new InvalidOperationException("Restore point is empty.");

            var snapshot = JsonSerializer.Deserialize<SqlDatabaseSnapshotDto>(dataJson);
            if (snapshot == null)
                throw new InvalidOperationException("Restore point could not be read.");

            var maintenanceTables = db.Tables
                .Where(kvp => IsSqlAdminMaintenanceTable(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

            db.Tables.Clear();
            foreach (var maintenance in maintenanceTables)
                db.Tables[maintenance.Key] = maintenance.Value;

            foreach (var tableSnapshot in snapshot.Tables ?? new List<SqlTableSnapshotDto>()) {
                if (string.IsNullOrWhiteSpace(tableSnapshot.Name) || IsSqlAdminMaintenanceTable(tableSnapshot.Name))
                    continue;
                var table = new Table(tableSnapshot.Name);
                foreach (var columnSnapshot in tableSnapshot.Columns ?? new List<SqlColumnSnapshotDto>()) {
                    var type = Type.GetType(columnSnapshot.TypeName ?? "", false) ?? ResolveType(columnSnapshot.TypeName ?? "string");
                    table.Columns.Add(new Column(columnSnapshot.Name ?? "", type, columnSnapshot.MaxLength));
                }
                foreach (var rowSnapshot in tableSnapshot.Rows ?? new List<List<string>>()) {
                    var row = new object[table.Columns.Count];
                    for (int i = 0; i < row.Length; i++) {
                        string raw = rowSnapshot != null && i < rowSnapshot.Count ? rowSnapshot[i] : null;
                        row[i] = raw == null ? null : ConvertValue(raw, table.Columns[i].DataType);
                    }
                    table.Rows.Add(row);
                }
                db.Tables[table.Name] = table;
            }
        }

        private object ApiBackupsList(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) return "{\"restorePoints\":[]}";
            var table = GetSqlRestorePointTable(ds);
            var rows = new List<object[]>();
            lock (table) {
                for (int i = table.Rows.Count - 1; i >= 0 && rows.Count < 200; i--) {
                    var row = NormalizeSqlRestorePointRow(table.Rows[i]);
                    if (CanSqlAdminSessionAccessDatabase(req, session, ds, GetRow(row, 5)))
                        rows.Add(row);
                }
            }

            var sb = new StringBuilder();
            sb.Append("{\"restorePoints\":[");
            for (int i = 0; i < rows.Count; i++) {
                if (i > 0) sb.Append(",");
                var row = rows[i];
                sb.Append("{\"id\":\"").Append(EscapeJson(GetRow(row, 0)))
                  .Append("\",\"createdUtc\":\"").Append(EscapeJson(GetRow(row, 1)))
                  .Append("\",\"username\":\"").Append(EscapeJson(GetRow(row, 2)))
                  .Append("\",\"ownerKey\":\"").Append(EscapeJson(GetRow(row, 3)))
                  .Append("\",\"sessionId\":\"").Append(EscapeJson(GetRow(row, 4)))
                  .Append("\",\"database\":\"").Append(EscapeJson(GetRow(row, 5)))
                  .Append("\",\"target\":\"").Append(EscapeJson(GetRow(row, 6)))
                  .Append("\",\"reason\":\"").Append(EscapeJson(GetRow(row, 7)))
                  .Append("\",\"sqlHash\":\"").Append(EscapeJson(GetRow(row, 8)))
                  .Append("\",\"snapshotHash\":\"").Append(EscapeJson(GetRow(row, 9)))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiBackupsRestore(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (!ValidateSqlAdminMutation(req, session, "backups.restore", out var mutationError))
                return mutationError;

            var ds = GetDataServer();
            if (ds == null) return "{\"error\":\"DataServer not available.\"}";
            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id") ?? ExtractJsonString(body, "restorePointId");
            var confirmText = (ExtractJsonString(body, "confirmText") ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) return "{\"error\":\"Restore point id is required.\"}";
            if (!string.Equals(confirmText, "RESTORE", StringComparison.OrdinalIgnoreCase)) {
                req.Context.StatusCode = "409 Conflict";
                return "{\"error\":\"Type RESTORE to roll back to this restore point.\",\"typedConfirmation\":\"RESTORE\"}";
            }

            var table = GetSqlRestorePointTable(ds);
            object[] restoreRow = null;
            lock (table) {
                restoreRow = table.Rows.Select(NormalizeSqlRestorePointRow)
                    .FirstOrDefault(row => string.Equals(GetRow(row, 0), id, StringComparison.OrdinalIgnoreCase));
            }
            if (restoreRow == null) return "{\"error\":\"Restore point not found.\"}";

            var databaseName = GetRow(restoreRow, 5);
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, databaseName))
                return SqlTenantForbiddenJson(req, databaseName);
            if (!ds.Databases.TryGetValue(databaseName, out var db))
                return "{\"error\":\"Database not found.\"}";

            string beforeHash = ComputeSqlDatabaseSnapshotHash(db);
            RestoreSqlDatabaseSnapshot(db, GetRow(restoreRow, 10));
            string afterHash = ComputeSqlDatabaseSnapshotHash(db);
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "backup.restore", databaseName, GetRow(restoreRow, 6), "success", 0, "Database restored from SQL Admin restore point.", "", beforeHash, afterHash, GetRow(restoreRow, 11), "backup");
            return "{\"success\":true,\"restorePointId\":\"" + EscapeJson(id) + "\",\"database\":\"" + EscapeJson(databaseName) + "\",\"beforeHash\":\"" + EscapeJson(beforeHash) + "\",\"afterHash\":\"" + EscapeJson(afterHash) + "\"}";
        }

        private static string ExtractSqlAdminClientIp(HttpRequest req) {
            foreach (string headerName in new[] { "CF-Connecting-IP", "X-Real-IP", "X-Forwarded-For" }) {
                string forwarded = DataServer.NormalizeSqlLoginIpAddress(GetHeader(req, headerName));
                if (!string.IsNullOrWhiteSpace(forwarded))
                    return forwarded;
            }

            try {
                var address = req?.Context?.Connection?.EndPoint?.Address;
                if (address != null) return DataServer.NormalizeSqlLoginIpAddress(address.ToString());
            } catch { }
            return "";
        }

        private static string ComputeSqlAuditHash(string sql) {
            if (string.IsNullOrEmpty(sql)) return "";
            using (var sha = SHA256.Create()) {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sql));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string TruncateAuditText(string text, int maxLength) {
            text = text ?? "";
            return text.Length <= maxLength ? text : text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
        }

        private static string NormalizeSqlPreview(string sql) {
            if (string.IsNullOrWhiteSpace(sql)) return "";
            return System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();
        }

        private static SqlRiskAssessment ClassifySqlRisk(string sql) {
            string normalized = NormalizeSqlPreview(RemoveSqlComments(sql)).ToUpperInvariant();
            var risk = new SqlRiskAssessment();
            if (string.IsNullOrWhiteSpace(normalized))
                return risk;

            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bDROP\s+(DATABASE|TABLE|VIEW|INDEX|PROCEDURE|FUNCTION|TRIGGER)\b")) {
                risk.Operation = "drop";
                risk.Severity = "critical";
                risk.RequiresConfirmation = true;
                risk.Reason = "DROP statements can permanently remove schema or data and require explicit confirmation.";
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bTRUNCATE\s+TABLE\b")) {
                risk.Operation = "truncate";
                risk.Severity = "critical";
                risk.RequiresConfirmation = true;
                risk.Reason = "TRUNCATE TABLE removes all table rows and requires explicit confirmation.";
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bALTER\s+(DATABASE|TABLE|VIEW|INDEX|PROCEDURE|FUNCTION|TRIGGER)\b")) {
                risk.Operation = "alter";
                risk.Severity = "high";
                risk.RequiresConfirmation = true;
                risk.Reason = "ALTER statements change database schema and require explicit confirmation.";
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bDELETE\s+FROM\b")) {
                risk.Operation = "delete";
                if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bWHERE\b")) {
                    risk.Severity = "critical";
                    risk.RequiresConfirmation = true;
                    risk.Reason = "DELETE without a WHERE clause can remove every row and requires explicit confirmation.";
                } else {
                    risk.Severity = "medium";
                }
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bUPDATE\s+[\[\]\w\.]+")) {
                risk.Operation = "update";
                if (!System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bWHERE\b")) {
                    risk.Severity = "high";
                    risk.RequiresConfirmation = true;
                    risk.Reason = "UPDATE without a WHERE clause can modify every row and requires explicit confirmation.";
                } else {
                    risk.Severity = "medium";
                }
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bINSERT\s+INTO\b")) {
                risk.Operation = "insert";
                risk.Severity = "medium";
                return risk;
            }
            if (System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\bCREATE\s+(DATABASE|TABLE|VIEW|INDEX|PROCEDURE|FUNCTION|TRIGGER)\b")) {
                risk.Operation = "create";
                risk.Severity = "medium";
                return risk;
            }

            risk.Operation = "read";
            risk.Severity = "low";
            return risk;
        }

        private static string RemoveSqlComments(string sql) {
            if (string.IsNullOrEmpty(sql)) return "";
            string withoutBlock = System.Text.RegularExpressions.Regex.Replace(sql, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
            return System.Text.RegularExpressions.Regex.Replace(withoutBlock, @"--.*?$", " ", System.Text.RegularExpressions.RegexOptions.Multiline);
        }

        private static long EstimateSqlAffectedRows(DataServer ds, string databaseName, string sql, string operation) {
            if (ds == null || string.IsNullOrWhiteSpace(databaseName) || string.IsNullOrWhiteSpace(sql))
                return 0;
            if (!ds.Databases.TryGetValue(databaseName, out var db))
                return 0;

            string normalized = NormalizeSqlPreview(RemoveSqlComments(sql));
            string tableName = null;
            try {
                switch ((operation ?? "").ToLowerInvariant()) {
                    case "drop": {
                        var m = System.Text.RegularExpressions.Regex.Match(normalized, @"\bDROP\s+TABLE\s+(?<table>[\[\]\w\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) tableName = StripBrackets(m.Groups["table"].Value.Split('.').Last());
                        break;
                    }
                    case "truncate": {
                        var m = System.Text.RegularExpressions.Regex.Match(normalized, @"\bTRUNCATE\s+TABLE\s+(?<table>[\[\]\w\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) tableName = StripBrackets(m.Groups["table"].Value.Split('.').Last());
                        break;
                    }
                    case "delete": {
                        var m = System.Text.RegularExpressions.Regex.Match(normalized, @"\bDELETE\s+FROM\s+(?<table>[\[\]\w\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) tableName = StripBrackets(m.Groups["table"].Value.Split('.').Last());
                        break;
                    }
                    case "update": {
                        var m = System.Text.RegularExpressions.Regex.Match(normalized, @"\bUPDATE\s+(?<table>[\[\]\w\.]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (m.Success) tableName = StripBrackets(m.Groups["table"].Value.Split('.').Last());
                        break;
                    }
                }
            } catch { }

            if (!string.IsNullOrWhiteSpace(tableName)) {
                var table = FindTableFuzzy(db, tableName);
                if (table != null)
                    return table.Rows?.Count ?? 0;
            }

            return db.Tables.Values.Sum(table => (long)(table?.Rows?.Count ?? 0));
        }

        #endregion

        #region Table Designer API

        private Table FindTable(HttpRequest req, SqlAdminSession session, DataServer ds, string dbName, string tableName, out Database db) {
            db = null;
            if (ds == null || string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(tableName)) return null;
            if (!ds.Databases.TryGetValue(dbName, out db)) return null;
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName)) return null;
            return FindTableFuzzy(db, tableName);
        }

        private static Type ResolveType(string typeName) {
            if (string.IsNullOrEmpty(typeName)) return typeof(string);
            switch (typeName.ToLowerInvariant()) {
                case "string": case "nvarchar": case "varchar": case "text": case "ntext": case "char": case "nchar":
                    return typeof(string);
                case "int": case "int32":
                    return typeof(int);
                case "bigint": case "long": case "int64":
                    return typeof(long);
                case "smallint": case "short": case "int16":
                    return typeof(short);
                case "tinyint": case "byte":
                    return typeof(byte);
                case "bit": case "bool": case "boolean":
                    return typeof(bool);
                case "float": case "double":
                    return typeof(double);
                case "real": case "single":
                    return typeof(float);
                case "decimal": case "numeric": case "money": case "smallmoney":
                    return typeof(decimal);
                case "datetime": case "datetime2": case "date": case "smalldatetime":
                    return typeof(DateTime);
                case "uniqueidentifier": case "guid":
                    return typeof(Guid);
                case "binary": case "varbinary": case "image": case "byte[]":
                    return typeof(byte[]);
                default:
                    return typeof(string);
            }
        }

        private static string TypeToDisplayName(Type t) {
            if (t == null) return "nvarchar";
            if (t == typeof(string)) return "nvarchar";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "bigint";
            if (t == typeof(short)) return "smallint";
            if (t == typeof(byte)) return "tinyint";
            if (t == typeof(bool)) return "bit";
            if (t == typeof(double)) return "float";
            if (t == typeof(float)) return "real";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(DateTime)) return "datetime";
            if (t == typeof(Guid)) return "uniqueidentifier";
            if (t == typeof(byte[])) return "varbinary";
            return t.Name.ToLowerInvariant();
        }

        private static object ConvertValue(string value, Type targetType) {
            if (value == null) return null;
            if (targetType == null || targetType == typeof(string)) return value;
            try {
                if (targetType == typeof(int)) return int.Parse(value);
                if (targetType == typeof(long)) return long.Parse(value);
                if (targetType == typeof(short)) return short.Parse(value);
                if (targetType == typeof(byte)) return byte.Parse(value);
                if (targetType == typeof(bool)) return bool.Parse(value);
                if (targetType == typeof(double)) return double.Parse(value);
                if (targetType == typeof(float)) return float.Parse(value);
                if (targetType == typeof(decimal)) return decimal.Parse(value);
                if (targetType == typeof(DateTime)) return DateTime.Parse(value);
                if (targetType == typeof(Guid)) return Guid.Parse(value);
                return value;
            } catch {
                return value;
            }
        }

        private object ApiDesignerCreateTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.create-table", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            if (string.IsNullOrWhiteSpace(tableName)) { return "{\"error\":\"Table name is required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName)) return SqlTenantForbiddenJson(req, dbName);
            if (db.Tables.ContainsKey(tableName)) { return "{\"error\":\"Table already exists.\"}"; }

            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.create-table", out var beforeHash);
            var table = new Table(tableName);
            table.Columns.Add(new Column("Id", typeof(int), -1));
            db.Tables[tableName] = table;
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.create-table", dbName, tableName, "success", 0, "Table created through SQL Admin designer.", "", beforeHash, ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true}";
        }

        private object ApiDesignerDropTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.drop-table", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            if (string.IsNullOrWhiteSpace(tableName)) { return "{\"error\":\"Table name is required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName)) return SqlTenantForbiddenJson(req, dbName);
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.drop-table", out var beforeHash);
            if (!db.Tables.TryRemove(tableName, out _)) { return "{\"error\":\"Table not found.\"}"; }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.drop-table", dbName, tableName, "success", 0, "Table dropped through SQL Admin designer.", "", beforeHash, ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true}";
        }

        private object ApiDesignerRenameTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.rename-table", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var oldName = ExtractJsonString(body, "table");
            var newName = ExtractJsonString(body, "newName");
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) { return "{\"error\":\"Table name and new name are required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, dbName)) return SqlTenantForbiddenJson(req, dbName);
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, oldName, "designer.rename-table", out var beforeHash);
            if (!db.Tables.TryRemove(oldName, out var table)) { return "{\"error\":\"Table not found.\"}"; }
            if (db.Tables.ContainsKey(newName)) {
                db.Tables[oldName] = table;
                return "{\"error\":\"A table with that name already exists.\"}";
            }
            table.Name = newName;
            db.Tables[newName] = table;
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.rename-table", dbName, oldName + " -> " + newName, "success", 0, "Table renamed through SQL Admin designer.", "", beforeHash, ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true}";
        }

        private object ApiDesignerSaveSchema(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.save-schema", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colNames = ExtractJsonString(body, "colNames");
            var colTypes = ExtractJsonString(body, "colTypes");
            var colMaxLens = ExtractJsonString(body, "colMaxLengths");

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (string.IsNullOrEmpty(colNames)) { return "{\"error\":\"Column names are required.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.save-schema", out var beforeHash);

            var names = colNames.Split('|');
            var types = (colTypes ?? "").Split('|');
            var maxLens = (colMaxLens ?? "").Split('|');

            var oldCount = table.Columns.Count;
            var newCols = new List<Column>();
            for (int i = 0; i < names.Length; i++) {
                if (string.IsNullOrWhiteSpace(names[i])) continue;
                var t = i < types.Length ? types[i] : "nvarchar";
                int ml = -1;
                if (i < maxLens.Length) int.TryParse(maxLens[i], out ml);
                newCols.Add(new Column(names[i].Trim(), ResolveType(t), ml));
            }

            // Adjust existing rows to match new column count
            var newCount = newCols.Count;
            if (newCount != oldCount) {
                for (int r = 0; r < table.Rows.Count; r++) {
                    var oldRow = table.Rows[r];
                    var newRow = new object[newCount];
                    for (int c = 0; c < Math.Min(oldRow.Length, newCount); c++)
                        newRow[c] = oldRow[c];
                    table.Rows[r] = newRow;
                }
            }

            table.Columns.Clear();
            table.Columns.AddRange(newCols);
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.save-schema", dbName, tableName, "success", table.Rows.Count, "Table schema saved through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true,\"columnCount\":" + newCols.Count + "}";
        }

        private object ApiDesignerAddColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.add-column", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colName = ExtractJsonString(body, "name");
            var colType = ExtractJsonString(body, "type") ?? "nvarchar";
            var colMaxLenStr = ExtractJsonString(body, "maxLength");
            int colMaxLen = -1;
            if (colMaxLenStr != null) int.TryParse(colMaxLenStr, out colMaxLen);

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (string.IsNullOrWhiteSpace(colName)) { return "{\"error\":\"Column name is required.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.add-column", out var beforeHash);

            table.Columns.Add(new Column(colName.Trim(), ResolveType(colType), colMaxLen));

            // Extend existing rows with a null value for the new column
            for (int r = 0; r < table.Rows.Count; r++) {
                var oldRow = table.Rows[r];
                var newRow = new object[table.Columns.Count];
                Array.Copy(oldRow, newRow, oldRow.Length);
                table.Rows[r] = newRow;
            }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.add-column", dbName, tableName + "." + colName, "success", table.Rows.Count, "Column added through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true,\"columnCount\":" + table.Columns.Count + "}";
        }

        private object ApiDesignerRemoveColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.remove-column", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colIndexStr = ExtractJsonString(body, "colIndex");
            if (!int.TryParse(colIndexStr, out var colIndex)) { return "{\"error\":\"Column index is required.\"}"; }

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (colIndex < 0 || colIndex >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.remove-column", out var beforeHash);
            string removedName = table.Columns[colIndex].Name;

            table.Columns.RemoveAt(colIndex);

            // Remove corresponding value from each row
            for (int r = 0; r < table.Rows.Count; r++) {
                var oldRow = table.Rows[r];
                var newRow = new object[table.Columns.Count];
                int dest = 0;
                for (int c = 0; c < oldRow.Length; c++) {
                    if (c == colIndex) continue;
                    if (dest < newRow.Length) newRow[dest++] = oldRow[c];
                }
                table.Rows[r] = newRow;
            }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.remove-column", dbName, tableName + "." + removedName, "success", table.Rows.Count, "Column removed through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true,\"columnCount\":" + table.Columns.Count + "}";
        }

        private object ApiDesignerUpdateColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.update-column", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colIndexStr = ExtractJsonString(body, "colIndex");
            if (!int.TryParse(colIndexStr, out var colIndex)) { return "{\"error\":\"Column index is required.\"}"; }

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (colIndex < 0 || colIndex >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.update-column", out var beforeHash);

            var col = table.Columns[colIndex];
            var newName = ExtractJsonString(body, "newName");
            var newType = ExtractJsonString(body, "newType");
            var newMaxLenStr = ExtractJsonString(body, "newMaxLength");

            if (!string.IsNullOrWhiteSpace(newName)) col.Name = newName.Trim();
            if (!string.IsNullOrWhiteSpace(newType)) col.DataType = ResolveType(newType);
            if (newMaxLenStr != null && int.TryParse(newMaxLenStr, out var newMaxLen)) col.MaxLength = newMaxLen;

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.update-column", dbName, tableName + "." + col.Name, "success", table.Rows.Count, "Column updated through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true}";
        }

        private object ApiDesignerInsertRow(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.insert-row", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.insert-row", out var beforeHash);

            var colCount = table.Columns.Count;
            var newRow = new object[colCount];
            // Parse optional pipe-delimited values
            var valuesStr = ExtractJsonString(body, "values");
            if (!string.IsNullOrEmpty(valuesStr)) {
                var parts = valuesStr.Split('|');
                for (int i = 0; i < Math.Min(parts.Length, colCount); i++) {
                    if (parts[i] == "\0" || parts[i] == "NULL") {
                        newRow[i] = null;
                    } else {
                        newRow[i] = ConvertValue(parts[i], table.Columns[i].DataType);
                    }
                }
            }
            table.Rows.Add(newRow);
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.insert-row", dbName, tableName, "success", 1, "Row inserted through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true,\"rowIndex\":" + (table.Rows.Count - 1) + ",\"totalRows\":" + table.Rows.Count + "}";
        }

        private object ApiDesignerUpdateCell(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.update-cell", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var rowStr = ExtractJsonString(body, "row");
            var colStr = ExtractJsonString(body, "col");
            var value = ExtractJsonString(body, "value");
            var isNull = ExtractJsonString(body, "isNull");

            if (!int.TryParse(rowStr, out var rowIdx) || !int.TryParse(colStr, out var colIdx)) {
                return "{\"error\":\"Row and column indices are required.\"}";
            }

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (rowIdx < 0 || rowIdx >= table.Rows.Count) { return "{\"error\":\"Row index out of range.\"}"; }
            if (colIdx < 0 || colIdx >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.update-cell", out var beforeHash);

            // Ensure row has enough slots
            var row = table.Rows[rowIdx];
            if (row.Length <= colIdx) {
                var expanded = new object[table.Columns.Count];
                Array.Copy(row, expanded, row.Length);
                row = expanded;
                table.Rows[rowIdx] = row;
            }

            if (isNull == "true") {
                row[colIdx] = null;
            } else {
                row[colIdx] = ConvertValue(value, table.Columns[colIdx].DataType);
            }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.update-cell", dbName, tableName + "[" + rowIdx + "," + colIdx + "]", "success", 1, "Cell updated through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true}";
        }

        private object ApiDesignerDeleteRow(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "designer.delete-row", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var rowStr = ExtractJsonString(body, "row");

            if (!int.TryParse(rowStr, out var rowIdx)) { return "{\"error\":\"Row index is required.\"}"; }

            var table = FindTable(req, session, ds, dbName, tableName, out var db);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (rowIdx < 0 || rowIdx >= table.Rows.Count) { return "{\"error\":\"Row index out of range.\"}"; }
            var restorePointId = CreateSqlDesignerRestorePoint(ds, session, dbName, tableName, "designer.delete-row", out var beforeHash);

            table.Rows.RemoveAt(rowIdx);
            ds.ScheduleSave();
            WriteSqlAudit(req, session, "designer.delete-row", dbName, tableName + "[" + rowIdx + "]", "success", 1, "Row deleted through SQL Admin designer.", "", beforeHash, db == null ? "" : ComputeSqlDatabaseSnapshotHash(db), restorePointId, "designer");
            return "{\"success\":true,\"totalRows\":" + table.Rows.Count + "}";
        }

        private object ApiDesignerViewport(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[],\"rows\":[],\"totalRows\":0}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            int offset = 0, limit = 50;
            if (req.QueryParameters.ContainsKey("offset") && int.TryParse(req.QueryParameters["offset"], out var o)) offset = Math.Max(o, 0);
            if (req.QueryParameters.ContainsKey("limit") && int.TryParse(req.QueryParameters["limit"], out var l)) limit = Math.Min(Math.Max(l, 1), 1000);

            var table = FindTable(req, session, ds, dbName, tableName, out _);
            if (table == null) { return "{\"columns\":[],\"rows\":[],\"totalRows\":0}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"offset\":").Append(offset)
              .Append(",\"limit\":").Append(limit)
              .Append(",\"totalRows\":").Append(table.Rows.Count)
              .Append(",\"columns\":[");
            for (int c = 0; c < table.Columns.Count; c++) {
                if (c > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(EscapeJson(table.Columns[c].Name))
                  .Append("\",\"type\":\"").Append(EscapeJson(TypeToDisplayName(table.Columns[c].DataType)))
                  .Append("\",\"maxLength\":").Append(table.Columns[c].MaxLength)
                  .Append("}");
            }
            sb.Append("],\"rows\":[");
            int end = Math.Min(offset + limit, table.Rows.Count);
            for (int r = offset; r < end; r++) {
                if (r > offset) sb.Append(",");
                sb.Append("[");
                var row = table.Rows[r];
                for (int c = 0; c < table.Columns.Count; c++) {
                    if (c > 0) sb.Append(",");
                    if (c >= row.Length || row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("]");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region API Creator

        private void LoadApiEndpoints() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("APIs", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ep = new ApiEndpointDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    Name = row.Length > 1 ? row[1]?.ToString() : null,
                    Route = row.Length > 2 ? row[2]?.ToString() : null,
                    HttpMethod = row.Length > 3 ? row[3]?.ToString() : "GET",
                    Database = row.Length > 4 ? row[4]?.ToString() : "db",
                    SqlQuery = row.Length > 5 ? row[5]?.ToString() : null,
                    ResponseFormat = row.Length > 6 ? row[6]?.ToString() : "json",
                    ContentType = row.Length > 7 ? row[7]?.ToString() : null,
                    Enabled = row.Length > 8 && (row[8]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    Variables = row.Length > 9 ? row[9]?.ToString() : null,
                    QuerySteps = row.Length > 10 ? row[10]?.ToString() : null,
                    BodyType = row.Length > 11 ? row[11]?.ToString() : null,
                    BodySchema = row.Length > 12 ? row[12]?.ToString() : null,
                    Parameters = row.Length > 13 ? row[13]?.ToString() : null,
                    OutputSchema = row.Length > 14 ? row[14]?.ToString() : null,
                    Description = row.Length > 15 ? row[15]?.ToString() : null,
                    HandlerTypeName = row.Length > 16 ? row[16]?.ToString() : null,
                    HandlerMethodName = row.Length > 17 ? row[17]?.ToString() : null,
                    HandlerArguments = row.Length > 18 ? row[18]?.ToString() : null,
                    AuthMode = NormalizeApiAuthMode(row.Length > 19 ? row[19]?.ToString() : "session"),
                    Scopes = row.Length > 20 ? row[20]?.ToString() : "sql:read",
                    RateLimitPerMinute = ParsePositiveInt(row.Length > 21 ? row[21]?.ToString() : null, 60),
                    CorsPolicy = row.Length > 22 ? row[22]?.ToString() : "same-origin",
                    InputSchema = row.Length > 23 ? row[23]?.ToString() : null,
                    PublicEnabled = row.Length > 24 && (row[24]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    EndpointSecret = row.Length > 25 ? row[25]?.ToString() : null,
                    Source = "creator",
                    ReadOnly = false
                };
                if (string.IsNullOrEmpty(ep.Id) || string.IsNullOrEmpty(ep.Route)) continue;
                _apiEndpoints[ep.Id] = ep;
                if (ep.Enabled) {
                    try { _server.Map(ep.HttpMethod.ToUpperInvariant(), ep.Route, (conn, req, ct) => HandleDynamicApiEndpoint(req, ep)); } catch { }
                }
            }
        }

        private void SaveApiEndpointsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("APIs");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Route", typeof(string), -1));
            table.Columns.Add(new Column("HttpMethod", typeof(string), 10));
            table.Columns.Add(new Column("Database", typeof(string), -1));
            table.Columns.Add(new Column("SqlQuery", typeof(string), -1));
            table.Columns.Add(new Column("ResponseFormat", typeof(string), 20));
            table.Columns.Add(new Column("ContentType", typeof(string), -1));
            table.Columns.Add(new Column("Enabled", typeof(string), 5));
            table.Columns.Add(new Column("Variables", typeof(string), -1));
            table.Columns.Add(new Column("QuerySteps", typeof(string), -1));
            table.Columns.Add(new Column("BodyType", typeof(string), 20));
            table.Columns.Add(new Column("BodySchema", typeof(string), -1));
            table.Columns.Add(new Column("Parameters", typeof(string), -1));
            table.Columns.Add(new Column("OutputSchema", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));
            table.Columns.Add(new Column("HandlerTypeName", typeof(string), -1));
            table.Columns.Add(new Column("HandlerMethodName", typeof(string), -1));
            table.Columns.Add(new Column("HandlerArguments", typeof(string), -1));
            table.Columns.Add(new Column("AuthMode", typeof(string), 40));
            table.Columns.Add(new Column("Scopes", typeof(string), -1));
            table.Columns.Add(new Column("RateLimitPerMinute", typeof(string), 16));
            table.Columns.Add(new Column("CorsPolicy", typeof(string), -1));
            table.Columns.Add(new Column("InputSchema", typeof(string), -1));
            table.Columns.Add(new Column("PublicEnabled", typeof(string), 8));
            table.Columns.Add(new Column("EndpointSecret", typeof(string), -1));

            foreach (var ep in _apiEndpoints.Values) {
                table.Rows.Add(new object[] {
                    ep.Id, ep.Name, ep.Route, ep.HttpMethod, ep.Database,
                    ep.SqlQuery, ep.ResponseFormat, ep.ContentType,
                    ep.Enabled ? "true" : "false", ep.Variables ?? "", ep.QuerySteps ?? "",
                    ep.BodyType ?? "", ep.BodySchema ?? "", ep.Parameters ?? "",
                    ep.OutputSchema ?? "", ep.Description ?? "",
                    ep.HandlerTypeName ?? "", ep.HandlerMethodName ?? "", ep.HandlerArguments ?? "",
                    NormalizeApiAuthMode(ep.AuthMode), ep.Scopes ?? "sql:read",
                    (ep.RateLimitPerMinute <= 0 ? 60 : ep.RateLimitPerMinute).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ep.CorsPolicy ?? "same-origin", ep.InputSchema ?? "",
                    ep.PublicEnabled ? "true" : "false", ep.EndpointSecret ?? ""
                });
            }

            configDb.Tables["APIs"] = table;
            ds.ScheduleSave();
        }

        private void LoadApiRouteSettings() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("ApiRouteSettings", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ep = new ApiEndpointDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    HttpMethod = row.Length > 1 ? row[1]?.ToString() : "GET",
                    Route = row.Length > 2 ? row[2]?.ToString() : null,
                    Name = row.Length > 3 ? row[3]?.ToString() : null,
                    Description = row.Length > 4 ? row[4]?.ToString() : null,
                    Variables = row.Length > 5 ? row[5]?.ToString() : null,
                    BodyType = row.Length > 6 ? row[6]?.ToString() : null,
                    BodySchema = row.Length > 7 ? row[7]?.ToString() : null,
                    Parameters = row.Length > 8 ? row[8]?.ToString() : null,
                    OutputSchema = row.Length > 9 ? row[9]?.ToString() : null,
                    ResponseFormat = row.Length > 10 ? row[10]?.ToString() : "handler",
                    ContentType = row.Length > 11 ? row[11]?.ToString() : null,
                    HandlerTypeName = row.Length > 12 ? row[12]?.ToString() : null,
                    HandlerMethodName = row.Length > 13 ? row[13]?.ToString() : null,
                    HandlerArguments = row.Length > 14 ? row[14]?.ToString() : null,
                    AuthMode = NormalizeApiAuthMode(row.Length > 15 ? row[15]?.ToString() : "session"),
                    Scopes = row.Length > 16 ? row[16]?.ToString() : "sql:read",
                    RateLimitPerMinute = ParsePositiveInt(row.Length > 17 ? row[17]?.ToString() : null, 120),
                    CorsPolicy = row.Length > 18 ? row[18]?.ToString() : "same-origin",
                    InputSchema = row.Length > 19 ? row[19]?.ToString() : null,
                    PublicEnabled = row.Length > 20 && (row[20]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    EndpointSecret = row.Length > 21 ? row[21]?.ToString() : null,
                    Enabled = true,
                    Source = "mapped",
                    ReadOnly = true
                };
                if (string.IsNullOrEmpty(ep.Route)) continue;
                ep.HttpMethod = (ep.HttpMethod ?? "GET").ToUpperInvariant();
                if (string.IsNullOrEmpty(ep.Id)) ep.Id = MappedEndpointId(ep.HttpMethod, ep.Route);
                _apiRouteSettings[ApiEndpointRouteKey(ep.HttpMethod, ep.Route)] = ep;
            }
        }

        private void SaveApiRouteSettingsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("ApiRouteSettings");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("HttpMethod", typeof(string), 10));
            table.Columns.Add(new Column("Route", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));
            table.Columns.Add(new Column("Variables", typeof(string), -1));
            table.Columns.Add(new Column("BodyType", typeof(string), 20));
            table.Columns.Add(new Column("BodySchema", typeof(string), -1));
            table.Columns.Add(new Column("Parameters", typeof(string), -1));
            table.Columns.Add(new Column("OutputSchema", typeof(string), -1));
            table.Columns.Add(new Column("ResponseFormat", typeof(string), 20));
            table.Columns.Add(new Column("ContentType", typeof(string), -1));
            table.Columns.Add(new Column("HandlerTypeName", typeof(string), -1));
            table.Columns.Add(new Column("HandlerMethodName", typeof(string), -1));
            table.Columns.Add(new Column("HandlerArguments", typeof(string), -1));
            table.Columns.Add(new Column("AuthMode", typeof(string), 40));
            table.Columns.Add(new Column("Scopes", typeof(string), -1));
            table.Columns.Add(new Column("RateLimitPerMinute", typeof(string), 16));
            table.Columns.Add(new Column("CorsPolicy", typeof(string), -1));
            table.Columns.Add(new Column("InputSchema", typeof(string), -1));
            table.Columns.Add(new Column("PublicEnabled", typeof(string), 8));
            table.Columns.Add(new Column("EndpointSecret", typeof(string), -1));

            foreach (var ep in _apiRouteSettings.Values
                .OrderBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.HttpMethod, StringComparer.OrdinalIgnoreCase)) {
                table.Rows.Add(new object[] {
                    ep.Id, ep.HttpMethod, ep.Route, ep.Name ?? "", ep.Description ?? "",
                    ep.Variables ?? "", ep.BodyType ?? "", ep.BodySchema ?? "",
                    ep.Parameters ?? "", ep.OutputSchema ?? "", ep.ResponseFormat ?? "handler",
                    ep.ContentType ?? "", ep.HandlerTypeName ?? "", ep.HandlerMethodName ?? "",
                    ep.HandlerArguments ?? "", NormalizeApiAuthMode(ep.AuthMode), ep.Scopes ?? "sql:read",
                    (ep.RateLimitPerMinute <= 0 ? 120 : ep.RateLimitPerMinute).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ep.CorsPolicy ?? "same-origin", ep.InputSchema ?? "",
                    ep.PublicEnabled ? "true" : "false", ep.EndpointSecret ?? ""
                });
            }

            configDb.Tables["ApiRouteSettings"] = table;
            ds.ScheduleSave();
        }

        private object ApiEndpointsList(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return JsonSerializer.Serialize(new { error = "Not authenticated." }); }

            var endpoints = GetApiEndpointList()
                .Select(ep => new {
                    id = ep.Id ?? "",
                    name = ep.Name ?? "",
                    route = ep.Route ?? "",
                    httpMethod = ep.HttpMethod ?? "GET",
                    database = ep.Database ?? "",
                    sqlQuery = ep.SqlQuery ?? "",
                    querySteps = ep.QuerySteps ?? "",
                    responseFormat = ep.ResponseFormat ?? "",
                    contentType = ep.ContentType ?? "",
                    variables = ep.Variables ?? "",
                    bodyType = string.IsNullOrWhiteSpace(ep.BodyType) ? "none" : ep.BodyType,
                    bodySchema = ep.BodySchema ?? "",
                    parameters = ep.Parameters ?? "",
                    outputSchema = ep.OutputSchema ?? "",
                    description = ep.Description ?? "",
                    handlerTypeName = ep.HandlerTypeName ?? "",
                    handlerMethodName = ep.HandlerMethodName ?? "",
                    handlerArguments = ep.HandlerArguments ?? "",
                    authMode = NormalizeApiAuthMode(ep.AuthMode),
                    scopes = ep.Scopes ?? "sql:read",
                    rateLimitPerMinute = ep.RateLimitPerMinute <= 0 ? 60 : ep.RateLimitPerMinute,
                    corsPolicy = ep.CorsPolicy ?? "same-origin",
                    inputSchema = ep.InputSchema ?? "",
                    publicEnabled = ep.PublicEnabled,
                    endpointSecretConfigured = !string.IsNullOrWhiteSpace(ep.EndpointSecret),
                    enabled = ep.Enabled,
                    source = string.IsNullOrEmpty(ep.Source) ? "creator" : ep.Source,
                    readOnly = ep.ReadOnly
                })
                .ToList();

            return JsonSerializer.Serialize(new { endpoints });
        }

        private List<ApiEndpointDef> GetApiEndpointList() {
            var endpoints = new List<ApiEndpointDef>();
            var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ep in _apiEndpoints.Values
                .OrderBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.HttpMethod, StringComparer.OrdinalIgnoreCase)) {
                ep.Source = "creator";
                ep.ReadOnly = false;
                endpoints.Add(ep);
                seenRoutes.Add(ApiEndpointRouteKey(ep.HttpMethod, ep.Route));
            }

            foreach (var route in _server.GetMappedRoutes()) {
                if (!IsRestApiMethod(route.Method) || IsAdminPanelRoute(route.Path))
                    continue;

                var routeKey = ApiEndpointRouteKey(route.Method, route.Path);
                if (seenRoutes.Contains(routeKey))
                    continue;

                _apiRouteSettings.TryGetValue(routeKey, out var settings);
                var ep = new ApiEndpointDef {
                    Id = MappedEndpointId(route.Method, route.Path),
                    Name = route.Method + " " + route.Path,
                    Route = route.Path,
                    HttpMethod = route.Method,
                    Database = "",
                    SqlQuery = "",
                    QuerySteps = "",
                    ResponseFormat = "handler",
                    ContentType = "",
                    Variables = route.InputVariables ?? "",
                    BodyType = string.IsNullOrWhiteSpace(route.RequestBodyKind) ? "none" : route.RequestBodyKind,
                    BodySchema = route.RequestBodySchema ?? "",
                    Parameters = route.ParametersSchema ?? "",
                    OutputSchema = route.ResponseSchema ?? "",
                    Description = BuildMappedRouteDescription(route),
                    HandlerTypeName = route.HandlerType ?? "",
                    HandlerMethodName = route.HandlerName ?? "",
                    HandlerArguments = "",
                    AuthMode = "session",
                    Scopes = "sql:read",
                    RateLimitPerMinute = 120,
                    CorsPolicy = "same-origin",
                    InputSchema = route.RequestBodySchema ?? "",
                    PublicEnabled = false,
                    EndpointSecret = "",
                    Enabled = true,
                    Source = "mapped",
                    ReadOnly = true
                };
                ApplyApiMetadata(ep, settings);
                endpoints.Add(ep);
                seenRoutes.Add(routeKey);
            }

            return endpoints;
        }

        private static string BuildMappedRouteDescription(HttpMappedRoute route) {
            if (route == null)
                return "";
            if (!string.IsNullOrWhiteSpace(route.HandlerSignature))
                return "Runtime handler: " + route.HandlerSignature;
            if (!string.IsNullOrWhiteSpace(route.HandlerType) || !string.IsNullOrWhiteSpace(route.HandlerName))
                return "Runtime handler: " + ((route.HandlerType ?? "") + "." + (route.HandlerName ?? "")).Trim('.');
            return "Mapped at runtime with HttpServer.Map.";
        }
        private static void ApplyApiMetadata(ApiEndpointDef target, ApiEndpointDef settings) {
            if (target == null || settings == null)
                return;

            if (!string.IsNullOrWhiteSpace(settings.Name)) target.Name = settings.Name;
            target.Description = settings.Description ?? "";
            target.Variables = settings.Variables ?? "";
            target.BodyType = string.IsNullOrWhiteSpace(settings.BodyType) ? "none" : settings.BodyType;
            target.BodySchema = settings.BodySchema ?? "";
            target.Parameters = settings.Parameters ?? "";
            target.OutputSchema = settings.OutputSchema ?? "";
            target.ResponseFormat = string.IsNullOrWhiteSpace(settings.ResponseFormat) ? target.ResponseFormat : settings.ResponseFormat;
            target.ContentType = settings.ContentType ?? "";
            target.HandlerTypeName = settings.HandlerTypeName ?? "";
            target.HandlerMethodName = settings.HandlerMethodName ?? "";
            target.HandlerArguments = settings.HandlerArguments ?? "";
            target.AuthMode = NormalizeApiAuthMode(settings.AuthMode);
            target.Scopes = settings.Scopes ?? "sql:read";
            target.RateLimitPerMinute = settings.RateLimitPerMinute <= 0 ? target.RateLimitPerMinute : settings.RateLimitPerMinute;
            target.CorsPolicy = string.IsNullOrWhiteSpace(settings.CorsPolicy) ? target.CorsPolicy : settings.CorsPolicy;
            target.InputSchema = settings.InputSchema ?? target.InputSchema ?? "";
            target.PublicEnabled = settings.PublicEnabled;
            target.EndpointSecret = settings.EndpointSecret ?? "";
        }

        private static void AppendApiEndpointJson(StringBuilder sb, ApiEndpointDef ep) {
            sb.Append(@"{""id"":""").Append(EscapeJson(ep.Id))
              .Append(@""",""name"":""").Append(EscapeJson(ep.Name))
              .Append(@""",""route"":""").Append(EscapeJson(ep.Route))
              .Append(@""",""httpMethod"":""").Append(EscapeJson(ep.HttpMethod))
              .Append(@""",""database"":""").Append(EscapeJson(ep.Database ?? ""))
              .Append(@""",""sqlQuery"":""").Append(EscapeJson(ep.SqlQuery ?? ""))
              .Append(@""",""querySteps"":""").Append(EscapeJson(ep.QuerySteps ?? ""))
              .Append(@""",""responseFormat"":""").Append(EscapeJson(ep.ResponseFormat ?? ""))
              .Append(@""",""contentType"":""").Append(EscapeJson(ep.ContentType ?? ""))
              .Append(@""",""variables"":""").Append(EscapeJson(ep.Variables ?? ""))
              .Append(@""",""bodyType"":""").Append(EscapeJson(ep.BodyType ?? "none"))
              .Append(@""",""bodySchema"":""").Append(EscapeJson(ep.BodySchema ?? ""))
              .Append(@""",""parameters"":""").Append(EscapeJson(ep.Parameters ?? ""))
              .Append(@""",""outputSchema"":""").Append(EscapeJson(ep.OutputSchema ?? ""))
              .Append(@""",""description"":""").Append(EscapeJson(ep.Description ?? ""))
              .Append(@""",""handlerTypeName"":""").Append(EscapeJson(ep.HandlerTypeName ?? ""))
              .Append(@""",""handlerMethodName"":""").Append(EscapeJson(ep.HandlerMethodName ?? ""))
              .Append(@""",""handlerArguments"":""").Append(EscapeJson(ep.HandlerArguments ?? ""))
              .Append(@""",""authMode"":""").Append(EscapeJson(NormalizeApiAuthMode(ep.AuthMode)))
              .Append(@""",""scopes"":""").Append(EscapeJson(ep.Scopes ?? "sql:read"))
              .Append(@""",""rateLimitPerMinute"":").Append(ep.RateLimitPerMinute <= 0 ? 60 : ep.RateLimitPerMinute)
              .Append(@",""corsPolicy"":""").Append(EscapeJson(ep.CorsPolicy ?? "same-origin"))
              .Append(@""",""inputSchema"":""").Append(EscapeJson(ep.InputSchema ?? ""))
              .Append(@""",""publicEnabled"":").Append(ep.PublicEnabled ? "true" : "false")
              .Append(@",""endpointSecretConfigured"":").Append(string.IsNullOrWhiteSpace(ep.EndpointSecret) ? "false" : "true")
              .Append(@""",""enabled"":").Append(ep.Enabled ? "true" : "false")
              .Append(@",""source"":""").Append(EscapeJson(string.IsNullOrEmpty(ep.Source) ? "creator" : ep.Source))
              .Append(@""",""readOnly"":").Append(ep.ReadOnly ? "true" : "false")
              .Append("}");
        }

        private bool IsAdminPanelRoute(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var basePath = BasePath.TrimEnd('/');
            if (string.IsNullOrEmpty(basePath))
                basePath = "/";

            if (path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                || path.Equals(basePath + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            var adminSuffixes = new[] {
                "/login",
                "/logout",
                "/api/databases",
                "/api/tables",
                "/api/columns",
                "/api/rows",
                "/api/query",
                "/api/users",
                "/api/sessions",
                "/api/audit/recent",
                "/api/bootstrap/sa",
                "/api/designer/create-table",
                "/api/designer/drop-table",
                "/api/designer/rename-table",
                "/api/designer/save-schema",
                "/api/designer/add-column",
                "/api/designer/remove-column",
                "/api/designer/update-column",
                "/api/designer/insert-row",
                "/api/designer/update-cell",
                "/api/designer/delete-row",
                "/api/designer/viewport",
                "/api/endpoints/list",
                "/api/endpoints/save",
                "/api/endpoints/delete",
                "/api/endpoints/test",
                "/api/endpoints/settings/save",
                "/api/endpoints/reflect",
                "/api/qb/list-trees",
                "/api/qb/load-tree",
                "/api/qb/save-tree",
                "/api/qb/delete-tree",
                "/api/events/list",
                "/api/events/save",
                "/api/events/delete",
                "/api/events/execute",
                "/api/events/reflect"
            };

            return adminSuffixes.Any(suffix =>
                path.Equals(AppendAdminRouteSuffix(basePath, suffix), StringComparison.OrdinalIgnoreCase));
        }

        private static string AppendAdminRouteSuffix(string basePath, string suffix) {
            if (basePath == "/")
                return suffix;
            return basePath + suffix;
        }

        private static bool IsRestApiMethod(string method) {
            if (string.IsNullOrWhiteSpace(method))
                return false;

            switch (method.ToUpperInvariant()) {
                case "GET":
                case "POST":
                case "PUT":
                case "PATCH":
                case "DELETE":
                case "HEAD":
                case "OPTIONS":
                case "TRACE":
                case "CONNECT":
                    return true;
                default:
                    return false;
            }
        }

        private static string ApiEndpointRouteKey(string method, string route) {
            return (method ?? "GET").ToUpperInvariant() + " " + NormalizeApiEndpointRoute(route);
        }

        private static string NormalizeApiEndpointRoute(string route) {
            if (string.IsNullOrWhiteSpace(route))
                return "/";
            var q = route.IndexOf('?');
            if (q >= 0)
                route = route.Substring(0, q);
            if (!route.StartsWith("/"))
                route = "/" + route;
            if (route.Length > 1 && route.EndsWith("/", StringComparison.Ordinal))
                route = route.Substring(0, route.Length - 1);
            return route;
        }

        private static string MappedEndpointId(HttpMappedRoute route) {
            return MappedEndpointId(route.Method, route.Path);
        }

        private static string MappedEndpointId(string method, string path) {
            var raw = (method ?? "") + ":" + (path ?? "");
            return "mapped-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        private object ApiEndpointsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "api-endpoints.save", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var id = ExtractJsonProperty(body, "id");
            var name = ExtractJsonProperty(body, "name");
            var route = ExtractJsonProperty(body, "route");
            var httpMethod = ExtractJsonProperty(body, "httpMethod") ?? "GET";
            var database = ExtractJsonProperty(body, "database") ?? "db";
            var sqlQuery = ExtractJsonProperty(body, "sqlQuery");
            var querySteps = ExtractJsonProperty(body, "querySteps");
            var responseFormat = ExtractJsonProperty(body, "responseFormat") ?? "json";
            var contentType = ExtractJsonProperty(body, "contentType");
            var variables = ExtractJsonProperty(body, "variables");
            var bodyType = ExtractJsonProperty(body, "bodyType") ?? "none";
            var bodySchema = ExtractJsonProperty(body, "bodySchema");
            var parameters = ExtractJsonProperty(body, "parameters");
            var outputSchema = ExtractJsonProperty(body, "outputSchema");
            var description = ExtractJsonProperty(body, "description");
            var handlerTypeName = ExtractJsonProperty(body, "handlerTypeName");
            var handlerMethodName = ExtractJsonProperty(body, "handlerMethodName");
            var handlerArguments = ExtractJsonProperty(body, "handlerArguments");
            var authMode = NormalizeApiAuthMode(ExtractJsonProperty(body, "authMode") ?? "session");
            var scopes = ExtractJsonProperty(body, "scopes") ?? "sql:read";
            var rateLimitPerMinute = ParsePositiveInt(ExtractJsonProperty(body, "rateLimitPerMinute"), 60);
            var corsPolicy = ExtractJsonProperty(body, "corsPolicy") ?? "same-origin";
            var inputSchema = ExtractJsonProperty(body, "inputSchema") ?? bodySchema;
            var publicEnabled = ExtractJsonBool(body, "publicEnabled");
            var endpointSecret = ExtractJsonProperty(body, "endpointSecret");
            var enabledStr = ExtractJsonProperty(body, "enabled") ?? "true";

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Endpoint name is required.\"}";
            if (string.IsNullOrWhiteSpace(route)) return "{\"error\":\"Route path is required.\"}";
            var hasHandler = !string.IsNullOrWhiteSpace(handlerTypeName) && !string.IsNullOrWhiteSpace(handlerMethodName);
            // Allow empty SQL when query steps or a reflected handler are provided.
            if (string.IsNullOrWhiteSpace(sqlQuery) && string.IsNullOrWhiteSpace(querySteps) && !hasHandler) return "{\"error\":\"SQL query or handler target is required.\"}";

            if (!route.StartsWith("/")) route = "/" + route;
            httpMethod = httpMethod.ToUpperInvariant();
            if (!IsRestApiMethod(httpMethod)) httpMethod = "GET";
            var ds = GetDataServer();
            if (ds != null && !string.IsNullOrWhiteSpace(database) && !CanSqlAdminSessionAccessDatabase(req, session, ds, database))
                return SqlTenantForbiddenJson(req, database);
            if (authMode == "secret" && string.IsNullOrWhiteSpace(endpointSecret))
                endpointSecret = GenerateToken();
            if (authMode == "public" && !publicEnabled)
                return "{\"error\":\"Public auth mode requires publicEnabled=true so the exposure is explicit.\"}";

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            // If updating, remove old route first
            if (!isNew && _apiEndpoints.TryGetValue(id, out var oldEp)) {
                try { _server.RemoveRoute(oldEp.HttpMethod.ToUpperInvariant(), oldEp.Route); } catch { }
            }

            var ep = new ApiEndpointDef {
                Id = id,
                Name = name,
                Route = route,
                HttpMethod = httpMethod,
                Database = database,
                SqlQuery = sqlQuery,
                QuerySteps = querySteps,
                ResponseFormat = responseFormat,
                ContentType = contentType,
                Variables = variables,
                BodyType = bodyType,
                BodySchema = bodySchema,
                Parameters = parameters,
                OutputSchema = outputSchema,
                Description = description,
                HandlerTypeName = handlerTypeName,
                HandlerMethodName = handlerMethodName,
                HandlerArguments = handlerArguments,
                AuthMode = authMode,
                Scopes = scopes,
                RateLimitPerMinute = rateLimitPerMinute,
                CorsPolicy = corsPolicy,
                InputSchema = inputSchema,
                PublicEnabled = publicEnabled,
                EndpointSecret = endpointSecret,
                Source = "creator",
                ReadOnly = false,
                Enabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase)
            };

            _apiEndpoints[id] = ep;

            // Register the dynamic route
            if (ep.Enabled) {
                try { _server.Map(httpMethod, route, (conn, r, ct) => HandleDynamicApiEndpoint(r, ep)); } catch { }
            }

            SaveApiEndpointsTable();
            WriteSqlAudit(req, session, "api-endpoint.save", database, route, "success", 0, "API Creator endpoint saved.", "", "", "", id, "api");
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object ApiEndpointSettingsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return @"{""error"":""Not authenticated.""}"; }
            if (!ValidateSqlAdminMutation(req, session, "api-endpoints.settings-save", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var id = ExtractJsonProperty(body, "id");
            var route = ExtractJsonProperty(body, "route");
            var httpMethod = (ExtractJsonProperty(body, "httpMethod") ?? "GET").ToUpperInvariant();
            var name = ExtractJsonProperty(body, "name");
            var description = ExtractJsonProperty(body, "description");
            var variables = ExtractJsonProperty(body, "variables");
            var bodyType = ExtractJsonProperty(body, "bodyType") ?? "none";
            var bodySchema = ExtractJsonProperty(body, "bodySchema");
            var parameters = ExtractJsonProperty(body, "parameters");
            var outputSchema = ExtractJsonProperty(body, "outputSchema");
            var responseFormat = ExtractJsonProperty(body, "responseFormat") ?? "handler";
            var contentType = ExtractJsonProperty(body, "contentType");
            var handlerTypeName = ExtractJsonProperty(body, "handlerTypeName");
            var handlerMethodName = ExtractJsonProperty(body, "handlerMethodName");
            var handlerArguments = ExtractJsonProperty(body, "handlerArguments");
            var authMode = NormalizeApiAuthMode(ExtractJsonProperty(body, "authMode") ?? "session");
            var scopes = ExtractJsonProperty(body, "scopes") ?? "sql:read";
            var rateLimitPerMinute = ParsePositiveInt(ExtractJsonProperty(body, "rateLimitPerMinute"), 120);
            var corsPolicy = ExtractJsonProperty(body, "corsPolicy") ?? "same-origin";
            var inputSchema = ExtractJsonProperty(body, "inputSchema") ?? bodySchema;
            var publicEnabled = ExtractJsonBool(body, "publicEnabled");
            var endpointSecret = ExtractJsonProperty(body, "endpointSecret");
            if (authMode == "public" && !publicEnabled)
                return "{\"error\":\"Public auth mode requires publicEnabled=true so the exposure is explicit.\"}";

            ApiEndpointDef existingCreator = null;
            if (!string.IsNullOrEmpty(id)) {
                _apiEndpoints.TryGetValue(id, out existingCreator);
            }

            if (existingCreator != null) {
                if (!string.IsNullOrWhiteSpace(name)) existingCreator.Name = name;
                existingCreator.Description = description;
                existingCreator.Variables = variables;
                existingCreator.BodyType = bodyType;
                existingCreator.BodySchema = bodySchema;
                existingCreator.Parameters = parameters;
                existingCreator.OutputSchema = outputSchema;
                existingCreator.ResponseFormat = string.IsNullOrWhiteSpace(responseFormat) ? existingCreator.ResponseFormat : responseFormat;
                existingCreator.ContentType = contentType;
                existingCreator.HandlerTypeName = handlerTypeName;
                existingCreator.HandlerMethodName = handlerMethodName;
                existingCreator.HandlerArguments = handlerArguments;
                existingCreator.AuthMode = authMode;
                existingCreator.Scopes = scopes;
                existingCreator.RateLimitPerMinute = rateLimitPerMinute;
                existingCreator.CorsPolicy = corsPolicy;
                existingCreator.InputSchema = inputSchema;
                existingCreator.PublicEnabled = publicEnabled;
                existingCreator.EndpointSecret = authMode == "secret"
                    ? (string.IsNullOrWhiteSpace(endpointSecret) ? (existingCreator.EndpointSecret ?? GenerateToken()) : endpointSecret)
                    : (endpointSecret ?? "");
                SaveApiEndpointsTable();
                WriteSqlAudit(req, session, "api-endpoint.settings", existingCreator.Database, existingCreator.Route, "success", 0, "API Creator endpoint settings saved.", "", "", "", existingCreator.Id, "api");
                return "{\"success\":true,\"id\":\"" + EscapeJson(existingCreator.Id) + "\",\"source\":\"creator\"}";
            }

            if (string.IsNullOrWhiteSpace(route)) return @"{""error"":""Route path is required.""}";
            route = NormalizeApiEndpointRoute(route);
            if (string.IsNullOrEmpty(id)) id = MappedEndpointId(httpMethod, route);

            var setting = new ApiEndpointDef {
                Id = id,
                Name = name,
                Route = route,
                HttpMethod = httpMethod,
                Database = "",
                SqlQuery = "",
                QuerySteps = "",
                ResponseFormat = responseFormat,
                ContentType = contentType,
                Variables = variables,
                BodyType = bodyType,
                BodySchema = bodySchema,
                Parameters = parameters,
                OutputSchema = outputSchema,
                Description = description,
                HandlerTypeName = handlerTypeName,
                HandlerMethodName = handlerMethodName,
                HandlerArguments = handlerArguments,
                AuthMode = authMode,
                Scopes = scopes,
                RateLimitPerMinute = rateLimitPerMinute,
                CorsPolicy = corsPolicy,
                InputSchema = inputSchema,
                PublicEnabled = publicEnabled,
                EndpointSecret = authMode == "secret" && string.IsNullOrWhiteSpace(endpointSecret) ? GenerateToken() : endpointSecret,
                Enabled = true,
                Source = "mapped",
                ReadOnly = true
            };

            _apiRouteSettings[ApiEndpointRouteKey(httpMethod, route)] = setting;
            SaveApiRouteSettingsTable();
            WriteSqlAudit(req, session, "api-endpoint.settings", "", route, "success", 0, "Runtime API route settings saved.", "", "", "", id, "api");
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\",\"source\":\"mapped\"}";
        }

        private object ApiEndpointsDelete(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "api-endpoints.delete", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Endpoint ID is required.\"}";

            if (_apiEndpoints.TryRemove(id, out var ep)) {
                try { _server.RemoveRoute(ep.HttpMethod.ToUpperInvariant(), ep.Route); } catch { }
                WriteSqlAudit(req, session, "api-endpoint.delete", ep.Database, ep.Route, "success", 0, "API Creator endpoint deleted.", "", "", "", id, "api");
            }

            SaveApiEndpointsTable();
            return "{\"success\":true}";
        }

        private object ApiEndpointsTest(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "api-endpoints.test", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var tempEp = new ApiEndpointDef {
                Database = ExtractJsonString(body, "database") ?? "db",
                SqlQuery = ExtractJsonString(body, "sqlQuery") ?? "",
                QuerySteps = ExtractJsonString(body, "querySteps") ?? "",
                ResponseFormat = ExtractJsonString(body, "responseFormat") ?? "json",
                ContentType = ExtractJsonString(body, "contentType"),
                HttpMethod = ExtractJsonString(body, "httpMethod") ?? "GET",
                Variables = ExtractJsonString(body, "variables"),
                HandlerTypeName = ExtractJsonString(body, "handlerTypeName"),
                HandlerMethodName = ExtractJsonString(body, "handlerMethodName"),
                HandlerArguments = ExtractJsonString(body, "handlerArguments"),
                AuthMode = "session",
                Scopes = ExtractJsonString(body, "scopes") ?? "sql:read",
                RateLimitPerMinute = ParsePositiveInt(ExtractJsonString(body, "rateLimitPerMinute"), 60),
                CorsPolicy = ExtractJsonString(body, "corsPolicy") ?? "same-origin",
                InputSchema = ExtractJsonString(body, "inputSchema") ?? ExtractJsonString(body, "bodySchema"),
                PublicEnabled = false
            };

            var paramsJson = ExtractJsonString(body, "parameters") ?? "{}";
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractAllJsonValues(paramsJson, parameters);

            return ExecuteEndpointQuery(req, tempEp, parameters);
        }

        private object HandleDynamicApiEndpoint(HttpRequest req, ApiEndpointDef endpoint) {
            if (!ValidateDynamicEndpointRequest(req, endpoint, out var policyError))
                return policyError;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Gather querystring parameters
            if (req.QueryParameters != null) {
                foreach (var kvp in req.QueryParameters)
                    parameters[kvp.Key] = kvp.Value;
            }

            // Gather POST body parameters
            if (!string.IsNullOrEmpty(req.Body)) {
                var ct = "";
                if (req.Headers != null && req.Headers.TryGetValue("Content-Type", out var ctVal))
                    ct = (ctVal ?? "").ToLowerInvariant();

                if (ct.Contains("application/x-www-form-urlencoded")) {
                    foreach (var pair in req.Body.Split('&')) {
                        var eqIdx = pair.IndexOf('=');
                        if (eqIdx > 0) {
                            var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx));
                            var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1).Replace('+', ' '));
                            parameters[key] = val;
                        }
                    }
                } else {
                    ExtractAllJsonValues(req.Body, parameters);
                }
            }

            try {
                var result = ExecuteEndpointQuery(req, endpoint, parameters);
                WriteSqlAudit(req, GetSession(req), "api-endpoint.invoke", endpoint.Database ?? "", endpoint.Route ?? "", "success", 0, "Dynamic API endpoint invoked.", "", "", "", endpoint.Id ?? "", "api");
                return result;
            } catch (Exception ex) {
                WriteSqlAudit(req, GetSession(req), "api-endpoint.invoke", endpoint.Database ?? "", endpoint.Route ?? "", "error", 0, ex.Message, "", "", "", endpoint.Id ?? "", "api");
                throw;
            }
        }

        private bool ValidateDynamicEndpointRequest(HttpRequest req, ApiEndpointDef endpoint, out string errorJson) {
            errorJson = null;
            req.Context.ContentType = "application/json";
            if (endpoint == null || !endpoint.Enabled) {
                req.Context.StatusCode = "404 Not Found";
                errorJson = "{\"error\":\"Endpoint is disabled or not found.\"}";
                return false;
            }

            ApplyDynamicEndpointCors(req, endpoint);
            if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase)) {
                errorJson = "{\"ok\":true}";
                return false;
            }

            if (!CheckDynamicEndpointRateLimit(req, endpoint)) {
                req.Context.StatusCode = "429 Too Many Requests";
                errorJson = "{\"error\":\"Endpoint rate limit exceeded.\"}";
                return false;
            }

            if (!ValidateEndpointInputSchema(req, endpoint, out var schemaError)) {
                req.Context.StatusCode = "400 Bad Request";
                errorJson = "{\"error\":\"" + EscapeJson(schemaError) + "\",\"schemaValidation\":true}";
                return false;
            }

            string mode = NormalizeApiAuthMode(endpoint.AuthMode);
            if (mode == "public" && endpoint.PublicEnabled)
                return true;

            if (mode == "secret") {
                if (ValidateEndpointSecret(req, endpoint))
                    return true;
                req.Context.StatusCode = "401 Unauthorized";
                errorJson = "{\"error\":\"Endpoint secret is required.\"}";
                return false;
            }

            var session = GetSession(req);
            if (session == null) {
                req.Context.StatusCode = "401 Unauthorized";
                errorJson = "{\"error\":\"SQL Admin session is required for this endpoint.\"}";
                return false;
            }

            var ds = GetDataServer();
            if (ds != null && !string.IsNullOrWhiteSpace(endpoint.Database) && !CanSqlAdminSessionAccessDatabase(req, session, ds, endpoint.Database)) {
                errorJson = SqlTenantForbiddenJson(req, endpoint.Database);
                return false;
            }

            return true;
        }

        private static string NormalizeApiAuthMode(string mode) {
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode == "public" || mode == "secret" || mode == "session")
                return mode;
            return "session";
        }

        private static int ParsePositiveInt(string value, int defaultValue) {
            if (int.TryParse(value, out var parsed) && parsed > 0)
                return parsed;
            return defaultValue;
        }

        private void ApplyDynamicEndpointCors(HttpRequest req, ApiEndpointDef endpoint) {
            string origin = GetHeader(req, "Origin");
            string policy = (endpoint?.CorsPolicy ?? "same-origin").Trim();
            if (string.IsNullOrWhiteSpace(origin) || string.Equals(policy, "same-origin", StringComparison.OrdinalIgnoreCase) || string.Equals(policy, "deny", StringComparison.OrdinalIgnoreCase))
                return;

            bool allowed = policy == "*" ||
                           policy.Equals("any", StringComparison.OrdinalIgnoreCase) ||
                           policy.Split(',').Any(item => string.Equals(item.Trim(), origin, StringComparison.OrdinalIgnoreCase));
            if (!allowed)
                return;

            req.Context.Response.Headers["Access-Control-Allow-Origin"] = policy == "*" || policy.Equals("any", StringComparison.OrdinalIgnoreCase) ? origin : origin;
            req.Context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            req.Context.Response.Headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-SqlAdmin-Csrf, X-CSRF-Token, X-SocketJack-Api-Secret";
            req.Context.Response.Headers["Access-Control-Allow-Methods"] = (endpoint?.HttpMethod ?? "GET") + ", OPTIONS";
        }

        private bool CheckDynamicEndpointRateLimit(HttpRequest req, ApiEndpointDef endpoint) {
            int limit = endpoint?.RateLimitPerMinute <= 0 ? 60 : endpoint.RateLimitPerMinute;
            string key = (endpoint?.Id ?? endpoint?.Route ?? "dynamic") + ":" + ExtractSqlAdminClientIp(req);
            var window = _dynamicEndpointRateWindows.GetOrAdd(key, _ => new List<DateTime>());
            var cutoff = DateTime.UtcNow.AddMinutes(-1);
            lock (window) {
                window.RemoveAll(ts => ts < cutoff);
                if (window.Count >= limit)
                    return false;
                window.Add(DateTime.UtcNow);
                return true;
            }
        }

        private bool ValidateEndpointSecret(HttpRequest req, ApiEndpointDef endpoint) {
            if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.EndpointSecret))
                return false;
            string supplied = GetHeader(req, "X-SocketJack-Api-Secret")
                ?? GetHeader(req, "Authorization");
            if (!string.IsNullOrWhiteSpace(supplied) && supplied.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                supplied = supplied.Substring("Bearer ".Length).Trim();
            if (string.IsNullOrWhiteSpace(supplied) && req?.QueryParameters != null)
                req.QueryParameters.TryGetValue("apiKey", out supplied);
            return string.Equals(supplied, endpoint.EndpointSecret, StringComparison.Ordinal);
        }

        private bool ValidateEndpointInputSchema(HttpRequest req, ApiEndpointDef endpoint, out string error) {
            error = null;
            string schema = endpoint?.InputSchema;
            if (string.IsNullOrWhiteSpace(schema))
                schema = endpoint?.BodySchema;
            if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(req?.Body))
                return true;

            try {
                using (var schemaDoc = JsonDocument.Parse(schema))
                using (var bodyDoc = JsonDocument.Parse(req.Body)) {
                    if (schemaDoc.RootElement.ValueKind != JsonValueKind.Object)
                        return true;
                    if (schemaDoc.RootElement.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array) {
                        foreach (var item in required.EnumerateArray()) {
                            var propertyName = item.ValueKind == JsonValueKind.String ? item.GetString() : "";
                            if (!string.IsNullOrWhiteSpace(propertyName) &&
                                (bodyDoc.RootElement.ValueKind != JsonValueKind.Object || !bodyDoc.RootElement.TryGetProperty(propertyName, out _))) {
                                error = "Missing required field: " + propertyName;
                                return false;
                            }
                        }
                    }

                    if (schemaDoc.RootElement.TryGetProperty("properties", out var properties) &&
                        properties.ValueKind == JsonValueKind.Object &&
                        bodyDoc.RootElement.ValueKind == JsonValueKind.Object) {
                        foreach (var property in properties.EnumerateObject()) {
                            if (!bodyDoc.RootElement.TryGetProperty(property.Name, out var value))
                                continue;
                            if (property.Value.ValueKind == JsonValueKind.Object &&
                                property.Value.TryGetProperty("type", out var typeElement) &&
                                typeElement.ValueKind == JsonValueKind.String &&
                                !JsonValueMatchesSchemaType(value, typeElement.GetString())) {
                                error = "Field '" + property.Name + "' did not match type " + typeElement.GetString() + ".";
                                return false;
                            }
                        }
                    }
                }
                return true;
            } catch (Exception ex) {
                error = "Invalid JSON body or schema: " + ex.Message;
                return false;
            }
        }

        private static bool JsonValueMatchesSchemaType(JsonElement value, string type) {
            switch ((type ?? "").Trim().ToLowerInvariant()) {
                case "string": return value.ValueKind == JsonValueKind.String;
                case "number": return value.ValueKind == JsonValueKind.Number;
                case "integer": return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
                case "boolean": return value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False;
                case "object": return value.ValueKind == JsonValueKind.Object;
                case "array": return value.ValueKind == JsonValueKind.Array;
                case "null": return value.ValueKind == JsonValueKind.Null;
                default: return true;
            }
        }

        private bool ValidateEndpointSqlPolicy(HttpRequest req, ApiEndpointDef endpoint, string sql, out string errorJson) {
            errorJson = null;
            var risk = ClassifySqlRisk(sql);
            if (risk.Operation == "read")
                return true;

            bool writeScope = EndpointHasScope(endpoint, "sql:write") || EndpointHasScope(endpoint, "sql:admin");
            if (endpoint == null || endpoint.ReadOnly || !writeScope) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "403 Forbidden";
                errorJson = "{\"error\":\"Dynamic endpoint SQL write was blocked by endpoint policy.\",\"operation\":\"" + EscapeJson(risk.Operation) + "\",\"requiredScope\":\"sql:write\"}";
                WriteSqlAudit(req, GetSession(req), "api-endpoint.sql-blocked", endpoint?.Database ?? "", endpoint?.Route ?? "", "blocked", 0, "Dynamic API SQL write blocked by endpoint policy.", sql, "", "", endpoint?.Id ?? "", "api");
                return false;
            }

            if (risk.RequiresConfirmation) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "403 Forbidden";
                errorJson = "{\"error\":\"Dynamic endpoints cannot run destructive SQL without an interactive SQL Admin restore-point workflow.\",\"operation\":\"" + EscapeJson(risk.Operation) + "\"}";
                WriteSqlAudit(req, GetSession(req), "api-endpoint.sql-blocked", endpoint?.Database ?? "", endpoint?.Route ?? "", "blocked", 0, "Dynamic API destructive SQL blocked.", sql, "", "", endpoint?.Id ?? "", "api");
                return false;
            }

            return true;
        }

        private static bool EndpointHasScope(ApiEndpointDef endpoint, string scope) {
            if (endpoint == null || string.IsNullOrWhiteSpace(scope))
                return false;
            return (endpoint.Scopes ?? "").Split(',')
                .Any(item => string.Equals(item.Trim(), scope, StringComparison.OrdinalIgnoreCase));
        }

        private object ExecuteEndpointQuery(HttpRequest req, ApiEndpointDef endpoint, Dictionary<string, string> parameters) {
            if (!string.IsNullOrWhiteSpace(endpoint.HandlerTypeName)
                && !string.IsNullOrWhiteSpace(endpoint.HandlerMethodName)) {
                return ExecuteReflectedEndpoint(req, endpoint, parameters);
            }

            var ds = GetDataServer();
            if (ds == null) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "503 Service Unavailable";
                return "{\"error\":\"DataServer not available.\"}";
            }

            var sqlSession = new SqlSession {
                ConnectionId = Guid.NewGuid(),
                Username = "api",
                CurrentDatabase = endpoint.Database ?? "db",
                ServerName = ds.ServerName,
                ServerVersion = ds.ServerVersion,
                IsAuthenticated = true
            };

            // Create a working copy of parameters that we can add lookup results to
            var workingParams = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);

            // Check if we have query steps to process
            if (!string.IsNullOrEmpty(endpoint.QuerySteps)) {
                try {
                    return ExecuteQuerySteps(req, endpoint, workingParams, ds, sqlSession);
                } catch (Exception ex) {
                    req.Context.ContentType = "application/json";
                    req.Context.StatusCode = "500 Internal Server Error";
                    return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
                }
            }

            // Legacy: single SQL query execution
            var sql = SubstituteParameters(endpoint.SqlQuery ?? "", workingParams, endpoint.Variables);
            if (!ValidateEndpointSqlPolicy(req, endpoint, sql, out var policyError))
                return policyError;

            try {
                var result = ds.ExecuteQuery(sqlSession, sql);

                switch ((endpoint.ResponseFormat ?? "json").ToLowerInvariant()) {
                    case "plaintext":
                        return FormatResultAsPlaintext(result);
                    case "binary":
                        return FormatResultAsBinary(result, endpoint.ContentType ?? "application/octet-stream");
                    default:
                        return FormatResultAsJson(result);
                }
            } catch (Exception ex) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "500 Internal Server Error";
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        private object ExecuteQuerySteps(HttpRequest req, ApiEndpointDef endpoint, Dictionary<string, string> workingParams, DataServer ds, SqlSession sqlSession) {
            // Parse query steps JSON
            var stepsJson = endpoint.QuerySteps;
            var steps = ParseQuerySteps(stepsJson);
            if (steps.Count == 0) {
                return "{\"error\":\"No query steps defined.\"}";
            }

            QueryResult lastQueryResult = null;

            foreach (var step in steps) {
                var sql = SubstituteParameters(step.Sql ?? "", workingParams, endpoint.Variables);
                if (!ValidateEndpointSqlPolicy(req, endpoint, sql, out var policyError))
                    return policyError;

                var result = ds.ExecuteQuery(sqlSession, sql);

                switch (step.Type.ToLowerInvariant()) {
                    case "lookup":
                        // Extract first row, first column value and store as variable
                        if (!string.IsNullOrEmpty(step.OutputVar)) {
                            var varName = step.OutputVar.StartsWith("$") ? step.OutputVar.Substring(1) : step.OutputVar;
                            string extractedValue = null;

                            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                                extractedValue = result.Rows[0][0]?.ToString() ?? "";
                            }

                            if (extractedValue != null) {
                                workingParams[varName] = extractedValue;
                            } else {
                                // Lookup returned no results
                                req.Context.ContentType = "application/json";
                                req.Context.StatusCode = "404 Not Found";
                                return "{\"error\":\"Lookup step '" + EscapeJson(step.Name) + "' returned no results.\"}";
                            }
                        }
                        break;

                    case "validate":
                        // Check validation condition
                        bool valid = EvaluateValidationCondition(result, step);
                        if (!valid) {
                            req.Context.ContentType = "application/json";
                            req.Context.StatusCode = "403 Forbidden";
                            var errorMsg = string.IsNullOrEmpty(step.ErrorMsg) ? "Validation failed" : step.ErrorMsg;
                            return "{\"error\":\"" + EscapeJson(errorMsg) + "\"}";
                        }
                        break;

                    case "modify":
                        // Modify steps don't return data, just execute
                        // Continue to next step
                        break;

                    case "query":
                        // This is the main query that returns data
                        lastQueryResult = result;
                        break;
                }
            }

            // Return the last query result (or success message if no query step)
            if (lastQueryResult != null) {
                switch ((endpoint.ResponseFormat ?? "json").ToLowerInvariant()) {
                    case "plaintext":
                        return FormatResultAsPlaintext(lastQueryResult);
                    case "binary":
                        return FormatResultAsBinary(lastQueryResult, endpoint.ContentType ?? "application/octet-stream");
                    default:
                        return FormatResultAsJson(lastQueryResult);
                }
            }

            return "{\"success\":true,\"message\":\"All steps executed successfully.\"}";
        }

        private object ExecuteReflectedEndpoint(HttpRequest req, ApiEndpointDef endpoint, Dictionary<string, string> parameters) {
            // An authenticated admin session is not permission to execute arbitrary CLR methods.
            req.Context.StatusCode = "403 Forbidden";
            return "{\"error\":\"Remote reflection invocation is disabled. Register an explicit authorized handler in application code.\"}";
        }

        private object[] BuildReflectArguments(MethodInfo method, string argumentsJson, HttpRequest req, Dictionary<string, string> parameters) {
            var methodParameters = method.GetParameters();
            var args = new object[methodParameters.Length];
            JsonDocument argumentsDocument = null;
            JsonDocument bodyDocument = null;

            try {
                if (!string.IsNullOrWhiteSpace(argumentsJson))
                    argumentsDocument = JsonDocument.Parse(argumentsJson);
            } catch { }

            try {
                if (!string.IsNullOrWhiteSpace(req?.Body))
                    bodyDocument = JsonDocument.Parse(req.Body);
            } catch { }

            try {
                for (int i = 0; i < methodParameters.Length; i++) {
                    var p = methodParameters[i];
                    var pType = p.ParameterType;

                    if (pType == typeof(HttpRequest)) {
                        args[i] = req;
                        continue;
                    }
                    if (pType == typeof(DataServer)) {
                        args[i] = GetDataServer();
                        continue;
                    }
                    if (pType == typeof(Dictionary<string, string>) || pType == typeof(IDictionary<string, string>)) {
                        args[i] = parameters;
                        continue;
                    }
                    if (pType == typeof(CancellationToken)) {
                        args[i] = req?.Context?.cancellationToken ?? CancellationToken.None;
                        continue;
                    }

                    if (TryGetJsonArgument(argumentsDocument, p.Name, i, out var argElement)) {
                        args[i] = ConvertJsonElementToType(argElement, pType);
                        continue;
                    }
                    if (parameters != null && !string.IsNullOrEmpty(p.Name) && parameters.TryGetValue(p.Name, out var parameterValue)) {
                        args[i] = ConvertStringToType(parameterValue, pType);
                        continue;
                    }
                    if (TryGetJsonArgument(bodyDocument, p.Name, -1, out var bodyElement)) {
                        args[i] = ConvertJsonElementToType(bodyElement, pType);
                        continue;
                    }
                    if (bodyDocument != null && bodyDocument.RootElement.ValueKind != JsonValueKind.Object && methodParameters.Length == 1) {
                        args[i] = ConvertJsonElementToType(bodyDocument.RootElement, pType);
                        continue;
                    }
                    if (p.HasDefaultValue) {
                        args[i] = p.DefaultValue;
                        continue;
                    }
                    if (!pType.IsValueType || Nullable.GetUnderlyingType(pType) != null) {
                        args[i] = null;
                        continue;
                    }

                    args[i] = Activator.CreateInstance(pType);
                }
                return args;
            } finally {
                argumentsDocument?.Dispose();
                bodyDocument?.Dispose();
            }
        }

        private static bool TryGetJsonArgument(JsonDocument document, string name, int index, out JsonElement value) {
            value = default;
            if (document == null)
                return false;

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && index >= 0 && root.GetArrayLength() > index) {
                value = root[index];
                return true;
            }
            if (root.ValueKind == JsonValueKind.Object && !string.IsNullOrEmpty(name)) {
                foreach (var property in root.EnumerateObject()) {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) {
                        value = property.Value;
                        return true;
                    }
                }
            }
            return false;
        }

        private static object ConvertJsonElementToType(JsonElement element, Type targetType) {
            if (targetType == null || targetType == typeof(object))
                return System.Text.Json.JsonSerializer.Deserialize<object>(element.GetRawText(), _reflectJsonOptions);
            if (targetType == typeof(JsonElement))
                return element.Clone();
            if (targetType == typeof(string))
                return element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
            if (targetType == typeof(Wrapper))
                return System.Text.Json.JsonSerializer.Deserialize<Wrapper>(element.GetRawText(), _reflectJsonOptions);

            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null) {
                if (element.ValueKind == JsonValueKind.Null)
                    return null;
                targetType = nullableType;
            }
            if (targetType.IsEnum) {
                var enumText = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
                return Enum.Parse(targetType, enumText, true);
            }

            return System.Text.Json.JsonSerializer.Deserialize(element.GetRawText(), targetType, _reflectJsonOptions);
        }

        private static object ConvertStringToType(string value, Type targetType) {
            if (targetType == null || targetType == typeof(string))
                return value;
            if (string.Equals(value, "null", StringComparison.OrdinalIgnoreCase) && (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null))
                return null;
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null)
                targetType = nullableType;
            if (targetType.IsEnum)
                return Enum.Parse(targetType, value, true);
            if (targetType == typeof(Guid))
                return Guid.Parse(value);
            if (targetType == typeof(DateTime))
                return DateTime.Parse(value);
            if (targetType == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(value);
            return Convert.ChangeType(value, targetType);
        }

        private static object UnwrapTaskResult(object value) {
            if (value is System.Threading.Tasks.Task task) {
                task.GetAwaiter().GetResult();
                var taskType = value.GetType();
                if (taskType.IsGenericType) {
                    var resultProperty = taskType.GetProperty("Result");
                    return resultProperty?.GetValue(value);
                }
                return null;
            }
            return value;
        }

        private static Type FindReflectType(string typeName) {
            if (string.IsNullOrWhiteSpace(typeName))
                return null;
            var direct = Type.GetType(typeName, false, true);
            if (direct != null)
                return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
                if (assembly.IsDynamic)
                    continue;
                Type type = null;
                try { type = assembly.GetType(typeName, false, true); } catch { }
                if (type != null)
                    return type;
                foreach (var candidate in GetLoadableTypes(assembly)) {
                    if (string.Equals(candidate.FullName, typeName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(candidate.Name, typeName, StringComparison.OrdinalIgnoreCase))
                        return candidate;
                }
            }
            return null;
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly) {
            if (assembly == null || assembly.IsDynamic)
                return Enumerable.Empty<Type>();
            try {
                return assembly.GetTypes().Where(t => t != null);
            } catch (ReflectionTypeLoadException ex) {
                return ex.Types.Where(t => t != null);
            } catch {
                return Enumerable.Empty<Type>();
            }
        }

        private string SerializeReflectResultSafe(object value) {
            if (value == null)
                return "null";
            try {
                var wrapped = value is Wrapper wrapper ? wrapper : new Wrapper(value, _server);
                return SerializeReflectResult(wrapped);
            } catch {
                return SerializeReflectResult(value);
            }
        }

        private string SubstituteParameters(string sql, Dictionary<string, string> parameters, string variablesList) {
            // Substitute @param placeholders
            foreach (var kvp in parameters) {
                sql = sql.Replace("@" + kvp.Key, "'" + (kvp.Value ?? "").Replace("'", "''") + "'");
            }

            // Substitute $Variable placeholders from parameters
            foreach (var kvp in parameters) {
                sql = sql.Replace("$" + kvp.Key, "'" + (kvp.Value ?? "").Replace("'", "''") + "'");
            }

            // Also check declared variables list
            if (!string.IsNullOrEmpty(variablesList)) {
                foreach (var varEntry in variablesList.Split(',')) {
                    var varName = varEntry.Trim();
                    if (string.IsNullOrEmpty(varName)) continue;
                    var lookupKey = varName.StartsWith("$") ? varName.Substring(1) : varName;
                    var placeholder = varName.StartsWith("$") ? varName : "$" + varName;
                    if (parameters.TryGetValue(lookupKey, out var val)) {
                        sql = sql.Replace(placeholder, "'" + (val ?? "").Replace("'", "''") + "'");
                    }
                }
            }

            return sql;
        }

        private class QueryStep {
            public int Id;
            public string Type;
            public string Name;
            public string Sql;
            public string OutputVar;
            public string Condition;
            public string ConditionOp;
            public string ConditionVal;
            public string ErrorMsg;
        }

        private List<QueryStep> ParseQuerySteps(string stepsJson) {
            var steps = new List<QueryStep>();
            if (string.IsNullOrEmpty(stepsJson)) return steps;

            // Simple JSON array parsing for query steps
            // Format: [{"id":1,"type":"lookup","name":"...","sql":"...","outputVar":"$Var",...},...]
            try {
                var trimmed = stepsJson.Trim();
                if (!trimmed.StartsWith("[")) return steps;

                // Find each object in the array
                int depth = 0;
                int objStart = -1;
                for (int i = 0; i < trimmed.Length; i++) {
                    char c = trimmed[i];
                    if (c == '{') {
                        if (depth == 1) objStart = i;
                        depth++;
                    } else if (c == '}') {
                        depth--;
                        if (depth == 1 && objStart >= 0) {
                            var objJson = trimmed.Substring(objStart, i - objStart + 1);
                            var step = new QueryStep {
                                Id = int.TryParse(ExtractJsonString(objJson, "id"), out var id) ? id : 0,
                                Type = ExtractJsonString(objJson, "type") ?? "query",
                                Name = ExtractJsonString(objJson, "name") ?? "",
                                Sql = ExtractJsonString(objJson, "sql") ?? "",
                                OutputVar = ExtractJsonString(objJson, "outputVar") ?? "",
                                Condition = ExtractJsonString(objJson, "condition") ?? "",
                                ConditionOp = ExtractJsonString(objJson, "conditionOp") ?? "",
                                ConditionVal = ExtractJsonString(objJson, "conditionVal") ?? "",
                                ErrorMsg = ExtractJsonString(objJson, "errorMsg") ?? ""
                            };
                            steps.Add(step);
                            objStart = -1;
                        }
                    } else if (c == '[') {
                        depth++;
                    } else if (c == ']') {
                        depth--;
                    }
                }
            } catch {
                // If parsing fails, return empty steps
            }

            return steps;
        }

        private bool EvaluateValidationCondition(QueryResult result, QueryStep step) {
            double checkValue = 0;

            switch ((step.Condition ?? "").ToLowerInvariant()) {
                case "rowcount":
                    checkValue = result.Rows.Count;
                    break;
                case "value":
                case "scalar":
                    if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                        var val = result.Rows[0][0];
                        if (val != null) {
                            double.TryParse(val.ToString(), out checkValue);
                        }
                    }
                    break;
                default:
                    checkValue = result.Rows.Count;
                    break;
            }

            double compareValue = 0;
            double.TryParse(step.ConditionVal ?? "0", out compareValue);

            switch (step.ConditionOp ?? ">") {
                case ">": return checkValue > compareValue;
                case ">=": return checkValue >= compareValue;
                case "=": return Math.Abs(checkValue - compareValue) < 0.0001;
                case "==": return Math.Abs(checkValue - compareValue) < 0.0001;
                case "!=": return Math.Abs(checkValue - compareValue) >= 0.0001;
                case "<>": return Math.Abs(checkValue - compareValue) >= 0.0001;
                case "<": return checkValue < compareValue;
                case "<=": return checkValue <= compareValue;
                default: return checkValue > compareValue;
            }
        }

        private static string FormatResultAsJson(QueryResult result) {
            if (!result.HasResultSet || result.Columns.Count == 0) {
                return "{\"rowsAffected\":" + result.RowsAffected + ",\"message\":\"Query executed successfully.\"}";
            }
            var sb = new StringBuilder();
            sb.Append("[");
            for (int r = 0; r < result.Rows.Count; r++) {
                if (r > 0) sb.Append(",");
                sb.Append("{");
                var row = result.Rows[r];
                for (int c = 0; c < result.Columns.Count; c++) {
                    if (c > 0) sb.Append(",");
                    sb.Append("\"").Append(EscapeJson(result.Columns[c])).Append("\":");
                    if (c >= row.Length || row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string FormatResultAsPlaintext(QueryResult result) {
            if (!result.HasResultSet || result.Columns.Count == 0) {
                return "Query executed successfully. Rows affected: " + result.RowsAffected;
            }
            var sb = new StringBuilder();
            for (int c = 0; c < result.Columns.Count; c++) {
                if (c > 0) sb.Append("\t");
                sb.Append(result.Columns[c]);
            }
            sb.Append("\n");
            for (int r = 0; r < result.Rows.Count; r++) {
                var row = result.Rows[r];
                for (int c = 0; c < result.Columns.Count; c++) {
                    if (c > 0) sb.Append("\t");
                    sb.Append(c < row.Length && row[c] != null ? row[c].ToString() : "NULL");
                }
                sb.Append("\n");
            }
            return sb.ToString();
        }

        private static object FormatResultAsBinary(QueryResult result, string contentType) {
            byte[] data;
            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                var val = result.Rows[0][0];
                if (val is byte[] bytes) {
                    data = bytes;
                } else {
                    data = Encoding.UTF8.GetBytes(val?.ToString() ?? "");
                }
            } else {
                data = Array.Empty<byte>();
            }
            return new FileResponse(data, contentType);
        }

        private static void ExtractAllJsonValues(string json, Dictionary<string, string> parameters) {
            if (string.IsNullOrEmpty(json)) return;
            json = json.Trim();
            if (!json.StartsWith("{")) return;
            int pos = json.IndexOf('{') + 1;
            while (pos < json.Length) {
                while (pos < json.Length && " \r\n\t,".IndexOf(json[pos]) >= 0) pos++;
                if (pos >= json.Length || json[pos] == '}') break;
                if (json[pos] != '"') break;
                pos++;
                var keySb = new StringBuilder();
                while (pos < json.Length && json[pos] != '"') {
                    if (json[pos] == '\\' && pos + 1 < json.Length) { pos++; keySb.Append(json[pos]); }
                    else keySb.Append(json[pos]);
                    pos++;
                }
                var key = keySb.ToString();
                pos++;
                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':')) pos++;
                if (pos >= json.Length) break;
                if (json[pos] == '"') {
                    pos++;
                    var valSb = new StringBuilder();
                    while (pos < json.Length && json[pos] != '"') {
                        if (json[pos] == '\\' && pos + 1 < json.Length) { pos++; valSb.Append(json[pos]); }
                        else valSb.Append(json[pos]);
                        pos++;
                    }
                    pos++;
                    parameters[key] = valSb.ToString();
                } else {
                    int valStart = pos;
                    while (pos < json.Length && json[pos] != ',' && json[pos] != '}') pos++;
                    parameters[key] = json.Substring(valStart, pos - valStart).Trim();
                }
            }
        }

        #endregion

        #region Query Builder Trees

        private void EnsureQueryTreesTable(DataServer ds) {
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (configDb.Tables.ContainsKey("QueryTrees")) return;
            var table = new Table("QueryTrees");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Database", typeof(string), -1));
            table.Columns.Add(new Column("TreeJson", typeof(string), -1));
            table.Columns.Add(new Column("GeneratedSql", typeof(string), -1));
            table.Columns.Add(new Column("CreatedAt", typeof(string), -1));
            configDb.Tables["QueryTrees"] = table;
            ds.ScheduleSave();
        }

        private object QbListTrees(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"trees\":[]}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"trees\":[]}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"trees\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"trees\":[");
            bool first = true;
            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var rowDbForAccess = row.Length > 2 ? row[2]?.ToString() : "";
                if (!string.IsNullOrWhiteSpace(rowDbForAccess) && !CanSqlAdminSessionAccessDatabase(req, session, ds, rowDbForAccess))
                    continue;
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":\"").Append(EscapeJson(row.Length > 0 ? row[0]?.ToString() : ""))
                  .Append("\",\"name\":\"").Append(EscapeJson(row.Length > 1 ? row[1]?.ToString() : ""))
                  .Append("\",\"database\":\"").Append(EscapeJson(row.Length > 2 ? row[2]?.ToString() : ""))
                  .Append("\",\"treeJson\":\"").Append(EscapeJson(row.Length > 3 ? row[3]?.ToString() : ""))
                  .Append("\",\"generatedSql\":\"").Append(EscapeJson(row.Length > 4 ? row[4]?.ToString() : ""))
                  .Append("\",\"createdAt\":\"").Append(EscapeJson(row.Length > 5 ? row[5]?.ToString() : ""))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object QbLoadTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var name = req.QueryParameters.ContainsKey("name") ? req.QueryParameters["name"] : null;
            var dbFilter = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : null;

            if (string.IsNullOrEmpty(name)) { return "{\"error\":\"Query name is required.\"}"; }

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var rowName = row.Length > 1 ? row[1]?.ToString() : "";
                var rowDb = row.Length > 2 ? row[2]?.ToString() : "";

                if (name.Equals(rowName, StringComparison.OrdinalIgnoreCase)) {
                    // If db filter is specified, check it matches
                    if (!string.IsNullOrEmpty(dbFilter) && !dbFilter.Equals(rowDb, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!CanSqlAdminSessionAccessDatabase(req, session, ds, rowDb))
                        return SqlTenantForbiddenJson(req, rowDb);

                    var sb = new StringBuilder();
                    sb.Append("{\"id\":\"").Append(EscapeJson(row.Length > 0 ? row[0]?.ToString() : ""))
                      .Append("\",\"name\":\"").Append(EscapeJson(rowName))
                      .Append("\",\"database\":\"").Append(EscapeJson(rowDb))
                      .Append("\",\"tree\":\"").Append(EscapeJson(row.Length > 3 ? row[3]?.ToString() : ""))
                      .Append("\",\"generatedSql\":\"").Append(EscapeJson(row.Length > 4 ? row[4]?.ToString() : ""))
                      .Append("\",\"createdAt\":\"").Append(EscapeJson(row.Length > 5 ? row[5]?.ToString() : ""))
                      .Append("\"}");
                    return sb.ToString();
                }
            }

            return "{\"error\":\"Query not found.\"}";
        }

        private object QbSaveTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "query-builder.save", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            var name = ExtractJsonString(body, "name");
            var database = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var treeJson = ExtractJsonString(body, "treeJson");
            var generatedSql = ExtractJsonString(body, "generatedSql");

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Tree name is required.\"}";
            if (string.IsNullOrWhiteSpace(treeJson)) return "{\"error\":\"Tree data is required.\"}";
            if (!CanSqlAdminSessionAccessDatabase(req, session, ds, database)) return SqlTenantForbiddenJson(req, database);

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            // Update existing or insert new
            bool found = false;
            if (!isNew) {
                for (int r = 0; r < table.Rows.Count; r++) {
                    if (table.Rows[r].Length > 0 && id.Equals(table.Rows[r][0]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                        table.Rows[r] = new object[] { id, name, database, treeJson, generatedSql, table.Rows[r].Length > 5 ? table.Rows[r][5] : DateTime.UtcNow.ToString("o") };
                        found = true;
                        break;
                    }
                }
            }
            if (!found) {
                table.Rows.Add(new object[] { id, name, database, treeJson, generatedSql, DateTime.UtcNow.ToString("o") });
            }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "query-builder.save", database, name, "success", 0, "Query Builder tree saved.", generatedSql ?? "", "", "", id, "query-builder");
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object QbDeleteTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "query-builder.delete", out var mutationError)) return mutationError;

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Tree ID is required.\"}";

            bool removed = false;
            for (int r = table.Rows.Count - 1; r >= 0; r--) {
                if (table.Rows[r].Length > 0 && id.Equals(table.Rows[r][0]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                    string rowDb = table.Rows[r].Length > 2 ? table.Rows[r][2]?.ToString() : "";
                    if (!CanSqlAdminSessionAccessDatabase(req, session, ds, rowDb)) return SqlTenantForbiddenJson(req, rowDb);
                    table.Rows.RemoveAt(r);
                    removed = true;
                    break;
                }
            }

            ds.ScheduleSave();
            WriteSqlAudit(req, session, "query-builder.delete", "", id, removed ? "success" : "not_found", 0, "Query Builder tree delete requested.", "", "", "", id, "query-builder");
            return "{\"success\":true,\"removed\":" + (removed ? "true" : "false") + "}";
        }

        #endregion

        #region Events System

        private void LoadEventDefs() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("Events", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ev = new EventDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    Name = row.Length > 1 ? row[1]?.ToString() : null,
                    Description = row.Length > 2 ? row[2]?.ToString() : null,
                    Enabled = row.Length > 3 && (row[3]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    Nodes = row.Length > 4 ? row[4]?.ToString() : null,
                    Approved = row.Length > 5 && (row[5]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    ApprovedBy = row.Length > 6 ? row[6]?.ToString() : null,
                    ApprovedUtc = row.Length > 7 ? row[7]?.ToString() : null,
                    DryRunByDefault = row.Length <= 8 || !(row[8]?.ToString() ?? "").Equals("false", StringComparison.OrdinalIgnoreCase)
                };
                if (string.IsNullOrEmpty(ev.Id)) continue;
                _eventDefs[ev.Id] = ev;
            }
        }

        private void SaveEventsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("Events");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));
            table.Columns.Add(new Column("Enabled", typeof(string), 5));
            table.Columns.Add(new Column("Nodes", typeof(string), -1));
            table.Columns.Add(new Column("Approved", typeof(string), 5));
            table.Columns.Add(new Column("ApprovedBy", typeof(string), 160));
            table.Columns.Add(new Column("ApprovedUtc", typeof(string), 80));
            table.Columns.Add(new Column("DryRunByDefault", typeof(string), 5));

            foreach (var ev in _eventDefs.Values) {
                table.Rows.Add(new object[] {
                    ev.Id, ev.Name, ev.Description ?? "",
                    ev.Enabled ? "true" : "false", ev.Nodes ?? "[]",
                    ev.Approved ? "true" : "false", ev.ApprovedBy ?? "",
                    ev.ApprovedUtc ?? "", ev.DryRunByDefault ? "true" : "false"
                });
            }

            configDb.Tables["Events"] = table;
            ds.ScheduleSave();
        }

        private object ApiEventsList(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var sb = new StringBuilder();
            sb.Append("{\"events\":[");
            bool first = true;
            foreach (var ev in _eventDefs.Values) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":\"").Append(EscapeJson(ev.Id))
                  .Append("\",\"name\":\"").Append(EscapeJson(ev.Name))
                  .Append("\",\"description\":\"").Append(EscapeJson(ev.Description ?? ""))
                  .Append("\",\"enabled\":").Append(ev.Enabled ? "true" : "false")
                  .Append(",\"approved\":").Append(ev.Approved ? "true" : "false")
                  .Append(",\"approvedBy\":\"").Append(EscapeJson(ev.ApprovedBy ?? ""))
                  .Append("\",\"approvedUtc\":\"").Append(EscapeJson(ev.ApprovedUtc ?? ""))
                  .Append("\",\"dryRunByDefault\":").Append(ev.DryRunByDefault ? "true" : "false")
                  .Append(",\"nodes\":\"").Append(EscapeJson(ev.Nodes ?? "[]"))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiEventsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "events.save", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            var name = ExtractJsonString(body, "name");
            var description = ExtractJsonString(body, "description") ?? "";
            var nodes = ExtractJsonString(body, "nodes") ?? "[]";
            var enabledStr = ExtractJsonString(body, "enabled") ?? "true";
            bool approved = ExtractJsonBool(body, "approved");
            bool dryRunByDefault = !ExtractJsonBool(body, "dryRunDisabled");

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Event name is required.\"}";

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            var ev = new EventDef {
                Id = id,
                Name = name,
                Description = description,
                Enabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase),
                Approved = approved,
                ApprovedBy = approved ? session.Username : "",
                ApprovedUtc = approved ? DateTime.UtcNow.ToString("O") : "",
                DryRunByDefault = dryRunByDefault,
                Nodes = nodes
            };

            _eventDefs[id] = ev;
            SaveEventsTable();
            WriteSqlAudit(req, session, "event.save", "", id, "success", 0, "SQL Admin event definition saved.", nodes, "", "", id, "event");
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object ApiEventsDelete(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "events.delete", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Event ID is required.\"}";

            _eventDefs.TryRemove(id, out var removedEvent);
            SaveEventsTable();
            WriteSqlAudit(req, session, "event.delete", "", id, "success", 0, "SQL Admin event definition deleted.", removedEvent?.Nodes ?? "", "", "", id, "event");
            return "{\"success\":true}";
        }

        /// <summary>
        /// Executes a single action node: supports HTTP API calls, SQL queries, and
        /// reflection-based method invocation on the server.
        /// </summary>
        private object ApiEventsExecute(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }
            if (!ValidateSqlAdminMutation(req, session, "events.execute", out var mutationError)) return mutationError;

            var body = req.Body ?? "";
            var eventId = ExtractJsonString(body, "eventId") ?? ExtractJsonString(body, "id") ?? "";
            EventDef eventDef = null;
            if (!string.IsNullOrWhiteSpace(eventId)) {
                if (!_eventDefs.TryGetValue(eventId, out eventDef))
                    return "{\"error\":\"Event definition not found.\"}";
                if (!eventDef.Enabled)
                    return "{\"error\":\"Event definition is disabled.\"}";
                if (_eventCircuitFailures.TryGetValue(eventId, out var failures) && failures >= 5) {
                    req.Context.StatusCode = "429 Too Many Requests";
                    return "{\"error\":\"Event circuit breaker is open after repeated failures.\",\"eventId\":\"" + EscapeJson(eventId) + "\"}";
                }
            }
            var actionType = ExtractJsonString(body, "actionType") ?? "";
            bool dryRun = ExtractJsonBool(body, "dryRun") || (eventDef != null && eventDef.DryRunByDefault && !ExtractJsonBool(body, "executeNow"));
            if (dryRun) {
                WriteSqlAudit(req, session, "event.dry-run", "", eventId, "preview", 0, "SQL Admin event dry-run preview generated.", body, "", "", eventId, "event");
                return "{\"success\":true,\"dryRun\":true,\"eventId\":\"" + EscapeJson(eventId) + "\",\"actionType\":\"" + EscapeJson(actionType) + "\",\"wouldExecute\":true}";
            }
            var sb = new StringBuilder();

            try {
                switch (actionType.ToLowerInvariant()) {
                    case "http": {
                        // Invoke an HTTP request
                        var url = ExtractJsonString(body, "url") ?? "";
                        var method = (ExtractJsonString(body, "method") ?? "GET").ToUpperInvariant();
                        var payload = ExtractJsonString(body, "payload") ?? "";
                        if (string.IsNullOrWhiteSpace(url)) return "{\"error\":\"URL is required for HTTP action.\"}";
                        if (!ValidateEventHttpAction(req, url, method, out var httpError)) return httpError;

                        string result = ExecuteEventHttpAction(url, method, payload);
                        sb.Append("{\"success\":true,\"result\":\"").Append(EscapeJson(result)).Append("\"}");
                        WriteSqlAudit(req, session, "event.http", "", eventId, "success", 0, "SQL Admin event HTTP action executed.", RedactEventPayloadForAudit(url + " " + payload), "", "", eventId, "event");
                        ResetEventCircuit(eventId);
                        return sb.ToString();
                    }
                    case "sql": {
                        // Execute a SQL query on the DataServer
                        var database = ExtractJsonString(body, "database") ?? session.CurrentDatabase ?? "db";
                        var sql = ExtractJsonString(body, "sql") ?? "";
                        if (string.IsNullOrWhiteSpace(sql)) return "{\"error\":\"SQL query is required.\"}";

                        var ds = GetDataServer();
                        if (ds == null) return "{\"error\":\"DataServer not available.\"}";
                        if (!CanSqlAdminSessionAccessDatabase(req, session, ds, database)) return SqlTenantForbiddenJson(req, database);
                        var risk = ClassifySqlRisk(sql);
                        if (risk.RequiresConfirmation && (eventDef == null || !eventDef.Approved)) {
                            req.Context.StatusCode = "403 Forbidden";
                            return "{\"error\":\"High-risk SQL event actions require an approved event definition.\",\"operation\":\"" + EscapeJson(risk.Operation) + "\"}";
                        }
                        string restorePointId = "";
                        string beforeHash = "";
                        if (risk.RequiresConfirmation)
                            restorePointId = CreateSqlDesignerRestorePoint(ds, session, database, risk.Operation, "event.sql", out beforeHash);

                        var sqlSession = new SqlSession {
                            ConnectionId = Guid.NewGuid(),
                            Username = session.Username,
                            CurrentDatabase = database,
                            ServerName = ds.ServerName,
                            ServerVersion = ds.ServerVersion,
                            IsAuthenticated = true
                        };
                        var result = ds.ExecuteQuery(sqlSession, sql);
                        string afterHash = beforeHash;
                        if (!string.IsNullOrWhiteSpace(beforeHash) && ds.Databases.TryGetValue(database, out var afterDb))
                            afterHash = ComputeSqlDatabaseSnapshotHash(afterDb);
                        WriteSqlAudit(req, session, "event.sql", database, eventId, "success", result.RowsAffected, "SQL Admin event SQL action executed.", sql, beforeHash, afterHash, string.IsNullOrWhiteSpace(restorePointId) ? eventId : restorePointId, "event");
                        ResetEventCircuit(eventId);
                        sb.Append("{\"success\":true,\"result\":\"").Append(EscapeJson(FormatResultAsJson(result))).Append("\"}");
                        return sb.ToString();
                    }
                    case "reflect": {
                        req.Context.StatusCode = "403 Forbidden";
                        return "{\"error\":\"Remote reflection invocation is disabled. Register an explicit authorized handler in application code.\"}";
                    }
                    default:
                        return "{\"error\":\"Unknown actionType: " + EscapeJson(actionType) + ". Expected http, sql, or reflect.\"}";
                }
            } catch (Exception ex) {
                IncrementEventCircuit(eventId);
                WriteSqlAudit(req, session, "event.execute", "", eventId, "error", 0, ex.Message, RedactEventPayloadForAudit(body), "", "", eventId, "event");
                return "{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        private bool ValidateEventHttpAction(HttpRequest req, string url, string method, out string errorJson) {
            errorJson = null;
            method = (method ?? "GET").ToUpperInvariant();
            var allowedMethods = (Environment.GetEnvironmentVariable("SOCKETJACK_SQL_EVENT_HTTP_METHODS") ?? "GET,POST")
                .Split(',')
                .Select(m => m.Trim().ToUpperInvariant())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .ToList();
            if (!allowedMethods.Contains(method)) {
                errorJson = "{\"error\":\"HTTP event method is not allowlisted.\"}";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
                errorJson = "{\"error\":\"HTTP event URL must be absolute http or https.\"}";
                return false;
            }

            string allowlist = Environment.GetEnvironmentVariable("SOCKETJACK_SQL_EVENT_HTTP_ALLOWLIST") ?? "";
            var allowedHosts = allowlist.Split(',').Select(h => h.Trim()).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            if (allowedHosts.Count > 0 &&
                !allowedHosts.Any(pattern => HostMatchesAllowlist(uri.Host, pattern))) {
                errorJson = "{\"error\":\"HTTP event URL host is not allowlisted.\"}";
                return false;
            }

            if (IsPrivateOrLoopbackHost(uri.Host) && !IsLocalhostRequest(req)) {
                errorJson = "{\"error\":\"HTTP events cannot target private or loopback network addresses from non-local SQL Admin sessions.\"}";
                return false;
            }

            return true;
        }

        private static bool HostMatchesAllowlist(string host, string pattern) {
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(pattern))
                return false;
            if (pattern == "*")
                return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
                return host.EndsWith(pattern.Substring(1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPrivateOrLoopbackHost(string host) {
            if (string.IsNullOrWhiteSpace(host))
                return true;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            try {
                foreach (var address in Dns.GetHostAddresses(host)) {
                    if (IPAddress.IsLoopback(address))
                        return true;
                    byte[] bytes = address.GetAddressBytes();
                    if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4) {
                        if (bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254))
                            return true;
                    } else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && bytes.Length == 16) {
                        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xfe) == 0xfc)
                            return true;
                    }
                }
            } catch {
                return true;
            }
            return false;
        }

        private static string ExecuteEventHttpAction(string url, string method, string payload) {
            using (var client = new System.Net.Http.HttpClient()) {
                client.Timeout = TimeSpan.FromSeconds(10);
                using (var request = new System.Net.Http.HttpRequestMessage(new System.Net.Http.HttpMethod((method ?? "GET").ToUpperInvariant()), url)) {
                    if (!string.IsNullOrEmpty(payload) && request.Method != System.Net.Http.HttpMethod.Get && request.Method != System.Net.Http.HttpMethod.Head)
                        request.Content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");

                    using (var response = client.SendAsync(request).GetAwaiter().GetResult())
                        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
            }
        }


        private void ResetEventCircuit(string eventId) {
            if (!string.IsNullOrWhiteSpace(eventId))
                _eventCircuitFailures.TryRemove(eventId, out _);
        }

        private void IncrementEventCircuit(string eventId) {
            if (!string.IsNullOrWhiteSpace(eventId))
                _eventCircuitFailures.AddOrUpdate(eventId, 1, (_, count) => Math.Min(1000, count + 1));
        }

        private static string RedactEventPayloadForAudit(string text) {
            if (string.IsNullOrEmpty(text))
                return "";
            return System.Text.RegularExpressions.Regex.Replace(
                text,
                "(?i)(api[_-]?key|token|secret|password|authorization)([\"'\\s:=]+)([^\"'\\s,}]+)",
                "$1$2[redacted]");
        }

        /// <summary>
        /// Serializes an arbitrary return value from a reflected method call into JSON.
        /// Handles null, primitives, strings, enums, collections, and objects with public properties.
        /// </summary>
        private static string SerializeReflectResult(object value, int depth = 0) {
            if (depth > 8) return "\"(max depth)\"";
            if (value == null) return "null";

            var type = value.GetType();

            // Primitives and simple types
            if (value is string s) return "\"" + EscapeJson(s) + "\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is char c) return "\"" + EscapeJson(c.ToString()) + "\"";
            if (value is DateTime dt) return "\"" + EscapeJson(dt.ToString("o")) + "\"";
            if (value is DateTimeOffset dto) return "\"" + EscapeJson(dto.ToString("o")) + "\"";
            if (value is Guid g) return "\"" + g.ToString() + "\"";
            if (type.IsEnum) return "\"" + EscapeJson(value.ToString()) + "\"";

            // Numeric types
            if (value is int || value is long || value is short || value is byte
                || value is uint || value is ulong || value is ushort || value is sbyte
                || value is float || value is double || value is decimal) {
                return value.ToString();
            }

            // Dictionaries (IDictionary<string,?> or IDictionary)
            if (value is System.Collections.IDictionary dict) {
                var dsb = new StringBuilder("{");
                bool dFirst = true;
                foreach (System.Collections.DictionaryEntry entry in dict) {
                    if (!dFirst) dsb.Append(",");
                    dFirst = false;
                    dsb.Append("\"").Append(EscapeJson(entry.Key?.ToString() ?? "")).Append("\":");
                    dsb.Append(SerializeReflectResult(entry.Value, depth + 1));
                }
                dsb.Append("}");
                return dsb.ToString();
            }

            // Collections (IEnumerable)
            if (value is System.Collections.IEnumerable enumerable && !(value is string)) {
                var asb = new StringBuilder("[");
                bool aFirst = true;
                int itemCount = 0;
                foreach (var item in enumerable) {
                    if (itemCount >= 500) { asb.Append(",\"...(truncated)\""); break; }
                    if (!aFirst) asb.Append(",");
                    aFirst = false;
                    asb.Append(SerializeReflectResult(item, depth + 1));
                    itemCount++;
                }
                asb.Append("]");
                return asb.ToString();
            }

            // Objects: serialize public instance properties
            if (!type.IsPrimitive && type.IsClass) {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (props.Length == 0) return "\"" + EscapeJson(value.ToString()) + "\"";
                var osb = new StringBuilder("{");
                bool oFirst = true;
                foreach (var prop in props) {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    object propVal;
                    try { propVal = prop.GetValue(value); } catch { continue; }
                    if (!oFirst) osb.Append(",");
                    oFirst = false;
                    osb.Append("\"").Append(EscapeJson(prop.Name)).Append("\":");
                    osb.Append(SerializeReflectResult(propVal, depth + 1));
                }
                osb.Append("}");
                return osb.ToString();
            }

            // Structs with properties
            if (type.IsValueType && !type.IsPrimitive && !type.IsEnum) {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (props.Length > 0) {
                    var ssb = new StringBuilder("{");
                    bool sFirst = true;
                    foreach (var prop in props) {
                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                        object propVal;
                        try { propVal = prop.GetValue(value); } catch { continue; }
                        if (!sFirst) ssb.Append(",");
                        sFirst = false;
                        ssb.Append("\"").Append(EscapeJson(prop.Name)).Append("\":");
                        ssb.Append(SerializeReflectResult(propVal, depth + 1));
                    }
                    ssb.Append("}");
                    return ssb.ToString();
                }
            }

            // Fallback
            return "\"" + EscapeJson(value.ToString()) + "\"";
        }

        /// <summary>
        /// Returns true if the assembly is likely an application assembly rather than a
        /// .NET runtime / framework / system library.
        /// </summary>
        private static bool IsApplicationAssembly(Assembly asm) {
            if (asm.IsDynamic) return false;
            var name = asm.GetName().Name;
            if (string.IsNullOrEmpty(name)) return false;

            // Skip well-known system / framework prefixes
            string[] systemPrefixes = {
                "System", "Microsoft", "mscorlib", "netstandard",
                "WindowsBase", "PresentationCore", "PresentationFramework",
                "UIAutomation", "DirectWriteForwarder", "ReachFramework",
                "Accessibility", "NuGet", "Newtonsoft", "testhost"
            };
            foreach (var p in systemPrefixes) {
                if (name.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Skip anything that lives inside the runtime directory
            try {
                var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && !string.IsNullOrEmpty(runtimeDir)
                    && loc.StartsWith(runtimeDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            } catch { }

            return true;
        }

        /// <summary>
        /// Returns discoverable types and their public static methods from loaded assemblies
        /// so the UI can offer reflection-based invocation targets.
        /// </summary>
        private object ApiEndpointsReflect(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var query = "";
            if (req.QueryString != null) {
                var qs = System.Web.HttpUtility.ParseQueryString(req.QueryString);
                query = qs["q"] ?? "";
            }

            var sb = new StringBuilder();
            sb.Append("{\"types\":[");
            bool firstType = true;
            int count = 0;
            const int limit = 500;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .OrderByDescending(IsApplicationAssembly)
                .ThenBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase)) {
                if (count >= limit) break;

                foreach (var type in GetLoadableTypes(assembly)) {
                    if (count >= limit) break;

                    MethodInfo[] methods = Array.Empty<MethodInfo>();
                    PropertyInfo[] properties = Array.Empty<PropertyInfo>();
                    FieldInfo[] fields = Array.Empty<FieldInfo>();
                    try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).ToArray(); } catch { }
                    try { properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); } catch { }
                    try { fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly); } catch { }
                    if (methods.Length == 0 && properties.Length == 0 && fields.Length == 0) continue;

                    var fullName = type.FullName ?? type.Name;
                    if (!string.IsNullOrEmpty(query)
                        && fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                        && !methods.Any(m => m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        && !properties.Any(p => p.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                        && !fields.Any(f => f.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) {
                        continue;
                    }

                    if (!firstType) sb.Append(",");
                    firstType = false;
                    sb.Append("{\"type\":\"").Append(EscapeJson(fullName))
                        .Append("\",\"assembly\":\"").Append(EscapeJson(assembly.GetName().Name))
                        .Append("\",\"methods\":[");

                    bool firstMethod = true;
                    foreach (var method in methods) {
                        if (!firstMethod) sb.Append(",");
                        firstMethod = false;
                        sb.Append("{\"name\":\"").Append(EscapeJson(method.Name))
                            .Append("\",\"returnType\":\"").Append(EscapeJson(TypeToReflectionName(method.ReturnType)))
                            .Append("\",\"fullReturnType\":\"").Append(EscapeJson(method.ReturnType.FullName ?? method.ReturnType.Name))
                            .Append("\",\"isStatic\":").Append(method.IsStatic ? "true" : "false")
                            .Append(",\"params\":[");

                        bool firstParam = true;
                        foreach (var parameter in method.GetParameters()) {
                            if (!firstParam) sb.Append(",");
                            firstParam = false;
                            var parameterType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                            sb.Append("{\"name\":\"").Append(EscapeJson(parameter.Name))
                                .Append("\",\"type\":\"").Append(EscapeJson(TypeToReflectionName(parameter.ParameterType)))
                                .Append("\",\"fullType\":\"").Append(EscapeJson(parameter.ParameterType.FullName ?? parameter.ParameterType.Name))
                                .Append("\",\"optional\":").Append(parameter.HasDefaultValue ? "true" : "false");
                            if (parameter.HasDefaultValue && parameter.DefaultValue != null)
                                sb.Append(",\"defaultValue\":\"").Append(EscapeJson(parameter.DefaultValue.ToString())).Append("\"");
                            if (parameterType.IsEnum) {
                                sb.Append(",\"isEnum\":true,\"enumValues\":[");
                                var enumNames = Enum.GetNames(parameterType);
                                for (int i = 0; i < enumNames.Length; i++) {
                                    if (i > 0) sb.Append(",");
                                    sb.Append("\"").Append(EscapeJson(enumNames[i])).Append("\"");
                                }
                                sb.Append("]");
                            }
                            sb.Append("}");
                        }
                        sb.Append("]}");
                    }

                    sb.Append("],\"properties\":[");
                    bool firstProperty = true;
                    foreach (var property in properties) {
                        if (!firstProperty) sb.Append(",");
                        firstProperty = false;
                        var access = property.GetGetMethod() ?? property.GetSetMethod();
                        sb.Append("{\"name\":\"").Append(EscapeJson(property.Name))
                            .Append("\",\"type\":\"").Append(EscapeJson(TypeToReflectionName(property.PropertyType)))
                            .Append("\",\"fullType\":\"").Append(EscapeJson(property.PropertyType.FullName ?? property.PropertyType.Name))
                            .Append("\",\"canRead\":").Append(property.CanRead ? "true" : "false")
                            .Append(",\"canWrite\":").Append(property.CanWrite ? "true" : "false")
                            .Append(",\"isStatic\":").Append(access != null && access.IsStatic ? "true" : "false")
                            .Append("}");
                    }

                    sb.Append("],\"fields\":[");
                    bool firstField = true;
                    foreach (var field in fields) {
                        if (!firstField) sb.Append(",");
                        firstField = false;
                        sb.Append("{\"name\":\"").Append(EscapeJson(field.Name))
                            .Append("\",\"type\":\"").Append(EscapeJson(TypeToReflectionName(field.FieldType)))
                            .Append("\",\"fullType\":\"").Append(EscapeJson(field.FieldType.FullName ?? field.FieldType.Name))
                            .Append("\",\"isStatic\":").Append(field.IsStatic ? "true" : "false")
                            .Append("}");
                    }

                    sb.Append("]}");
                    count++;
                }
            }

            sb.Append("]}");
            return sb.ToString();
        }

        private static string TypeToReflectionName(Type type) {
            if (type == null) return "";
            if (!type.IsGenericType) return type.Name;
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeToReflectionName)) + ">";
        }

        private object ApiEventsReflect(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var query = "";
            if (req.QueryString != null) {
                var qs = System.Web.HttpUtility.ParseQueryString(req.QueryString);
                query = qs["q"] ?? "";
            }

            var sb = new StringBuilder();
            sb.Append("{\"types\":[");
            bool first = true;
            int count = 0;
            const int limit = 200;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                if (count >= limit) break;
                if (!IsApplicationAssembly(asm)) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types) {
                    if (count >= limit) break;
                    if (t.IsAbstract && t.IsSealed) { /* static class – always include */ }
                    else if (!t.IsPublic) continue;

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
                    if (methods.Length == 0) continue;

                    var fullName = t.FullName ?? t.Name;
                    if (!string.IsNullOrEmpty(query) && fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                        && !methods.Any(m => m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"type\":\"").Append(EscapeJson(fullName)).Append("\",\"methods\":[");
                    bool mFirst = true;
                    foreach (var m in methods) {
                        if (!mFirst) sb.Append(",");
                        mFirst = false;
                        sb.Append("{\"name\":\"").Append(EscapeJson(m.Name));
                        sb.Append("\",\"returnType\":\"").Append(EscapeJson(m.ReturnType.Name));
                        sb.Append("\",\"params\":[");
                        bool pFirst = true;
                        foreach (var p in m.GetParameters()) {
                            if (!pFirst) sb.Append(",");
                            pFirst = false;
                            sb.Append("{\"name\":\"").Append(EscapeJson(p.Name));
                            sb.Append("\",\"type\":\"").Append(EscapeJson(p.ParameterType.Name));
                            sb.Append("\",\"fullType\":\"").Append(EscapeJson(p.ParameterType.FullName ?? p.ParameterType.Name));
                            sb.Append("\",\"optional\":").Append(p.HasDefaultValue ? "true" : "false");
                            if (p.HasDefaultValue && p.DefaultValue != null) {
                                sb.Append(",\"defaultValue\":\"").Append(EscapeJson(p.DefaultValue.ToString())).Append("\"");
                            }
                            // Detect if param type is an enum and include values
                            var pType = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                            if (pType.IsEnum) {
                                sb.Append(",\"isEnum\":true,\"enumValues\":[");
                                var names = Enum.GetNames(pType);
                                for (int ei = 0; ei < names.Length; ei++) {
                                    if (ei > 0) sb.Append(",");
                                    sb.Append("\"").Append(EscapeJson(names[ei])).Append("\"");
                                }
                                sb.Append("]");
                            }
                            // Detect if param type is an application type and include provider methods
                            if (!pType.IsEnum && !pType.IsPrimitive && pType != typeof(string) && pType != typeof(decimal)
                                && pType != typeof(DateTime) && pType != typeof(Guid) && pType != typeof(char)
                                && pType != typeof(Uri) && pType != typeof(object)
                                && !pType.IsArray && !(pType.IsGenericType && pType.GetGenericTypeDefinition() == typeof(Nullable<>))) {
                                var pTypeAsm = pType.Assembly;
                                if (IsApplicationAssembly(pTypeAsm)) {
                                    sb.Append(",\"isAppType\":true,\"appMethods\":[");
                                    bool amFirst = true;
                                    // Find static methods across all app assemblies that return this type or collections of it
                                    foreach (var provAsm in AppDomain.CurrentDomain.GetAssemblies()) {
                                        if (!IsApplicationAssembly(provAsm)) continue;
                                        Type[] provTypes;
                                        try { provTypes = provAsm.GetTypes(); } catch { continue; }
                                        foreach (var pt in provTypes) {
                                            if (!pt.IsPublic && !(pt.IsAbstract && pt.IsSealed)) continue;
                                            MethodInfo[] provMethods;
                                            try { provMethods = pt.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
                                            foreach (var pm in provMethods) {
                                                var retType = pm.ReturnType;
                                                if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(retType) && retType.IsGenericType)
                                                    retType = retType.GetGenericArguments()[0];
                                                bool match = retType == pType
                                                    || (retType.IsArray && retType.GetElementType() == pType)
                                                    || (retType.IsGenericType && retType.GetGenericArguments().Length == 1 && retType.GetGenericArguments()[0] == pType);
                                                if (!match) continue;
                                                if (!amFirst) sb.Append(",");
                                                amFirst = false;
                                                sb.Append("{\"type\":\"").Append(EscapeJson(pt.FullName ?? pt.Name));
                                                sb.Append("\",\"method\":\"").Append(EscapeJson(pm.Name));
                                                sb.Append("\",\"returnType\":\"").Append(EscapeJson(pm.ReturnType.Name));
                                                sb.Append("\",\"params\":[");
                                                bool ppFirst = true;
                                                foreach (var pp in pm.GetParameters()) {
                                                    if (!ppFirst) sb.Append(",");
                                                    ppFirst = false;
                                                    sb.Append("{\"name\":\"").Append(EscapeJson(pp.Name));
                                                    sb.Append("\",\"type\":\"").Append(EscapeJson(pp.ParameterType.Name));
                                                    sb.Append("\",\"optional\":").Append(pp.HasDefaultValue ? "true" : "false");
                                                    sb.Append("}");
                                                }
                                                sb.Append("]}");
                                            }
                                        }
                                    }
                                    sb.Append("]");
                                }
                            }
                            sb.Append("}");
                        }
                        sb.Append("]}");
                    }
                    sb.Append("]}");
                    count++;
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static List<string> ParseSimpleJsonArray(string json) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            json = json.Trim();
            if (!json.StartsWith("[")) return list;
            int i = 1;
            while (i < json.Length) {
                while (i < json.Length && (json[i] == ' ' || json[i] == ',')) i++;
                if (i >= json.Length || json[i] == ']') break;
                if (json[i] == '"') {
                    i++;
                    var sb2 = new StringBuilder();
                    while (i < json.Length && json[i] != '"') {
                        if (json[i] == '\\' && i + 1 < json.Length) { i++; sb2.Append(json[i]); }
                        else sb2.Append(json[i]);
                        i++;
                    }
                    i++; // closing quote
                    list.Add(sb2.ToString());
                } else {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != ']') i++;
                    list.Add(json.Substring(start, i - start).Trim());
                }
            }
            return list;
        }

        #endregion

        #region JSON Helpers

        private static string ExtractJsonProperty(string json, string key) {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try {
                using (var document = JsonDocument.Parse(json)) {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return null;
                    if (!document.RootElement.TryGetProperty(key, out var value))
                        return null;

                    switch (value.ValueKind) {
                        case JsonValueKind.String:
                            return value.GetString();
                        case JsonValueKind.True:
                            return "true";
                        case JsonValueKind.False:
                            return "false";
                        case JsonValueKind.Null:
                        case JsonValueKind.Undefined:
                            return null;
                        default:
                            return value.GetRawText();
                    }
                }
            } catch {
                return ExtractJsonString(json, key);
            }
        }

        private static string ExtractJsonString(string json, string key) {
            if (string.IsNullOrEmpty(json)) return null;
            var search = "\"" + key + "\"";
            int idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += search.Length;
            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++; // skip opening quote
            var sb = new StringBuilder();
            while (idx < json.Length) {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length) {
                    idx++;
                    char next = json[idx];
                    switch (next) {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(next); break;
                    }
                } else if (c == '"') {
                    break;
                } else {
                    sb.Append(c);
                }
                idx++;
            }
            return sb.ToString();
        }

        private static bool ExtractJsonBool(string json, string key) {
            var value = ExtractJsonProperty(json, key);
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string EscapeJson(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s) {
                switch (c) {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Page Template Loading

        /// <summary>
        /// Loads an HTML page template from embedded memory first, with a disk fallback for
        /// development overrides.
        /// </summary>
        private static string LoadPageTemplate(string filename) {
            if (SocketJack.HtmlPageResources.TryGetHtml(filename, out var embeddedTemplate))
                return embeddedTemplate;

            var filePath = Path.Combine(PagesFolder, filename);
            if (!File.Exists(filePath)) return "";

            var currentHash = ComputeFileHash(filePath);
            if (_pageHashCache.TryGetValue(filename, out var cachedHash) && cachedHash == currentHash) {
                if (_pageTemplateCache.TryGetValue(filename, out var cached)) return cached;
            }

            var content = File.ReadAllText(filePath);
            _pageTemplateCache[filename] = content;
            _pageHashCache[filename] = currentHash;
            return content;
        }

        private static string ComputeFileHash(string filePath) {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash);
            }
        }

        /// <summary>
        /// Obsolete compatibility shim. HTML pages are now served from embedded memory.
        /// </summary>
        private static void LoadHtmlFromBinary(string filename) {
        }

        #endregion

        #region HTML Generation

        private string LoginPageHtml(HttpRequest req) {
            var template = LoadPageTemplate("SqlLogin.html");
            if (!string.IsNullOrEmpty(template)) {
                return template
                    .Replace("$BasePath", BasePath.TrimEnd('/'))
                    .Replace("$SaBootstrapRequired", IsSaBootstrapRequired() ? "true" : "false")
                    .Replace("$SaBootstrapLocal", IsLocalhostRequest(req) ? "true" : "false");
            }
            return "";
        }

        private string PanelPageHtml(SqlAdminSession session) {
            var template = LoadPageTemplate("SqlPanel.html");
            if (!string.IsNullOrEmpty(template)) {
                var basePath = BasePath.TrimEnd('/');
                return template
                    .Replace("$BasePath", basePath)
                    .Replace("$Username", EscapeHtml(session.Username))
                    .Replace("$SqlAdminSessionId", EscapeJson(session.SessionId ?? ""))
                    .Replace("$SqlAdminCsrfToken", EscapeJson(session.CsrfToken ?? ""))
                    .Replace("$CurrentDatabase", EscapeJson(session.CurrentDatabase ?? "db"));
            }
            return "";
        }

        private static string EscapeHtml(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion
    }
}
