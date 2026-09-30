using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using heirowLLM;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetHttpClient = System.Net.Http.HttpClient;

namespace SocketJack.Tests
{
    [TestClass]
    public sealed class WorkstationSocketJackAuthApprovalTests
    {
        [TestMethod]
        public async Task SocketJackAccount_RequiresWorkstationApprovalWhenOpenRegistrationIsUnchecked()
        {
            int authPort = NextPort();
            int modelPort = NextPort();
            int proxyPort = NextPort();
            int chatPort = NextPort();
            string dataRoot = Path.Combine(Path.GetTempPath(), "SocketJack.Tests", "workstation-auth-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            try
            {
                using var authServer = CreateSocketJackAuthServer(authPort, "alice", "socketjack-token");
                Assert.IsTrue(authServer.Listen(), "Fake SocketJack auth server should start.");

                using var workstation = new HeirowLlm("127.0.0.1", modelPort, proxyPort, chatPort, dataRoot)
                {
                    SocketJackAuthServerUrl = "http://127.0.0.1:" + authPort,
                    UseSocketJackMasterAuth = true,
                    StoreLocalWebAuthAccounts = false,
                    AllowOpenRegistration = false
                };
                Assert.IsTrue(workstation.ChatServer.Listen(), "Workstation chat server should start.");

                using var client = new NetHttpClient { BaseAddress = new Uri("http://127.0.0.1:" + chatPort), Timeout = TimeSpan.FromSeconds(15) };

                using JsonDocument unapprovedSession = await GetSession(client, "socketjack-token");
                Assert.IsFalse(unapprovedSession.RootElement.GetProperty("authenticated").GetBoolean(), "A SocketJack token must not bypass the Workstation approval gate.");
                Assert.AreEqual(1, workstation.GetPendingWebAuthRegistrationRequests().Count);

                using HttpResponseMessage deniedLogin = await Login(client, "alice", "password123");
                Assert.AreEqual(HttpStatusCode.Forbidden, deniedLogin.StatusCode);
                using JsonDocument deniedBody = JsonDocument.Parse(await deniedLogin.Content.ReadAsStringAsync());
                Assert.IsTrue(deniedBody.RootElement.GetProperty("pending").GetBoolean());
                StringAssert.Contains(deniedBody.RootElement.GetProperty("error").GetString(), "must approve");
                Assert.AreEqual(1, workstation.GetPendingWebAuthRegistrationRequests().Count, "Repeated sign-in should reuse the pending approval request.");

                WebAuthRegistrationRequestSnapshot pending = workstation.GetPendingWebAuthRegistrationRequests().Single();
                workstation.ApproveWebAuthRegistrationRequest(pending.Id);

                using HttpResponseMessage approvedLogin = await Login(client, "alice", "password123");
                Assert.AreEqual(HttpStatusCode.OK, approvedLogin.StatusCode);
                using JsonDocument approvedBody = JsonDocument.Parse(await approvedLogin.Content.ReadAsStringAsync());
                Assert.IsTrue(approvedBody.RootElement.GetProperty("ok").GetBoolean());
                Assert.AreEqual("alice", approvedBody.RootElement.GetProperty("username").GetString());

                using JsonDocument approvedSession = await GetSession(client, "socketjack-token");
                Assert.IsTrue(approvedSession.RootElement.GetProperty("authenticated").GetBoolean());
                Assert.AreEqual(0, workstation.GetPendingWebAuthRegistrationRequests().Count);
                Assert.IsTrue(workstation.GetWebAuthUserDiagnostics().Single(user => user.UserName == "alice").Enabled);
            }
            finally
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            }
        }

