using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Net;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NetHttpClient = System.Net.Http.HttpClient;

namespace SocketJack.Tests
{
    [TestClass]
    public sealed class SockJackDmlWorkflowEndpointTests
    {
        [TestMethod]
        public async Task WorkflowToolEndpoints_RoundTripPlanExecuteStatusControlFindAndEvidence()
        {
            int modelPort = NextPort();
            int proxyPort = NextPort();
            int chatPort = NextPort();
            string sessionId = "endpoint-" + Guid.NewGuid().ToString("N");
            string tempRoot = Path.Combine(Path.GetTempPath(), "SocketJack.Tests", sessionId);
            Directory.CreateDirectory(tempRoot);
            string progressPath = Path.Combine(tempRoot, "SocketJack_SockJackDmlWorkflowExpansionEndpoint_Progress.md");
            using var proxy = new HeirowLlm("127.0.0.1", modelPort, proxyPort, chatPort);
            HttpServer chatServer = proxy.ChatServer;
            Assert.IsTrue(chatServer.Listen(), "Chat server should start.");

            using var client = new NetHttpClient { BaseAddress = new Uri("http://127.0.0.1:" + chatPort), Timeout = TimeSpan.FromSeconds(15) };

            JsonDocument plan = await PostJson(client, "/api/sockjackdml/tools/plan/create", new
            {
                sessionId,
                projectOrSessionName = "SocketJack",
                featureName = "SockJackDmlWorkflowExpansionEndpoint",
                goal = "Exercise the SockJackDml workflow endpoint surface.",
                requirements = new[] { "Expose workflow status", "Persist progress state" },
                finalize = true
            });
            Assert.IsTrue(GetBool(plan.RootElement, "ok"));
            string planId = GetString(plan.RootElement, "planId");
            Assert.IsFalse(string.IsNullOrWhiteSpace(planId));

            JsonDocument progress = await PostJson(client, "/api/sockjackdml/tools/progress/document", new
            {
                sessionId,
                planId,
                projectOrSessionName = "SocketJack",
                featureName = "SockJackDmlWorkflowExpansionEndpoint",
                progressPath,
                overallPercent = 40,
                currentStatus = "Endpoint test tracker created.",
                nextStep = "Preview execution packet."
            });
            Assert.IsTrue(GetBool(progress.RootElement, "ok"));
            string progressId = GetString(progress.RootElement, "progressId");
            Assert.IsFalse(string.IsNullOrWhiteSpace(progressId));

            JsonDocument execution = await PostJson(client, "/api/sockjackdml/tools/plan/execute", new
            {
                sessionId,
                planId,
                projectOrSessionName = "SocketJack",
                featureName = "SockJackDmlWorkflowExpansionEndpoint",
                progressPath,
                executionMode = "preview",
                actionsJson = "[{\"id\":\"step_1\",\"type\":\"progress\",\"summary\":\"Endpoint preview action\"}]"
            });
            Assert.IsTrue(GetBool(execution.RootElement, "ok"));
            string executionId = GetString(execution.RootElement, "executionId");
            Assert.IsFalse(string.IsNullOrWhiteSpace(executionId));
            Assert.AreEqual("preview", GetString(execution.RootElement, "status"));

            JsonDocument status = await GetJson(client, "/api/sockjackdml/tools/workflow/status?sessionId=" + Uri.EscapeDataString(sessionId) + "&take=10");
            Assert.IsTrue(GetBool(status.RootElement, "ok"));
            Assert.IsTrue(GetInt(status.RootElement, "planCount") >= 1);
            Assert.IsTrue(GetInt(status.RootElement, "progressCount") >= 1);
            Assert.IsTrue(GetInt(status.RootElement, "executionCount") >= 1);

            JsonDocument find = await PostJson(client, "/api/sockjackdml/tools/progress/find", new
            {
                sessionId,
                planId,
                projectOrSessionName = "SocketJack",
                featureName = "SockJackDmlWorkflowExpansionEndpoint",
                progressPath
            });
            Assert.IsTrue(GetBool(find.RootElement, "ok"));

            JsonDocument control = await PostJson(client, "/api/sockjackdml/tools/execution/control", new
            {
                sessionId,
                executionId,
                action = "pause",
                reason = "Endpoint control test."
            });
            Assert.IsTrue(GetBool(control.RootElement, "ok"));
            Assert.AreEqual("paused", GetString(control.RootElement, "status"));

            JsonDocument evidence = await PostJson(client, "/api/sockjackdml/tools/evidence/link", new
            {
                sessionId,
                evidencePacketId = "packet_endpoint_test",
                planId,
                progressId,
                executionId,
                actionId = "step_1",
                sourceKind = "verification",
                sourceRef = "SockJackDmlWorkflowEndpointTests",
                summary = "Endpoint evidence link test."
            });
            Assert.IsTrue(GetBool(evidence.RootElement, "ok"));
        }

