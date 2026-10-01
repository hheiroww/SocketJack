using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using SocketJack.Net.Database;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using NetHttpClient = System.Net.Http.HttpClient;

namespace SocketJack.Tests {

    [TestClass]
    public sealed class AdminSuiteTests {
        private static int NextPort() {
            var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static MutableTcpServer CreateAdminServer(int port, string tempRoot) {
            var server = new MutableTcpServer(new NetworkOptions {
                UsePeerToPeer = false,
                UseCompression = false,
                Logging = false
            }, port, "AdminSuiteTests");
            var ds = server.GetOrCreateDataServer();
            ds.DataPath = Path.Combine(tempRoot, "dataserver");
            ds.EnablePayloadEncryption = false;
            ds.AutoSave = false;
            ds.Databases.Clear();
            ds.Databases.TryAdd("master", new Database("master"));
            ds.Databases.TryAdd("db", new Database("db"));
            ds.Users.Clear();
            ds.Users.TryAdd("sa", "");
            server.AdminSuiteEnabled = true;
            return server;
        }

        private static NetHttpClient CreateClient(int port, CookieContainer cookies = null) {
            var handler = new HttpClientHandler {
                CookieContainer = cookies ?? new CookieContainer(),
                UseCookies = true
            };
            var client = new NetHttpClient(handler) {
                BaseAddress = new Uri("http://127.0.0.1:" + port)
            };
            return client;
        }

        private static async Task LoginAsync(NetHttpClient client) {
            var setup = await client.PostAsync("/sql/api/bootstrap/sa", Json("{\"username\":\"sa\",\"password\":\"password123\",\"confirmPassword\":\"password123\"}"));
            Assert.IsTrue(setup.IsSuccessStatusCode, await setup.Content.ReadAsStringAsync());
            var login = await client.PostAsync("/sql/login", Json("{\"username\":\"sa\",\"password\":\"password123\"}"));
            Assert.IsTrue(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        }

        [TestMethod]
        public async Task AdminSuite_RegistersAdminAndSqlNavigation() {
            string temp = NewTempRoot();
            int port = NextPort();
            using var server = CreateAdminServer(port, temp);
            Assert.IsTrue(server.Listen());

            using var client = CreateClient(port);
            string admin = await client.GetStringAsync("/Admin");
            Assert.IsTrue(admin.Contains("href=\"/sql\""), "Admin shell should link to /sql.");

            string sqlLogin = await client.GetStringAsync("/sql");
            Assert.IsTrue(sqlLogin.Contains("href=\"/Admin\""), "SQL login should link back to /Admin.");

            await LoginAsync(client);
            string sqlPanel = await client.GetStringAsync("/sql");
            Assert.IsTrue(sqlPanel.Contains("Admin Suite"), "SQL panel should link back to /Admin.");

            server.StopListening();
            TryDelete(temp);
        }

        [TestMethod]
        public async Task AdminSuite_ServesSqlBackedPagesWithInjectedContext() {
            string temp = NewTempRoot();
            int port = NextPort();
            using var server = CreateAdminServer(port, temp);
            Assert.IsTrue(server.Listen());
            using var client = CreateClient(port);
            await LoginAsync(client);

            var save = await client.PostAsync("/Admin/api/pages/save", Json("{\"route\":\"hello.html\",\"title\":\"Hello\",\"storageMode\":\"sql\",\"html\":\"<!doctype html><html><head><title>$PageRoute</title></head><body>$Username $CurrentDatabase</body></html>\"}"));
            Assert.IsTrue(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());

            var response = await client.GetAsync("/Admin/pages/hello.html");
            string body = await response.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Content.Headers.ContentType.MediaType.Contains("text/html"));
            Assert.IsTrue(body.Contains("sa db"));
            Assert.IsTrue(body.Contains("window.SocketJackContext"));

            server.StopListening();
            TryDelete(temp);
        }

        [TestMethod]
        public async Task AdminSuite_ServesDiskPagesAndAssetsWithoutTraversal() {
            string temp = NewTempRoot();
            string site = Path.Combine(temp, "site");
            Directory.CreateDirectory(site);
            File.WriteAllText(Path.Combine(site, "disk.html"), "<!doctype html><html><body>disk page</body></html>");
            File.WriteAllText(Path.Combine(temp, "outside.html"), "secret outside");

            int port = NextPort();
            using var server = CreateAdminServer(port, temp);
            Assert.IsTrue(server.Listen());
            using var client = CreateClient(port);
            await LoginAsync(client);

            string bodyJson = "{\"route\":\"disk.html\",\"title\":\"Disk\",\"storageMode\":\"disk\",\"directoryPath\":\"" + EscapeJson(site) + "\"}";
            var save = await client.PostAsync("/Admin/api/pages/save", Json(bodyJson));
            Assert.IsTrue(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());

            var disk = await client.GetAsync("/Admin/pages/disk.html");
            string diskBody = await disk.Content.ReadAsStringAsync();
            Assert.AreEqual(HttpStatusCode.OK, disk.StatusCode);
            Assert.IsTrue(diskBody.Contains("disk page"));

            var assetSave = await client.PostAsync("/Admin/api/assets/save", Json("{\"name\":\"site.js\",\"contentType\":\"application/javascript\",\"storageMode\":\"sql\",\"content\":\"window.adminAsset=1;\"}"));
            Assert.IsTrue(assetSave.IsSuccessStatusCode, await assetSave.Content.ReadAsStringAsync());
            var asset = await client.GetAsync("/Admin/pages/site.js");
            Assert.AreEqual(HttpStatusCode.OK, asset.StatusCode);
            Assert.IsTrue(asset.Content.Headers.ContentType.MediaType.Contains("application/javascript"));

            var traversal = await client.GetAsync("/Admin/pages/%2e%2e/outside.html");
            string traversalBody = await traversal.Content.ReadAsStringAsync();
            Assert.IsFalse(traversalBody.Contains("secret outside"), "Document root traversal must not expose files outside the configured root.");

            server.StopListening();
            TryDelete(temp);
        }

        [TestMethod]
        public async Task AdminSuite_GeneratedCrudEnforcesAuthValidationAndAudits() {
            string temp = NewTempRoot();
            int port = NextPort();
            using var server = CreateAdminServer(port, temp);
            var ds = server.GetOrCreateDataServer();
            var db = ds.Databases["db"];
            var people = new Table("People");
            people.Columns.Add(new Column("Id", typeof(string)));
            people.Columns.Add(new Column("Name", typeof(string)));
            db.Tables.TryAdd("People", people);
            Assert.IsTrue(server.Listen());

            using var client = CreateClient(port);
            using var anonymous = CreateClient(port);
            await LoginAsync(client);

            string schema = "{\"type\":\"object\",\"required\":[\"Id\",\"Name\"],\"properties\":{\"Id\":{\"type\":\"string\"},\"Name\":{\"type\":\"string\"}}}";
            string saveBody = "{\"name\":\"People CRUD\",\"route\":\"people\",\"database\":\"db\",\"table\":\"People\",\"keyColumn\":\"Id\",\"scopes\":\"sql:read,sql:write\",\"inputSchema\":" + JsonSerializerLiteral(schema) + ",\"enabled\":true}";
            var save = await client.PostAsync("/Admin/api/crud/save", Json(saveBody));
            Assert.IsTrue(save.IsSuccessStatusCode, await save.Content.ReadAsStringAsync());

            var clientJs = await client.GetStringAsync("/Admin/api/crud/client/people.js");
            Assert.IsTrue(clientJs.Contains("list:function"));
            Assert.IsTrue(clientJs.Contains("create:function"));
            Assert.IsTrue(clientJs.Contains("\"delete\":function"));
            Assert.IsFalse(clientJs.IndexOf("secret", StringComparison.OrdinalIgnoreCase) >= 0);

            var blocked = await anonymous.PostAsync("/Admin/api/crud/run/people/create", Json("{\"Id\":\"1\",\"Name\":\"Ada\"}"));
            Assert.AreEqual(HttpStatusCode.Unauthorized, blocked.StatusCode);

            var missing = await client.PostAsync("/Admin/api/crud/run/people/create", Json("{\"Id\":\"1\"}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);

            var unknown = await client.PostAsync("/Admin/api/crud/run/people/create", Json("{\"Id\":\"1\",\"Name\":\"Ada\",\"Role\":\"admin\"}"));
            Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);

            var create = await client.PostAsync("/Admin/api/crud/run/people/create", Json("{\"Id\":\"1\",\"Name\":\"Ada\"}"));
            Assert.IsTrue(create.IsSuccessStatusCode, await create.Content.ReadAsStringAsync());

            string list = await client.GetStringAsync("/Admin/api/crud/run/people/list");
            Assert.IsTrue(list.Contains("\"Name\":\"Ada\""));

            var update = await client.PostAsync("/Admin/api/crud/run/people/update", Json("{\"Id\":\"1\",\"Name\":\"Grace\"}"));
            Assert.IsTrue(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());

            string get = await client.GetStringAsync("/Admin/api/crud/run/people/get?Id=1");
            Assert.IsTrue(get.Contains("\"Name\":\"Grace\""));

            var delete = await client.PostAsync("/Admin/api/crud/run/people/delete", Json("{\"Id\":\"1\"}"));
            Assert.IsTrue(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync());
            Assert.AreEqual(0, people.Rows.Count);

            var audit = ds.Databases["SocketJack"].Tables["SocketJackAdminAudit"];
            Assert.IsTrue(audit.Rows.Any(row => row.Length > 2 && (row[2]?.ToString() ?? "") == "crud.create"));

            server.StopListening();
            TryDelete(temp);
        }

        private static StringContent Json(string value) {
            return new StringContent(value, Encoding.UTF8, "application/json");
        }

        private static string JsonSerializerLiteral(string value) {
            return System.Text.Json.JsonSerializer.Serialize(value);
        }

        private static string EscapeJson(string value) {
            return (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string NewTempRoot() {
            string path = Path.Combine(Path.GetTempPath(), "SocketJackAdminSuiteTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void TryDelete(string path) {
            try {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            } catch {
            }
        }
    }
}