        [TestMethod]
        public async Task SocketJackApprovalRequest_CannotBeConvertedIntoALocalPasswordAccount()
        {
            int authPort = NextPort();
            int modelPort = NextPort();
            int proxyPort = NextPort();
            int chatPort = NextPort();
            string dataRoot = Path.Combine(Path.GetTempPath(), "SocketJack.Tests", "workstation-auth-source-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dataRoot);

            try
            {
                using var authServer = CreateSocketJackAuthServer(authPort, "alice", "socketjack-token", "password123");
                Assert.IsTrue(authServer.Listen());
                using var workstation = new HeirowLlm("127.0.0.1", modelPort, proxyPort, chatPort, dataRoot)
                {
                    SocketJackAuthServerUrl = "http://127.0.0.1:" + authPort,
                    UseSocketJackMasterAuth = true,
                    StoreLocalWebAuthAccounts = true,
                    AllowOpenRegistration = false
                };
                Assert.IsTrue(workstation.ChatServer.Listen());
                using var client = new NetHttpClient { BaseAddress = new Uri("http://127.0.0.1:" + chatPort), Timeout = TimeSpan.FromSeconds(15) };

                using HttpResponseMessage pendingLogin = await Login(client, "alice", "password123");
                Assert.AreEqual(HttpStatusCode.Forbidden, pendingLogin.StatusCode);

                using HttpResponseMessage localRequest = await client.PostAsync("/api/web-auth/registration-request", new StringContent(
                    JsonSerializer.Serialize(new { username = "alice", password = "attacker-password" }), Encoding.UTF8, "application/json"));
                Assert.AreEqual(HttpStatusCode.OK, localRequest.StatusCode);
                Assert.AreEqual(2, workstation.GetPendingWebAuthRegistrationRequests().Count, "SocketJack and local-password requests must remain distinct.");

                WebAuthRegistrationRequestSnapshot socketJackRequest = workstation.GetPendingWebAuthRegistrationRequests()
                    .Single(request => request.Note.StartsWith("SocketJack.com account verified", StringComparison.Ordinal));
                workstation.ApproveWebAuthRegistrationRequest(socketJackRequest.Id);

                using HttpResponseMessage attackerLogin = await Login(client, "alice", "attacker-password");
                Assert.AreEqual(HttpStatusCode.Unauthorized, attackerLogin.StatusCode, "Approving a SocketJack account must not install a locally supplied password.");
                using HttpResponseMessage realLogin = await Login(client, "alice", "password123");
                Assert.AreEqual(HttpStatusCode.OK, realLogin.StatusCode);
            }
            finally
            {
                if (Directory.Exists(dataRoot)) Directory.Delete(dataRoot, recursive: true);
            }
        }

        private static HttpServer CreateSocketJackAuthServer(int port, string username, string token, string password = "password123")
        {
            var server = new HttpServer(port);
            server.Map("POST", "/api/web-auth/login", (_, request, _) =>
            {
                request.Context.Response.ContentType = "application/json; charset=utf-8";
                using JsonDocument body = JsonDocument.Parse(request.Body ?? "{}");
                string suppliedUser = body.RootElement.TryGetProperty("username", out JsonElement userElement) ? userElement.GetString() : "";
                string suppliedPassword = body.RootElement.TryGetProperty("password", out JsonElement passwordElement) ? passwordElement.GetString() : "";
                if (!string.Equals(suppliedUser, username, StringComparison.OrdinalIgnoreCase) || suppliedPassword != password)
                {
                    request.Context.Response.StatusCodeNumber = 401;
                    request.Context.Response.ReasonPhrase = "Unauthorized";
                    return JsonSerializer.Serialize(new { ok = false, error = "Invalid username or password." });
                }
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    accessToken = token,
                    expiresUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
                });
            });
            server.Map("POST", "/api/socketjack/oauth/introspect", (_, request, _) =>
            {
                request.Context.Response.ContentType = "application/json; charset=utf-8";
                return JsonSerializer.Serialize(new
                {
                    active = true,
                    username,
                    accountType = "socketjack",
                    expiresUtc = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
                    ownsServer = false,
                    isAdministrator = false
                });
            });
            return server;
        }

        private static async Task<HttpResponseMessage> Login(NetHttpClient client, string username, string password)
        {
            return await client.PostAsync("/api/web-auth/login", new StringContent(
                JsonSerializer.Serialize(new { username, password, remember = true }), Encoding.UTF8, "application/json"));
        }

        private static async Task<JsonDocument> GetSession(NetHttpClient client, string token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/web-auth/session");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        private static int NextPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