        [TestMethod]
        public async Task WorkflowToolEndpoints_RejectInvalidPayload()
        {
            int modelPort = NextPort();
            int proxyPort = NextPort();
            int chatPort = NextPort();
            using var proxy = new HeirowLlm("127.0.0.1", modelPort, proxyPort, chatPort);
            Assert.IsTrue(proxy.ChatServer.Listen(), "Chat server should start.");

            using var client = new NetHttpClient { BaseAddress = new Uri("http://127.0.0.1:" + chatPort), Timeout = TimeSpan.FromSeconds(15) };
            using var content = new StringContent("[]", Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("/api/sockjackdml/tools/execution/control", content);
            string body = await response.Content.ReadAsStringAsync();

            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            using JsonDocument document = JsonDocument.Parse(body);
            Assert.IsFalse(GetBool(document.RootElement, "ok", true));
        }

        [TestMethod]
        public async Task DocumentationRoute_ServesDocumentationHtml()
        {
            int modelPort = NextPort();
            int proxyPort = NextPort();
            int chatPort = NextPort();
            using var proxy = new HeirowLlm("127.0.0.1", modelPort, proxyPort, chatPort);
            Assert.IsTrue(proxy.ChatServer.Listen(), "Chat server should start.");

            using var client = new NetHttpClient { BaseAddress = new Uri("http://127.0.0.1:" + chatPort), Timeout = TimeSpan.FromSeconds(15) };

            string documentation = await GetText(client, "/Documentation");
            StringAssert.StartsWith(documentation.TrimStart(), "<!DOCTYPE html>");
            StringAssert.Contains(documentation, "<title>SocketJack Documentation</title>");
            StringAssert.Contains(documentation, "id=\"docs-data\"");
            StringAssert.Contains(documentation, "id=\"repo-data\"");
            StringAssert.Contains(documentation, "SocketJack Library");
            StringAssert.Contains(documentation, "heirowLLM Workstation");
            StringAssert.Contains(documentation, "/issues");

            string documentationSubpath = await GetText(client, "/Documentation/README.md?page=README.md");
            StringAssert.Contains(documentationSubpath, "<title>SocketJack Documentation</title>");

            foreach (string alias in new[] { "/Doc", "/Docs", "/ReadMe", "/Info", "/Help" })
            {
                string aliasHtml = await GetText(client, alias);
                StringAssert.Contains(aliasHtml, "<title>SocketJack Documentation</title>");
            }
        }

        [TestMethod]
        public void MagicMasterListIssuesSurface_IsWiredForPublicRouteApiAndJackOnlyAdmin()
        {
            string root = FindSocketJackProjectRoot();
            string program = File.ReadAllText(Path.Combine(root, "..", "SocketJack-MagicMasterList", "Program.cs"));

            StringAssert.Contains(program, "\"/issues\"");
            StringAssert.Contains(program, "\"/api/issues/sessions\"");
            StringAssert.Contains(program, "\"/api/issues/report\"");
            StringAssert.Contains(program, "\"/api/socketjack/admin/issues\"");
            StringAssert.Contains(program, "MaxIssueScreenshots = 10");
            StringAssert.Contains(program, "MaxIssueErrorLines = 10000");
            StringAssert.Contains(program, "Issue reports allow up to 10 screenshots.");
            StringAssert.Contains(program, "Issue reports allow up to 10,000 lines of exception/error text.");
            StringAssert.Contains(program, "/auto/api?mode=tools&origin=issues&minParamsB=2&model=claude");
            StringAssert.Contains(program, "IsJackAdministrator(admin)");
            StringAssert.Contains(program, "canViewIssues = IsJackAdministrator(admin)");
            StringAssert.Contains(program, "BuildIssueAssistantPrompt");
            StringAssert.Contains(program, "BuildAbsoluteWebsiteUrl(\"/Documentation\")");
        }

        [TestMethod]
        public void MagicMasterListAuthRoutes_ExposeLowercaseLoginAndRegistrationAliases()
        {
            string root = FindSocketJackProjectRoot();
            string program = File.ReadAllText(Path.Combine(root, "..", "SocketJack-MagicMasterList", "Program.cs"));
            string webChat = File.ReadAllText(Path.Combine(root, "..", "SocketJack.LlmCore", "html", "heirowLLMWebChat.html"));

            StringAssert.Contains(program, "return \"/login?returnUrl=\"");
            StringAssert.Contains(program, "ServeCanonicalWebsiteAuthPage(request, \"/login\")");
            StringAssert.Contains(program, "ServeCanonicalWebsiteAuthPage(request, \"/register\")");
            StringAssert.Contains(program, "\"/login\"");
            StringAssert.Contains(program, "\"/register\"");
            StringAssert.Contains(program, "\"/signup\"");
            StringAssert.Contains(program, "\"/auth/register\"");
            StringAssert.Contains(program, "const authRoute=/^\\/(?:login|register|signin|sign-in|signup|sign-up)\\/?$/i");
            StringAssert.Contains(webChat, "socketJackAuthUrl('/login?returnUrl='");
            StringAssert.Contains(webChat, "socketJackAuthUrl('/register?returnUrl='");
        }

        [TestMethod]
        public void AutoToolsRouting_ForcesSessionScopedAgentToolsAndWriteCreatesBeforeRemoteClone()
        {
            string root = FindSocketJackProjectRoot();
            string program = File.ReadAllText(Path.Combine(root, "..", "SocketJack-MagicMasterList", "Program.cs"));
            string proxy = File.ReadAllText(Path.Combine(root, "..", "SocketJack.LlmCore", "Proxy", "heirowLLM.cs"));

            StringAssert.Contains(program, "ApplyAutoApiServiceForMode(obj, mode);");
            StringAssert.Contains(program, "obj[\"service\"] = service;");
            StringAssert.Contains(program, "obj[\"serviceId\"] = service;");
            StringAssert.Contains(program, "Use the session-scoped Agent file tools");
            StringAssert.Contains(program, "if (mode.Equals(\"tools\", StringComparison.OrdinalIgnoreCase))");
            StringAssert.Contains(program, "return \"agent\";");

            int cloneBuilder = proxy.IndexOf("private bool TryBuildRemoteSessionFileCloneRequest", StringComparison.Ordinal);
            Assert.IsTrue(cloneBuilder >= 0, "Remote session clone builder should exist.");
            int writeCloneGuard = proxy.IndexOf("if (toolName.Equals(\"vs_write_file\", StringComparison.Ordinal))", cloneBuilder, StringComparison.Ordinal);
            Assert.IsTrue(writeCloneGuard >= 0, "vs_write_file should explicitly skip remote session clone materialization.");
            string guardBlock = proxy.Substring(writeCloneGuard, Math.Min(320, proxy.Length - writeCloneGuard));
            StringAssert.Contains(guardBlock, "return false;");
            StringAssert.Contains(guardBlock, "TryResolveAuthorizedExistingChatAgentFilePath");
        }

        [TestMethod]
        public void WorkflowGuiSurfaces_ContainWorkflowControlsAndEndpoints()
        {
            string root = FindSocketJackProjectRoot();
            string magic = File.ReadAllText(Path.Combine(root, "..", "SocketJack.LlmCore", "html", "SockJackDml.html"));
            string webChat = File.ReadAllText(Path.Combine(root, "..", "SocketJack.LlmCore", "html", "heirowLLMWebChat.html"));
            string wpfXaml = File.ReadAllText(Path.Combine(root, "..", "heirowLLM", "MainWindow.xaml"));
            string wpfCode = File.ReadAllText(Path.Combine(root, "..", "heirowLLM", "MainWindow.xaml.cs"));

            StringAssert.Contains(magic, "id=\"workflowView\"");
            StringAssert.Contains(magic, "/api/sockjackdml/tools/workflow/status");
            StringAssert.Contains(magic, "workflowControlForm");
            StringAssert.Contains(webChat, "magicWorkflowControls");
            StringAssert.Contains(webChat, "loadMagicWorkflowStatus");
            StringAssert.Contains(wpfXaml, "OpenMagicWorkflowButton");
            StringAssert.Contains(wpfCode, "sockjackdml_workflow_status");
            StringAssert.Contains(wpfCode, "SockJackDml tools: 7");
        }

        private static int NextPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<JsonDocument> GetJson(NetHttpClient client, string path)
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, body);
            return JsonDocument.Parse(body);
        }

        private static async Task<string> GetText(NetHttpClient client, string path)
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, body);
            return body;
        }

        private static async Task<JsonDocument> PostJson(NetHttpClient client, string path, object payload)
        {
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync(path, content);
            string body = await response.Content.ReadAsStringAsync();
            Assert.IsTrue(response.IsSuccessStatusCode, body);
            return JsonDocument.Parse(body);
        }

        private static string GetString(JsonElement element, string name)
        {
            return TryGetProperty(element, name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? ""
                : "";
        }

        private static int GetInt(JsonElement element, string name)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
                return 0;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            return 0;
        }

        private static bool GetBool(JsonElement element, string name, bool fallback = false)
        {
            if (!TryGetProperty(element, name, out JsonElement value))
                return fallback;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            return fallback;
        }

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static string FindSocketJackProjectRoot()
        {
            string directory = AppContext.BaseDirectory;
            while (!string.IsNullOrWhiteSpace(directory))
            {
                string candidate = Path.Combine(directory, "SocketJack.csproj");
                if (File.Exists(candidate))
                    return directory;
                string siblingCandidate = Path.Combine(directory, "SocketJack", "SocketJack.csproj");
                if (File.Exists(siblingCandidate))
                    return Path.Combine(directory, "SocketJack");
                directory = Directory.GetParent(directory)?.FullName;
            }

            throw new DirectoryNotFoundException("Could not find SocketJack.csproj from " + AppContext.BaseDirectory);
        }
    }
}
