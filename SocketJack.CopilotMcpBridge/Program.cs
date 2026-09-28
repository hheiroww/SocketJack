using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace SocketJack.CopilotMcpBridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (SocketJackVisualStudioAuthCallback.TryHandle(args, out int callbackExitCode))
        {
            return callbackExitCode;
        }

        CopilotBridgeOptions options;
        try
        {
            options = CopilotBridgeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.CopilotMcpBridge option error: " + ex.Message);
            return 2;
        }

        if (options.ShowHelp)
        {
            Console.Error.WriteLine(CopilotBridgeOptions.HelpText);
            return 0;
        }

        try
        {
            return options.Transport == CopilotBridgeTransport.HttpProxy
                ? await RunHttpProxyAsync(options).ConfigureAwait(false)
                : await RunStdioAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack.CopilotMcpBridge fatal error: " + SocketJackSecretRedactor.Redact(ex.ToString()));
            return 1;
        }
    }

    private static async Task<int> RunStdioAsync(CopilotBridgeOptions options)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<SocketJackCopilotTools>();

        await builder.Build().RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task<int> RunHttpProxyAsync(CopilotBridgeOptions options)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        ConfigureCommonServices(builder.Services, builder.Logging, options);
        builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost";
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Listen(IPAddress.Loopback, options.ListenPort, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
            });
        });

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!IsLoopbackRequest(context) || !IsAllowedHost(context.Request.Host.Host))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("SocketJack Copilot local proxy accepts loopback requests only.").ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });

        app.MapGet("/socketjack-proxy-health", (CopilotBridgeOptions bridgeOptions) => Results.Json(new
        {
            ok = true,
            server = "SocketJack.CopilotMcpBridge",
            mode = "http-proxy",
            processId = Environment.ProcessId,
            selectedServer = bridgeOptions.ServerId,
            selectedModel = bridgeOptions.ModelId,
            authConfigured = bridgeOptions.HasUpstreamAuthentication,
            authFingerprint = bridgeOptions.AuthFingerprint,
            endpoint = SocketJackSecretRedactor.Redact(bridgeOptions.ServerEndpoint.ToString()),
            webSocket = SocketJackSecretRedactor.Redact(SocketJackWebChatApiClient.BuildWebSocketUri(bridgeOptions.ServerEndpoint).ToString())
        }));

        app.Map("/{**path}", async (HttpContext context, SocketJackModelProxyForwarder forwarder, CancellationToken cancellationToken) =>
        {
            await forwarder.ForwardAsync(context, cancellationToken).ConfigureAwait(false);
        });

        Console.Error.WriteLine("SocketJack.CopilotMcpBridge local proxy listening on http://127.0.0.1:" +
            options.ListenPort.ToString(CultureInfo.InvariantCulture));
        await app.RunAsync().ConfigureAwait(false);
        return 0;
    }

    private static void ConfigureCommonServices(IServiceCollection services, ILoggingBuilder logging, CopilotBridgeOptions options)
    {
        logging.ClearProviders();
        logging.AddConsole(console =>
        {
            console.LogToStandardErrorThreshold = LogLevel.Trace;
        });
        logging.SetMinimumLevel(options.Verbose ? LogLevel.Debug : LogLevel.Warning);
        services.AddSingleton(options);
        services.AddSingleton<SocketJackWebChatApiClient>();
        services.AddSingleton<SocketJackModelProxyForwarder>();
        services.AddHttpClient<SocketJackCopilotGateway>(ConfigureHttpClient);
        services.AddHttpClient<SocketJackModelProxyForwarder>(ConfigureHttpClient);

        void ConfigureHttpClient(IServiceProvider provider, HttpClient client)
        {
            CopilotBridgeOptions bridgeOptions = provider.GetRequiredService<CopilotBridgeOptions>();
            client.BaseAddress = bridgeOptions.ServerEndpoint;
            client.Timeout = TimeSpan.FromSeconds(bridgeOptions.TimeoutSeconds);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
            bridgeOptions.ApplyAuthHeaders(client.DefaultRequestHeaders);
        }
    }

    private static bool IsLoopbackRequest(HttpContext context)
    {
        IPAddress? remote = context.Connection.RemoteIpAddress;
        if (remote == null)
            return false;
        return IPAddress.IsLoopback(remote) ||
            (remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4()));
    }

    private static bool IsAllowedHost(string? host)
    {
        return string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}

internal static class SocketJackVisualStudioAuthCallback
{
    public static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || !string.Equals(args[0], "--vs-auth-callback", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
            {
                throw new ArgumentException("--vs-auth-callback requires a socketjack:// callback URL.");
            }

            Handle(args[1]);
            exitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("SocketJack Visual Studio auth callback failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            exitCode = 1;
        }

        return true;
    }

    private static void Handle(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri? uri) ||
            !uri.Scheme.Equals("socketjack", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Callback URL is not a SocketJack Visual Studio callback.");
        }

        if (uri.Host.Equals("visualstudio-server-browser", StringComparison.OrdinalIgnoreCase))
        {
            OpenServerBrowser();
            return;
        }

        if (!uri.Host.Equals("visualstudio-auth", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Callback URL is not a supported SocketJack Visual Studio callback.");
        }

        string state = GetUriValue(uri, "socketjack_vs_state");
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new ArgumentException("Callback URL is missing socketjack_vs_state.");
        }

        string token = FirstNonEmpty(
            GetUriValue(uri, "socketjack_auth"),
            GetUriValue(uri, "access_token"),
            GetUriValue(uri, "authToken"),
            GetUriValue(uri, "token"));
        string error = FirstNonEmpty(
            GetUriValue(uri, "error"),
            GetUriValue(uri, "error_description"));
        string userName = FirstNonEmpty(
            GetUriValue(uri, "username"),
            GetUriValue(uri, "userName"),
            GetUriValue(uri, "loginName"),
            GetUriValue(uri, "user"));

        Directory.CreateDirectory(CallbackDirectory());
        string path = CallbackPath(state);
        var payload = new JsonObject
        {
            ["state"] = state,
            ["accessToken"] = token,
            ["userName"] = userName,
            ["error"] = error,
            ["savedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
        File.WriteAllText(path, payload.ToJsonString(), new UTF8Encoding(false));
    }

    public static bool OpenServerBrowser()
    {
        string devenv = FindVisualStudioExecutable();
        if (string.IsNullOrWhiteSpace(devenv))
        {
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = devenv,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add("/command");
            startInfo.ArgumentList.Add("LlmRuntime.VisualStudio2026.SocketJackCopilotServersCommand");
            Process.Start(startInfo);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FindVisualStudioExecutable()
    {
        string? directory = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrWhiteSpace(directory); i++)
        {
            string candidate = Path.Combine(directory, "devenv.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        string[] candidates =
        [
            @"C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\2026\Insiders\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Preview\Common7\IDE\devenv.exe",
            @"C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\IDE\devenv.exe"
        ];
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private static string CallbackPath(string state)
    {
        return Path.Combine(CallbackDirectory(), SanitizeState(state) + ".json");
    }

    private static string CallbackDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SocketJack",
            "VisualStudio2026",
            "BrowserLogin");
    }

    private static string SanitizeState(string state)
    {
        var builder = new StringBuilder();
        foreach (char ch in state ?? "")
        {
            if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.Length == 0 ? "missing" : builder.ToString();
    }

    private static string GetUriValue(Uri uri, string name)
    {
        string value = GetQueryValue(uri.Query, name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        string fragment = uri.Fragment;
        if (fragment.StartsWith("#", StringComparison.Ordinal))
        {
            fragment = fragment.Substring(1);
        }

        return GetQueryValue(fragment, name);
    }

    private static string GetQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair.Substring(0, equals);
            if (!string.Equals(Uri.UnescapeDataString(key.Replace("+", " ", StringComparison.Ordinal)), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = equals < 0 ? "" : pair.Substring(equals + 1);
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}

public enum CopilotBridgeTransport
{
    Stdio,
    HttpProxy
}

public sealed class CopilotBridgeOptions
{
    public CopilotBridgeTransport Transport { get; private init; } = CopilotBridgeTransport.Stdio;
    public Uri ServerEndpoint { get; private init; } = new("https://desktop-kssu21a.tail3b2157.ts.net/proxy/TitanX/");
    public Uri LocalWebChatEndpoint { get; private init; } = new("http://127.0.0.1:11436/");
    public bool PreferLocalWebChat { get; private init; } = true;
    public string ServerId { get; private init; } = "TitanX";
    public string ServerName { get; private init; } = "TitanX";
    public string ModelId { get; private init; } = "";
    public string AuthToken { get; private init; } = "";
    public string AuthUserName { get; private init; } = "";
    public bool HasUpstreamAuthentication => !string.IsNullOrWhiteSpace(this.AuthToken);
    public string AuthFingerprint => SocketJackAuthFingerprint.Compute(this.AuthToken);
    public int ListenPort { get; private init; } = 11574;
    public int TimeoutSeconds { get; private init; } = 60;
    public bool Verbose { get; private init; }
    public bool ShowHelp { get; private init; }

    public static string HelpText =>
        """
        SocketJack.CopilotMcpBridge

        Transports:
          --stdio                  Run MCP over stdin/stdout. This is the default.
          --http-proxy             Run a loopback HTTP model proxy at http://127.0.0.1:<port>.

        Options:
          --server-endpoint <url>  SocketJack server endpoint, for example https://desktop-kssu21a.tail3b2157.ts.net/proxy/TitanX.
          --local-webchat-endpoint <url>
                                    Local heirowLLM web-chat endpoint for fast direct streams. Default: http://127.0.0.1:11436.
          --disable-local-webchat  Do not prefer the local heirowLLM endpoint before the configured SocketJack endpoint.
          --server-id <id>         MasterList server id.
          --server-name <name>     Human-readable server name.
          --model <id>             Selected model id.
          --auth-token <token>     Optional token; redacted from logs.
          --auth-user <username>   Optional remembered SocketJack.com user for token refresh.
          --listen-port <number>   Loopback HTTP proxy port. Default: 11574.
          --timeout <seconds>      HTTP/WebSocket request timeout. Default: 60.
          --verbose                Enable debug logging to stderr.
          --help                   Show this help.
        """;

    public static CopilotBridgeOptions Parse(string[] args)
    {
        CopilotBridgeTransport transport = CopilotBridgeTransport.Stdio;
        Uri endpoint = ReadEndpoint(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ENDPOINT") ?? "https://desktop-kssu21a.tail3b2157.ts.net/proxy/TitanX");
        Uri localWebChatEndpoint = ReadEndpoint(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_LOCAL_WEBCHAT_ENDPOINT") ?? "http://127.0.0.1:11436");
        bool preferLocalWebChat = !string.Equals(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_DISABLE_LOCAL_WEBCHAT"), "1", StringComparison.OrdinalIgnoreCase);
        string serverId = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ID") ?? "TitanX";
        string serverName = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_NAME") ?? serverId;
        string model = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_MODEL_ID") ?? "";
        string authToken = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_TOKEN") ?? "";
        string authUserName = Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_USER") ?? "";
        int listenPort = ReadPort(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_PROXY_PORT"), 11574, "SOCKETJACK_COPILOT_PROXY_PORT");
        int timeout = ReadPositiveInt(Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_TIMEOUT_SECONDS"), 60, "SOCKETJACK_COPILOT_TIMEOUT_SECONDS");
        bool verbose = false;
        bool help = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--stdio":
                    transport = CopilotBridgeTransport.Stdio;
                    break;
                case "--http-proxy":
                    transport = CopilotBridgeTransport.HttpProxy;
                    break;
                case "--server-endpoint":
                    endpoint = ReadEndpoint(RequireValue(args, ref i, arg));
                    break;
                case "--local-webchat-endpoint":
                    localWebChatEndpoint = ReadEndpoint(RequireValue(args, ref i, arg));
                    break;
                case "--disable-local-webchat":
                    preferLocalWebChat = false;
                    break;
                case "--server-id":
                    serverId = RequireValue(args, ref i, arg);
                    break;
                case "--server-name":
                    serverName = RequireValue(args, ref i, arg);
                    break;
                case "--model":
                case "--model-id":
                    model = RequireValue(args, ref i, arg);
                    break;
                case "--auth-token":
                    authToken = RequireValue(args, ref i, arg);
                    break;
                case "--auth-user":
                case "--auth-username":
                    authUserName = RequireValue(args, ref i, arg);
                    break;
                case "--listen-port":
                case "--port":
                    listenPort = ReadPort(RequireValue(args, ref i, arg), listenPort, arg);
                    break;
                case "--timeout":
                    timeout = ReadPositiveInt(RequireValue(args, ref i, arg), timeout, arg);
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                case "--help":
                case "-h":
                case "/?":
                    help = true;
                    break;
                default:
                    throw new ArgumentException("Unknown argument: " + arg);
            }
        }

        if (string.IsNullOrWhiteSpace(model) && !help)
            throw new ArgumentException("--model is required.");

        return new CopilotBridgeOptions
        {
            Transport = transport,
            ServerEndpoint = endpoint,
            LocalWebChatEndpoint = localWebChatEndpoint,
            PreferLocalWebChat = preferLocalWebChat,
            ServerId = string.IsNullOrWhiteSpace(serverId) ? endpoint.Host : serverId.Trim(),
            ServerName = string.IsNullOrWhiteSpace(serverName) ? serverId : serverName.Trim(),
            ModelId = model.Trim(),
            AuthToken = authToken,
            AuthUserName = authUserName.Trim(),
            ListenPort = listenPort,
            TimeoutSeconds = timeout,
            Verbose = verbose,
            ShowHelp = help
        };
    }

    public void ApplyAuthHeaders(HttpRequestHeaders headers)
    {
        if (!string.IsNullOrWhiteSpace(this.AuthToken))
        {
            string token = this.AuthToken.Trim();
            headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            headers.TryAddWithoutValidation("X-SocketJack-Auth", token);
        }

        if (!string.IsNullOrWhiteSpace(this.AuthUserName))
        {
            string userName = this.AuthUserName.Trim();
            headers.TryAddWithoutValidation("X-SocketJack-User", userName);
            headers.TryAddWithoutValidation("X-SocketJack-Username", userName);
        }
    }

    public void ApplyAuthHeaders(ClientWebSocketOptions options)
    {
        if (!string.IsNullOrWhiteSpace(this.AuthToken))
        {
            string token = this.AuthToken.Trim();
            options.SetRequestHeader("Authorization", "Bearer " + token);
            options.SetRequestHeader("X-SocketJack-Auth", token);
        }

        if (!string.IsNullOrWhiteSpace(this.AuthUserName))
        {
            string userName = this.AuthUserName.Trim();
            options.SetRequestHeader("X-SocketJack-User", userName);
            options.SetRequestHeader("X-SocketJack-Username", userName);
        }
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
            throw new ArgumentException(option + " requires a value.");
        index++;
        return args[index];
    }

    private static Uri ReadEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Server endpoint is required.");
        string normalized = value.Trim();
        if (!normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        if (!Uri.TryCreate(normalized.TrimEnd('/') + "/", UriKind.Absolute, out Uri? uri))
            throw new ArgumentException("Server endpoint is not a valid absolute URI.");
        if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Server endpoint must be http or https.");
        return uri;
    }

    private static int ReadPort(string? value, int fallback, string name)
    {
        int port = ReadPositiveInt(value, fallback, name);
        if (port < 1 || port > 65535)
            throw new ArgumentException(name + " must be between 1 and 65535.");
        return port;
    }

    private static int ReadPositiveInt(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException(name + " must be a positive integer.");
        return parsed;
    }
}

public static class SocketJackAuthFingerprint
{
    public static string Compute(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return "";

        byte[] tokenBytes = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            byte[] hash = SHA256.HashData(tokenBytes);
            return Convert.ToHexString(hash.AsSpan(0, 12));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }
}

[McpServerToolType]
public sealed class SocketJackCopilotTools
{
    [McpServerTool(
        Name = "socketjack_copilot_server_status",
        Title = "SocketJack Copilot Server Status",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets the selected SocketJack server status for the Visual Studio Copilot bridge.")]
    public Task<string> GetServerStatusAsync(SocketJackCopilotGateway gateway, CancellationToken cancellationToken)
    {
        return gateway.GetServerStatusAsync(cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_copilot_get_models",
        Title = "SocketJack Copilot Models",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets the selected SocketJack server model metadata.")]
    public Task<string> GetModelsAsync(SocketJackCopilotGateway gateway, CancellationToken cancellationToken)
    {
        return gateway.GetModelsAsync(cancellationToken);
    }

    [McpServerTool(
        Name = "socketjack_copilot_get_path",
        Title = "SocketJack Copilot Safe API GET",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = true)]
    [Description("Gets a safe selected-server API path. The path must be relative and start with /api/ or /v1/, or be exactly /health.")]
    public Task<string> GetPathAsync(
        SocketJackCopilotGateway gateway,
        [Description("Relative API path such as /api/models, /api/model-runtime/models, /v1/models, or /health. Absolute URLs are rejected.")] string path,
        CancellationToken cancellationToken)
    {
        return gateway.GetPathAsync(path, cancellationToken);
    }
}

public static class CopilotBridgeToolCatalog
{
    public static readonly string[] ToolNames =
    {
        "socketjack_copilot_server_status",
        "socketjack_copilot_get_models",
        "socketjack_copilot_get_path"
    };
}

public sealed class SocketJackCopilotGateway
{
    private readonly HttpClient _httpClient;
    private readonly CopilotBridgeOptions _options;

    public SocketJackCopilotGateway(HttpClient httpClient, CopilotBridgeOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<string> GetServerStatusAsync(CancellationToken cancellationToken)
    {
        JsonObject status = new()
        {
            ["serverId"] = _options.ServerId,
            ["serverName"] = _options.ServerName,
            ["modelId"] = _options.ModelId,
            ["endpoint"] = SocketJackSecretRedactor.Redact(_options.ServerEndpoint.ToString())
        };

        string health = await TryGetStringAsync("/health", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(health))
            health = await TryGetStringAsync("/api/health", cancellationToken).ConfigureAwait(false);
        status["health"] = string.IsNullOrWhiteSpace(health) ? "No health endpoint responded." : SocketJackSecretRedactor.Redact(health);
        return status.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task<string> GetModelsAsync(CancellationToken cancellationToken)
    {
        string models = await TryGetStringAsync("/api/models", cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(models))
            models = await TryGetStringAsync("/api/model-runtime/models", cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(models) ? "{\"models\":[]}" : SocketJackSecretRedactor.Redact(models);
    }

    public async Task<string> GetPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!SocketJackSafePath.IsSafeGetPath(path))
            throw new ArgumentException("Path must be relative and start with /api/ or /v1/, or be exactly /health.");

        string result = await TryGetStringAsync(path, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(result) ? "{}" : SocketJackSecretRedactor.Redact(result);
    }

    private async Task<string> TryGetStringAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.GetAsync(path.TrimStart('/'), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return "";
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }
}

public sealed class SocketJackModelProxyForwarder
{
    private enum OpenAiProxyResponseShape
    {
        ChatCompletions,
        Responses
    }

    private static readonly object ResponseLifecycleStartedItemKey = new();
    private static readonly object ResponseTextStartedItemKey = new();

    private static readonly TimeSpan DirectChatStreamHeartbeatInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DirectChatStreamIdleTimeout = ReadPositiveEnvironmentTimeSpan(
        "SOCKETJACK_COPILOT_BRIDGE_STREAM_IDLE_TIMEOUT_SECONDS",
        TimeSpan.FromMinutes(3));
    private static readonly TimeSpan DirectChatStreamMaxDuration = ReadPositiveEnvironmentTimeSpan(
        "SOCKETJACK_COPILOT_BRIDGE_STREAM_MAX_SECONDS",
        TimeSpan.FromMinutes(20));
    internal const int VisualStudioAutopilotMinimumMaxTokens = 8192;
    internal const int VisualStudioStreamMinimumTimeoutSeconds = 1200;

    private readonly HttpClient _httpClient;
    private readonly SocketJackWebChatApiClient _webChatClient;
    private readonly CopilotBridgeOptions _options;
    private static readonly object ServerBrowserOpenLock = new();
    private static DateTimeOffset lastServerBrowserOpenUtc = DateTimeOffset.MinValue;

    public SocketJackModelProxyForwarder(HttpClient httpClient, SocketJackWebChatApiClient webChatClient, CopilotBridgeOptions options)
    {
        _httpClient = httpClient;
        _webChatClient = webChatClient;
        _options = options;
    }

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        string path = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
        if (!SocketJackSafePath.IsSafeProxyPath(path))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Unsupported proxy path.", cancellationToken).ConfigureAwait(false);
            return;
        }

        string upstreamPath = SocketJackProxyPath.NormalizeForUpstream(path);
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            SocketJackProxyPath.IsOpenAiModelsPath(upstreamPath))
        {
            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildModelsResponse(_options.ModelId), cancellationToken).ConfigureAwait(false);
            return;
        }
        if (context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
            SocketJackProxyPath.IsOllamaTagsPath(upstreamPath))
        {
            await WriteProxyResponseAsync(context, SocketJackOllamaChatAdapter.BuildTagsResponse(_options.ModelId), cancellationToken).ConfigureAwait(false);
            return;
        }

        byte[] body = await ReadRequestBodyAsync(context.Request, cancellationToken).ConfigureAwait(false);
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
            SocketJackProxyPath.IsOllamaChatPath(upstreamPath))
        {
            ProxyResponse ollamaResponse;
            try
            {
                byte[] openAiBody = SocketJackOllamaChatAdapter.BuildOpenAiChatRequest(body, _options.ModelId);
                ProxyResponse openAiResponse = await ForwardOpenAiChatViaWebChatAsync(
                    openAiBody,
                    OpenAiProxyResponseShape.ChatCompletions,
                    cancellationToken).ConfigureAwait(false);
                ollamaResponse = SocketJackOllamaChatAdapter.BuildChatResponse(openAiResponse, _options.ModelId);
            }
            catch (JsonException ex)
            {
                ollamaResponse = SocketJackOpenAiChatAdapter.BuildErrorResponse(
                    StatusCodes.Status400BadRequest,
                    "Invalid Ollama chat request JSON: " + ex.Message);
            }

            await WriteProxyResponseAsync(context, ollamaResponse, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (SocketJackProxyPath.IsOpenAiChatCompletionsPath(upstreamPath) ||
            SocketJackProxyPath.IsOpenAiResponsesPath(upstreamPath))
        {
            OpenAiProxyResponseShape shape = SocketJackProxyPath.IsOpenAiResponsesPath(upstreamPath)
                ? OpenAiProxyResponseShape.Responses
                : OpenAiProxyResponseShape.ChatCompletions;

            if (await TryStreamOpenAiChatViaWebChatAsync(context, body, shape, cancellationToken).ConfigureAwait(false))
                return;

            ProxyResponse adapted = await ForwardOpenAiChatViaWebChatAsync(body, shape, cancellationToken).ConfigureAwait(false);
            await WriteProxyResponseAsync(context, adapted, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProxyResponse? direct = await TryForwardDirectAsync(context.Request, upstreamPath, body, cancellationToken).ConfigureAwait(false);
        if (direct != null && !ShouldFallback(direct.StatusCode))
        {
            await WriteProxyResponseAsync(context, direct, cancellationToken).ConfigureAwait(false);
            return;
        }

        ProxyResponse viaSocket = await _webChatClient.ForwardAsync(context.Request, upstreamPath, body, cancellationToken).ConfigureAwait(false);
        await WriteProxyResponseAsync(context, viaSocket, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteServerOfflineSseAsync(HttpContext context, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        this.TryOpenServerBrowserForOffline();
        string message = SocketJackOpenAiChatAdapter.BuildServerOfflineAssistantText(_options.ServerName);
        StartOpenAiSseResponse(context);
        await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
        await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, message), cancellationToken).ConfigureAwait(false);
        await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, message, cancellationToken).ConfigureAwait(false);
    }

    private ProxyResponse BuildServerOfflineResponse(JsonObject openAiRequest, OpenAiProxyResponseShape shape, string model)
    {
        this.TryOpenServerBrowserForOffline();
        string message = SocketJackOpenAiChatAdapter.BuildServerOfflineAssistantText(_options.ServerName);
        return shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.BuildResponseResponse(openAiRequest, message, model)
            : SocketJackOpenAiChatAdapter.BuildChatResponse(openAiRequest, message, model);
    }

    private void TryOpenServerBrowserForOffline()
    {
        lock (ServerBrowserOpenLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((now - lastServerBrowserOpenUtc) < TimeSpan.FromMinutes(2))
            {
                return;
            }

            lastServerBrowserOpenUtc = now;
        }

        _ = Task.Run(SocketJackVisualStudioAuthCallback.OpenServerBrowser);
    }

    private async Task<bool> TryStreamOpenAiChatViaWebChatAsync(HttpContext context, byte[] openAiBody, OpenAiProxyResponseShape shape, CancellationToken cancellationToken)
    {
        JsonObject openAiRequest;
        try
        {
            openAiRequest = JsonNode.Parse(openAiBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status400BadRequest, "Invalid OpenAI chat request JSON: " + ex.Message), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest))
            return false;

        JsonObject chatStreamRequest = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(openAiRequest, _options.ModelId);
        string canonicalModel = await ResolveCanonicalModelIdAsync(ChooseSelectedServerModel(chatStreamRequest["model"]?.ToString()), cancellationToken).ConfigureAwait(false);
        chatStreamRequest["model"] = canonicalModel;
        string completionId = shape == OpenAiProxyResponseShape.Responses
            ? SocketJackOpenAiChatAdapter.CreateResponseId()
            : SocketJackOpenAiChatAdapter.CreateCompletionId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string model = canonicalModel;

        JsonObject directOpenAiRequest = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(openAiRequest, canonicalModel);
        byte[]? directOpenAiBody = Encoding.UTF8.GetBytes(directOpenAiRequest.ToJsonString());

        var availableToolNames = SocketJackOpenAiChatAdapter.ReadAvailableToolNames(chatStreamRequest);
        bool preferDirectOpenAiFirst = directOpenAiBody != null;
        string requestId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        if (preferDirectOpenAiFirst)
        {
            if (await TryStreamOpenAiChatViaDirectOpenAiAsync(context, directOpenAiBody!, openAiBody, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
                return true;
            if (await TryStreamOpenAiChatViaWebSocketOpenAiAsync(context, directOpenAiBody!, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
                return true;
        }

        await TryUploadVisualStudioReferenceFilesAsync(chatStreamRequest, cancellationToken).ConfigureAwait(false);
        byte[] chatStreamBody = Encoding.UTF8.GetBytes(chatStreamRequest.ToJsonString());

        if (await TryStreamOpenAiChatViaDirectChatStreamAsync(context, chatStreamBody, openAiBody, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
            return true;

        if (directOpenAiBody != null && !preferDirectOpenAiFirst)
        {
            if (await TryStreamOpenAiChatViaDirectOpenAiAsync(context, directOpenAiBody, openAiBody, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
                return true;
            if (await TryStreamOpenAiChatViaWebSocketOpenAiAsync(context, directOpenAiBody, shape, completionId, created, model, cancellationToken).ConfigureAwait(false))
                return true;
        }

        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _options.ApplyAuthHeaders(socket.Options);

        try
        {
            await socket.ConnectAsync(SocketJackWebChatApiClient.BuildWebSocketUri(_options.ServerEndpoint), cancellationToken).ConfigureAwait(false);
            JsonObject envelope = BuildWebChatEnvelope(requestId, "/api/chat-stream", chatStreamBody);
            byte[] payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

            bool responseStarted = false;
            bool roleSent = false;
            bool wroteContent = false;
            var assistantText = new SocketJackAssistantTextAccumulator();
            var toolCallText = new SocketJackToolCallTextAccumulator(availableToolNames);
            string visualStudioDefaultProjectPath = SocketJackOpenAiChatAdapter.FindLatestVisualStudioProjectPath(chatStreamBody);
            bool preferVisualStudioXamlCodeBehindForVbFiles = SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(chatStreamBody);
            string lineBuffer = "";
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                while (socket.State == WebSocketState.Open)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        message.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    byte[] messageBytes = message.ToArray();
                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        JsonObject? textMessage = ParseJsonObject(Encoding.UTF8.GetString(messageBytes));
                        if (textMessage == null)
                            continue;

                        string type = textMessage["type"]?.ToString() ?? "";
                        string messageId = textMessage["id"]?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(messageId) && !string.Equals(messageId, requestId, StringComparison.Ordinal))
                            continue;

                        if (type.Equals("response-start", StringComparison.OrdinalIgnoreCase))
                        {
                            int statusCode = ReadInt(textMessage, "statusCode") ?? StatusCodes.Status200OK;
                            if (statusCode < 200 || statusCode >= 300)
                            {
                                if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(statusCode, textMessage.ToJsonString()))
                                {
                                    await this.WriteServerOfflineSseAsync(context, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                                    return true;
                                }

                                await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "The selected model server returned HTTP " + statusCode.ToString(CultureInfo.InvariantCulture) + "."), cancellationToken).ConfigureAwait(false);
                                return true;
                            }

                            StartOpenAiSseResponse(context);
                            responseStarted = true;
                            await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                            roleSent = true;
                            continue;
                        }

                        if (type.Equals("response-end", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!string.IsNullOrEmpty(lineBuffer))
                            {
                                ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lineBuffer, assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, false, cancellationToken).ConfigureAwait(false);
                                if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                                    return true;
                                wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                                lineBuffer = "";
                            }

                            if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                                return true;

                            if (!wroteContent &&
                                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                            {
                                return true;
                            }

                            if (!wroteContent)
                                wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);

                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                        {
                            string errorText = textMessage["error"]?.ToString() ?? "unknown error";
                            if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(StatusCodes.Status502BadGateway, errorText))
                            {
                                await this.WriteServerOfflineSseAsync(context, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                                return true;
                            }

                            await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "Model WebSocket proxy error: " + errorText), cancellationToken).ConfigureAwait(false);
                            return true;
                        }

                        continue;
                    }

                    if (!responseStarted)
                    {
                        StartOpenAiSseResponse(context);
                        responseStarted = true;
                    }

                    if (!roleSent)
                    {
                        await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                        roleSent = true;
                    }

                    ChatStreamBinaryWriteResult binaryResult = await WriteChatStreamBinaryAsOpenAiSseAsync(context, requestId, messageBytes, lineBuffer, assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false);
                    if (binaryResult.FinalizedToolCalls)
                        return true;
                    lineBuffer = binaryResult.LineBuffer;
                    wroteContent = wroteContent || binaryResult.WroteContent;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                return true;

            if (!wroteContent &&
                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (!wroteContent)
                wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);
            await FinishOpenAiSseAsync(context, shape, completionId, created, model, responseStarted, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!context.Response.HasStarted)
            {
                if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(StatusCodes.Status502BadGateway, ex.Message))
                {
                    await this.WriteServerOfflineSseAsync(context, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                await WriteProxyResponseAsync(context, SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "Model streaming adapter failed: " + ex.Message), cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
    }

    private async Task<bool> TryStreamOpenAiChatViaDirectChatStreamAsync(HttpContext context, byte[] chatStreamBody, byte[] recoveryRequestBody, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        (string sessionId, string streamId) = ReadChatStreamRequestIds(chatStreamBody);
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        DateTimeOffset lastUpstreamDataUtc = startedUtc;
        bool wroteContent = false;
        var assistantText = new SocketJackAssistantTextAccumulator();
        var availableToolNames = SocketJackOpenAiChatAdapter.ReadAvailableToolNames(chatStreamBody);
        var toolCallText = new SocketJackToolCallTextAccumulator(availableToolNames);
        bool visualStudioToolRequest = SocketJackOpenAiChatAdapter.IsVisualStudioToolRequest(recoveryRequestBody) ||
            SocketJackOpenAiChatAdapter.IsVisualStudioToolRequest(chatStreamBody);
        string visualStudioDefaultProjectPath = SocketJackOpenAiChatAdapter.FindLatestVisualStudioProjectPath(recoveryRequestBody);
        if (string.IsNullOrWhiteSpace(visualStudioDefaultProjectPath))
            visualStudioDefaultProjectPath = SocketJackOpenAiChatAdapter.FindLatestVisualStudioProjectPath(chatStreamBody);
        bool preferVisualStudioXamlCodeBehindForVbFiles =
            SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(recoveryRequestBody) ||
            SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(chatStreamBody);
        bool suppressedRecoverableMalformedToolAttempt = false;
        bool suppressedRecoverableToolTranscript = false;
        bool suppressedRecoverablePrematureClarification = false;
        bool suppressedRecoverableNoVisibleFallback = false;
        bool wroteVisibleWaitStatus = false;
        bool responseStarted = false;

        using var client = new HttpClient
        {
            BaseAddress = _options.ServerEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
        _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-stream"));
            request.Headers.Accept.ParseAdd("application/x-ndjson");
            request.Content = new ByteArrayContent(chatStreamBody);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var upstreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken upstreamToken = upstreamCancellation.Token;

            Task<HttpResponseMessage> sendTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
            while (!sendTask.IsCompleted)
            {
                Task completed = await Task.WhenAny(sendTask, Task.Delay(DirectChatStreamHeartbeatInterval, cancellationToken)).ConfigureAwait(false);
                if (completed == sendTask)
                    break;

                if (!responseStarted)
                {
                    StartOpenAiSseResponse(context);
                    responseStarted = true;
                    await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                }

                if (!wroteVisibleWaitStatus)
                {
                    await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, "Working on it...\n\n", cancellationToken).ConfigureAwait(false);
                    wroteVisibleWaitStatus = true;
                }
                else
                {
                    await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, ""), cancellationToken).ConfigureAwait(false);
                }
            }

            using HttpResponseMessage response = await sendTask.ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string detail = await ReadHttpResponsePreviewAsync(response, cancellationToken).ConfigureAwait(false);
                if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer((int)response.StatusCode, detail))
                {
                    if (responseStarted)
                    {
                        await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, SocketJackOpenAiChatAdapter.BuildServerOfflineAssistantText(_options.ServerName), cancellationToken).ConfigureAwait(false);
                        await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                        return true;
                    }

                    await this.WriteServerOfflineSseAsync(context, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                if (!responseStarted && ShouldFallbackFromDirectChatStream((int)response.StatusCode, detail))
                {
                    return false;
                }

                if (!responseStarted)
                {
                    StartOpenAiSseResponse(context);
                    responseStarted = true;
                    await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                }
                await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model,
                    "The selected model server returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + (string.IsNullOrWhiteSpace(detail) ? "." : ": " + detail), cancellationToken).ConfigureAwait(false);
                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (!responseStarted)
            {
                StartOpenAiSseResponse(context);
                responseStarted = true;
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync(upstreamToken).ConfigureAwait(false);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            string lineBuffer = "";
            var captured = new StringBuilder();
            try
            {
                Task<int>? pendingRead = null;
                while (!cancellationToken.IsCancellationRequested)
                {
                    pendingRead ??= stream.ReadAsync(buffer.AsMemory(0, buffer.Length), upstreamToken).AsTask();
                    Task completed = await Task.WhenAny(pendingRead, Task.Delay(DirectChatStreamHeartbeatInterval, cancellationToken)).ConfigureAwait(false);
                    if (!ReferenceEquals(completed, pendingRead))
                    {
                        string? timeoutMessage = GetDirectChatStreamTimeoutMessage(startedUtc, lastUpstreamDataUtc);
                        if (!string.IsNullOrWhiteSpace(timeoutMessage))
                        {
                            bool stopped = await TryStopRemoteChatStreamAsync(streamId, sessionId, timeoutMessage!, CancellationToken.None).ConfigureAwait(false);
                            await WriteSyntheticAssistantDeltaAsync(
                                context,
                                assistantText,
                                shape,
                                completionId,
                                created,
                                model,
                                timeoutMessage + (stopped ? " The upstream model stream was asked to stop." : " The bridge could not confirm an upstream stop request."),
                                cancellationToken).ConfigureAwait(false);
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            upstreamCancellation.Cancel();
                            await ObserveCancelledReadAsync(pendingRead).ConfigureAwait(false);
                            return true;
                        }

                        if (!wroteContent && !wroteVisibleWaitStatus)
                        {
                            await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, "Working on it...\n\n", cancellationToken).ConfigureAwait(false);
                            wroteVisibleWaitStatus = true;
                        }
                        else
                        {
                            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, ""), cancellationToken).ConfigureAwait(false);
                        }
                        continue;
                    }

                    int read = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (read <= 0)
                        break;

                    lastUpstreamDataUtc = DateTimeOffset.UtcNow;
                    string chunkText = Encoding.UTF8.GetString(buffer, 0, read);
                    if (captured.Length < 1024 * 1024)
                        captured.Append(chunkText);

                    if (lineBuffer.Length == 0 && !ContainsLineBreak(chunkText) && !IsHttpChunkSizeLine(chunkText) && LooksLikeImmediateRawTextChunk(chunkText))
                    {
                        if (SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(chunkText))
                        {
                            suppressedRecoverableMalformedToolAttempt = true;
                            continue;
                        }
                        if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(chunkText))
                        {
                            suppressedRecoverableToolTranscript = true;
                            continue;
                        }
                        if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(chunkText) &&
                            SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(recoveryRequestBody, out _))
                        {
                            suppressedRecoverablePrematureClarification = true;
                            continue;
                        }
                        if (visualStudioToolRequest &&
                            SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(chunkText))
                        {
                            suppressedRecoverableNoVisibleFallback = true;
                            continue;
                        }

                        string content = assistantText.AcceptDeltaOrSnapshot(chunkText);
                        if (!string.IsNullOrEmpty(content))
                        {
                            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
                            wroteContent = true;
                        }

                        continue;
                    }

                    string text = lineBuffer + chunkText;
                    string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                    lineBuffer = lines.Length == 0 ? "" : lines[^1];
                    for (int i = 0; i < lines.Length - 1; i++)
                    {
                        if (SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(lines[i]))
                        {
                            suppressedRecoverableMalformedToolAttempt = true;
                            continue;
                        }
                        if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(lines[i]))
                        {
                            suppressedRecoverableToolTranscript = true;
                            continue;
                        }
                        if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(lines[i]) &&
                            SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(recoveryRequestBody, out _))
                        {
                            suppressedRecoverablePrematureClarification = true;
                            continue;
                        }

                        ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lines[i], assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, visualStudioToolRequest, cancellationToken).ConfigureAwait(false);
                        if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                            return true;
                        if (lineResult == ChatStreamLineResult.SuppressedNoVisibleFallback)
                        {
                            suppressedRecoverableNoVisibleFallback = true;
                            continue;
                        }
                        wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                        if (lineResult == ChatStreamLineResult.Done)
                        {
                            if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                                return true;
                            if (!wroteContent &&
                                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, recoveryRequestBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                            {
                                return true;
                            }
                            if ((suppressedRecoverableMalformedToolAttempt || suppressedRecoverableToolTranscript || suppressedRecoverablePrematureClarification || suppressedRecoverableNoVisibleFallback) &&
                                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, recoveryRequestBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                            {
                                return true;
                            }

                            if (!wroteContent && !suppressedRecoverableMalformedToolAttempt && !suppressedRecoverableToolTranscript && !suppressedRecoverablePrematureClarification && !suppressedRecoverableNoVisibleFallback)
                                wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                            if (!wroteContent)
                                wroteContent = await TryWriteNoVisibleAssistantRecoveryAsync(context, recoveryRequestBody, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                            if (!wroteContent)
                                wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);
                            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                            return true;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (!string.IsNullOrWhiteSpace(lineBuffer))
            {
                if (SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(lineBuffer))
                {
                    suppressedRecoverableMalformedToolAttempt = true;
                }
                else if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(lineBuffer))
                {
                    suppressedRecoverableToolTranscript = true;
                }
                else if (SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(lineBuffer) &&
                    SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(recoveryRequestBody, out _))
                {
                    suppressedRecoverablePrematureClarification = true;
                }
                else if (visualStudioToolRequest &&
                    SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(lineBuffer))
                {
                    suppressedRecoverableNoVisibleFallback = true;
                }
                else
                {
                    ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lineBuffer, assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, visualStudioToolRequest, cancellationToken).ConfigureAwait(false);
                    if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                        return true;
                    if (lineResult == ChatStreamLineResult.SuppressedNoVisibleFallback)
                        suppressedRecoverableNoVisibleFallback = true;
                    wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                }
            }

            if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                return true;
            if (!wroteContent &&
                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, recoveryRequestBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }
            if ((suppressedRecoverableMalformedToolAttempt || suppressedRecoverableToolTranscript || suppressedRecoverablePrematureClarification || suppressedRecoverableNoVisibleFallback) &&
                await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, recoveryRequestBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            if (!wroteContent && !suppressedRecoverableMalformedToolAttempt && !suppressedRecoverableToolTranscript && !suppressedRecoverablePrematureClarification && !suppressedRecoverableNoVisibleFallback)
                wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
            if (!wroteContent)
                wroteContent = await TryWriteNoVisibleAssistantRecoveryAsync(context, recoveryRequestBody, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
            if (!wroteContent)
                wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);
            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (context.Response.HasStarted)
            {
                try
                {
                    if (!wroteContent &&
                        await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, recoveryRequestBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, CancellationToken.None).ConfigureAwait(false))
                    {
                        return true;
                    }

                    if (!wroteContent)
                        await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), CancellationToken.None).ConfigureAwait(false);
                    await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                return true;
            }

            return false;
        }
    }

    private async Task<bool> TryStreamOpenAiChatViaDirectOpenAiAsync(HttpContext context, byte[] openAiBody, byte[] recoveryRequestBody, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        using var client = new HttpClient
        {
            BaseAddress = _options.ServerEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
        _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

        bool wroteContent = false;
        var assistantText = new SocketJackAssistantTextAccumulator();
        var availableToolNames = SocketJackOpenAiChatAdapter.ReadAvailableToolNames(openAiBody);
        var toolCallText = new SocketJackToolCallTextAccumulator(availableToolNames);
        bool responseStarted = false;
        bool visualStudioToolRequest = SocketJackOpenAiChatAdapter.IsVisualStudioToolRequest(openAiBody);
        string visualStudioDefaultProjectPath = SocketJackOpenAiChatAdapter.FindLatestVisualStudioProjectPath(openAiBody);
        bool preferVisualStudioXamlCodeBehindForVbFiles = SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(openAiBody);
        bool suppressedRecoverableMalformedToolAttempt = false;
        bool suppressedRecoverableToolTranscript = false;
        bool suppressedRecoverablePrematureClarification = false;
        bool suppressedRecoverableNoVisibleFallback = false;
        int lastForwardStatusCode = 0;
        string lastForwardStatusMessage = "";
        using var upstreamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        int effectiveTimeoutSeconds = SocketJackOpenAiChatAdapter.GetEffectiveOpenAiStreamTimeoutSeconds(openAiBody, _options.TimeoutSeconds);
        if (effectiveTimeoutSeconds > 0)
            upstreamCancellation.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSeconds));
        CancellationToken upstreamToken = upstreamCancellation.Token;

        try
        {
            if (visualStudioToolRequest)
            {
                StartOpenAiSseResponse(context);
                responseStarted = true;
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
            }

            foreach (string path in OpenAiChatForwardPaths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri(path));
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new ByteArrayContent(openAiBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response = await SendOpenAiStreamingRequestAsync(client, request, context, responseStarted, upstreamToken, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    lastForwardStatusCode = (int)response.StatusCode;
                    lastForwardStatusMessage = "The selected model endpoint returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " for " + path + ".";
                    continue;
                }

                if (!responseStarted)
                {
                    StartOpenAiSseResponse(context);
                    responseStarted = true;
                    await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(upstreamToken).ConfigureAwait(false);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
                string lineBuffer = "";
                var captured = new StringBuilder();
                Task<int>? pendingRead = null;
                try
                {
                    while (!upstreamToken.IsCancellationRequested)
                    {
                        pendingRead ??= stream.ReadAsync(buffer.AsMemory(0, buffer.Length), upstreamToken).AsTask();
                        Task completed = await Task.WhenAny(pendingRead, Task.Delay(DirectChatStreamHeartbeatInterval, cancellationToken)).ConfigureAwait(false);
                        if (!ReferenceEquals(completed, pendingRead))
                        {
                            await WriteOpenAiSseAsync(context, ": socketjack keepalive\n\n", cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        int read = await pendingRead.ConfigureAwait(false);
                        pendingRead = null;
                        if (read <= 0)
                            break;

                        string chunkText = Encoding.UTF8.GetString(buffer, 0, read);
                        if (captured.Length < 1024 * 1024)
                            captured.Append(chunkText);

                        if (lineBuffer.Length == 0 && !ContainsLineBreak(chunkText) && !IsHttpChunkSizeLine(chunkText) && LooksLikeImmediateRawTextChunk(chunkText))
                        {
                            if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(chunkText))
                            {
                                suppressedRecoverableMalformedToolAttempt = true;
                                continue;
                            }
                            if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(chunkText))
                            {
                                suppressedRecoverableToolTranscript = true;
                                continue;
                            }
                            if (visualStudioToolRequest &&
                                SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(chunkText) &&
                                SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(openAiBody, out _))
                            {
                                suppressedRecoverablePrematureClarification = true;
                                continue;
                            }
                            if (visualStudioToolRequest &&
                                SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(chunkText))
                            {
                                suppressedRecoverableNoVisibleFallback = true;
                                continue;
                            }

                            string content = assistantText.AcceptDeltaOrSnapshot(chunkText);
                            if (!string.IsNullOrEmpty(content))
                            {
                                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
                                wroteContent = true;
                            }

                            continue;
                        }

                        string text = lineBuffer + chunkText;
                        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                        lineBuffer = lines.Length == 0 ? "" : lines[^1];
                        for (int i = 0; i < lines.Length - 1; i++)
                        {
                            if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(lines[i]))
                            {
                                suppressedRecoverableMalformedToolAttempt = true;
                                continue;
                            }
                            if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(lines[i]))
                            {
                                suppressedRecoverableToolTranscript = true;
                                continue;
                            }
                            if (visualStudioToolRequest &&
                                SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(lines[i]) &&
                                SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(openAiBody, out _))
                            {
                                suppressedRecoverablePrematureClarification = true;
                                continue;
                            }

                            ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lines[i], assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, visualStudioToolRequest, cancellationToken).ConfigureAwait(false);
                            if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                                return true;
                            if (lineResult == ChatStreamLineResult.SuppressedNoVisibleFallback)
                            {
                                suppressedRecoverableNoVisibleFallback = true;
                                continue;
                            }
                            wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                            if (lineResult == ChatStreamLineResult.Done)
                            {
                                if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                                    return true;
                                if (!wroteContent &&
                                    await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                                {
                                    return true;
                                }
                                if ((suppressedRecoverableMalformedToolAttempt || suppressedRecoverableToolTranscript || suppressedRecoverablePrematureClarification || suppressedRecoverableNoVisibleFallback) &&
                                    await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                                {
                                    return true;
                                }

                                if (!wroteContent && !suppressedRecoverableMalformedToolAttempt && !suppressedRecoverableToolTranscript && !suppressedRecoverablePrematureClarification && !suppressedRecoverableNoVisibleFallback)
                                    wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                                if (!wroteContent)
                                    wroteContent = await TryWriteNoVisibleAssistantRecoveryAsync(context, recoveryRequestBody, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                                if (!wroteContent)
                                    wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);
                                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                                return true;
                            }
                        }
                    }
                }
                finally
                {
                    await ObserveCancelledReadAsync(pendingRead).ConfigureAwait(false);
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (!string.IsNullOrWhiteSpace(lineBuffer))
                {
                    if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableMalformedToolAttemptText(lineBuffer))
                    {
                        suppressedRecoverableMalformedToolAttempt = true;
                    }
                    else if (visualStudioToolRequest && SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioToolTranscriptText(lineBuffer))
                    {
                        suppressedRecoverableToolTranscript = true;
                    }
                    else if (visualStudioToolRequest &&
                        SocketJackOpenAiChatAdapter.IsRecoverableVisualStudioPrematureClarificationText(lineBuffer) &&
                        SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(openAiBody, out _))
                    {
                        suppressedRecoverablePrematureClarification = true;
                    }
                    else if (visualStudioToolRequest &&
                        SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(lineBuffer))
                    {
                        suppressedRecoverableNoVisibleFallback = true;
                    }
                    else
                    {
                        ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lineBuffer, assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, visualStudioToolRequest, cancellationToken).ConfigureAwait(false);
                        if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                            return true;
                        if (lineResult == ChatStreamLineResult.SuppressedNoVisibleFallback)
                            suppressedRecoverableNoVisibleFallback = true;
                        wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
                    }
                }

                if (await TryWriteBufferedToolCallsAsync(context, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                    return true;
                if (!wroteContent &&
                    await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
                if ((suppressedRecoverableMalformedToolAttempt || suppressedRecoverableToolTranscript || suppressedRecoverablePrematureClarification || suppressedRecoverableNoVisibleFallback) &&
                    await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                if (!wroteContent && !suppressedRecoverableMalformedToolAttempt && !suppressedRecoverableToolTranscript && !suppressedRecoverablePrematureClarification && !suppressedRecoverableNoVisibleFallback)
                    wroteContent = await TryWriteCapturedChatStreamTextAsync(context, captured.ToString(), assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                if (!wroteContent)
                    wroteContent = await TryWriteNoVisibleAssistantRecoveryAsync(context, recoveryRequestBody, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);
                if (!wroteContent)
                    wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), cancellationToken).ConfigureAwait(false);
                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                return true;
            }

            if (responseStarted)
            {
                if (!wroteContent &&
                    await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                if (!wroteContent && lastForwardStatusCode == 0)
                    wroteContent = await TryWriteNoVisibleAssistantRecoveryAsync(context, recoveryRequestBody, assistantText, shape, completionId, created, model, cancellationToken).ConfigureAwait(false);

                if (!wroteContent)
                {
                    string fallbackMessage = string.IsNullOrWhiteSpace(lastForwardStatusMessage)
                        ? BuildNoVisibleAssistantTextFallback()
                        : SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(lastForwardStatusCode, lastForwardStatusMessage)
                            ? SocketJackOpenAiChatAdapter.BuildServerOfflineAssistantText(_options.ServerName)
                            : lastForwardStatusMessage;
                    if (lastForwardStatusCode != 0 && SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(lastForwardStatusCode, lastForwardStatusMessage))
                        this.TryOpenServerBrowserForOffline();
                    wroteContent = await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, fallbackMessage, cancellationToken).ConfigureAwait(false);
                }

                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, cancellationToken).ConfigureAwait(false);
                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string timeoutMessage = "The selected model server did not produce a response within " + effectiveTimeoutSeconds.ToString(CultureInfo.InvariantCulture) + " seconds, so the bridge ended this Copilot turn to avoid leaving Visual Studio waiting indefinitely.";
            try
            {
                if (!responseStarted)
                {
                    StartOpenAiSseResponse(context);
                    await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), CancellationToken.None).ConfigureAwait(false);
                }

                if (!wroteContent)
                    await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, timeoutMessage, CancellationToken.None).ConfigureAwait(false);
                await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (responseStarted)
            {
                try
                {
                    if (!wroteContent &&
                        await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, CancellationToken.None).ConfigureAwait(false))
                    {
                        return true;
                    }

                    if (!wroteContent)
                        await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, BuildNoVisibleAssistantTextFallback(), CancellationToken.None).ConfigureAwait(false);
                    await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText.VisibleText, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }

                return true;
            }

            return false;
        }
    }

    private async Task<bool> TryWriteNoVisibleAssistantRecoveryAsync(
        HttpContext context,
        byte[] originalRequestBody,
        SocketJackAssistantTextAccumulator assistantText,
        OpenAiProxyResponseShape shape,
        string completionId,
        long created,
        string model,
        CancellationToken cancellationToken)
    {
        JsonObject recoveryRequest;
        try
        {
            JsonObject originalRequest = JsonNode.Parse(originalRequestBody) as JsonObject ?? new JsonObject();
            recoveryRequest = SocketJackOpenAiChatAdapter.BuildNoVisibleAssistantRecoveryRequest(originalRequest, model);
        }
        catch (JsonException)
        {
            return false;
        }

        using var client = new HttpClient
        {
            BaseAddress = _options.ServerEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
        _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        recoveryCancellation.CancelAfter(DirectChatStreamMaxDuration);
        byte[] body = Encoding.UTF8.GetBytes(recoveryRequest.ToJsonString());
        foreach (string path in OpenAiChatForwardPaths)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri(path));
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new ByteArrayContent(body);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                Task<HttpResponseMessage> sendTask = client.SendAsync(request, HttpCompletionOption.ResponseContentRead, recoveryCancellation.Token);
                while (!sendTask.IsCompleted)
                {
                    Task completed = await Task.WhenAny(sendTask, Task.Delay(DirectChatStreamHeartbeatInterval, cancellationToken)).ConfigureAwait(false);
                    if (ReferenceEquals(completed, sendTask))
                        break;

                    await WriteOpenAiSseAsync(context, ": socketjack recovery keepalive\n\n", cancellationToken).ConfigureAwait(false);
                }

                using HttpResponseMessage response = await sendTask.ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string responseText = await response.Content.ReadAsStringAsync(recoveryCancellation.Token).ConfigureAwait(false);
                string visibleText = SocketJackOpenAiChatAdapter.ExtractAssistantText(responseText);
                if (string.IsNullOrWhiteSpace(visibleText) || SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(visibleText))
                    continue;

                return await WriteSyntheticAssistantDeltaAsync(context, assistantText, shape, completionId, created, model, visibleText, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return false;
    }

    private static async Task<HttpResponseMessage> SendOpenAiStreamingRequestAsync(HttpClient client, HttpRequestMessage request, HttpContext context, bool sendKeepalive, CancellationToken upstreamToken, CancellationToken cancellationToken)
    {
        Task<HttpResponseMessage> sendTask = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamToken);
        while (!sendTask.IsCompleted)
        {
            Task completed = await Task.WhenAny(sendTask, Task.Delay(DirectChatStreamHeartbeatInterval, upstreamToken)).ConfigureAwait(false);
            if (completed == sendTask)
                break;

            if (sendKeepalive && !cancellationToken.IsCancellationRequested)
                await WriteOpenAiSseAsync(context, ": socketjack keepalive\n\n", cancellationToken).ConfigureAwait(false);
        }

        return await sendTask.ConfigureAwait(false);
    }

    private static bool ShouldFallbackFromDirectChatStream(int statusCode, string detail)
    {
        if (statusCode == StatusCodes.Status404NotFound ||
            statusCode == StatusCodes.Status405MethodNotAllowed ||
            statusCode == StatusCodes.Status501NotImplemented)
        {
            return true;
        }

        string text = detail ?? "";
        return text.Contains("Cannot POST", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no route", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WriteSyntheticAssistantDeltaAsync(HttpContext context, SocketJackAssistantTextAccumulator assistantText, OpenAiProxyResponseShape shape, string completionId, long created, string model, string text, CancellationToken cancellationToken)
    {
        string content = assistantText.AcceptDeltaOrSnapshot(text);
        if (string.IsNullOrEmpty(content))
            return false;

        await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(
                    context,
                    shape,
                    completionId,
                    created,
                    model,
                    content), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> TryWriteBufferedToolCallsAsync(HttpContext context, SocketJackToolCallTextAccumulator toolCallText, OpenAiProxyResponseShape shape, string completionId, long created, string model, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles, CancellationToken cancellationToken)
    {
        if (!toolCallText.TryComplete(out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls))
        {
            return false;
        }

        await WriteOpenAiSseAsync(context, BuildOpenAiToolCallsSse(context, shape, completionId, created, model, toolCalls, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<bool> TryWriteVisualStudioMalformedRecoveryToolCallAsync(HttpContext context, byte[] openAiBody, OpenAiProxyResponseShape shape, string completionId, long created, string model, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles, CancellationToken cancellationToken)
    {
        if (!SocketJackOpenAiChatAdapter.TryBuildVisualStudioMalformedRecoveryToolCalls(openAiBody, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls))
            return false;

        if (!context.Response.HasStarted)
            StartOpenAiSseResponse(context);

        await WriteOpenAiSseAsync(context, BuildOpenAiToolCallsSse(context, shape, completionId, created, model, toolCalls, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryStreamOpenAiChatViaWebSocketOpenAiAsync(HttpContext context, byte[] openAiBody, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        try
        {
            ProxyResponse? response = await TryForwardOpenAiChatViaWebSocketOpenAiAsync(openAiBody, cancellationToken).ConfigureAwait(false);
            if (response == null)
                return false;

            if (response.StatusCode < 200 || response.StatusCode >= 300)
            {
                await WriteProxyResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
                return true;
            }

            string assistantText = "";
            try
            {
                assistantText = SocketJackOpenAiChatAdapter.ExtractAssistantText(Encoding.UTF8.GetString(response.Body));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                assistantText = "";
            }

            if (string.IsNullOrWhiteSpace(assistantText))
            {
                string visualStudioDefaultProjectPath = SocketJackOpenAiChatAdapter.FindLatestVisualStudioProjectPath(openAiBody);
                bool preferVisualStudioXamlCodeBehindForVbFiles = SocketJackOpenAiChatAdapter.ShouldPreferVisualStudioXamlCodeBehindForVbFiles(openAiBody);
                if (await TryWriteVisualStudioMalformedRecoveryToolCallAsync(context, openAiBody, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, cancellationToken).ConfigureAwait(false))
                    return true;

                assistantText = BuildNoVisibleAssistantTextFallback();
            }

            StartOpenAiSseResponse(context);
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamStartSse(context, shape, completionId, created, model), cancellationToken).ConfigureAwait(false);
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, assistantText), cancellationToken).ConfigureAwait(false);
            await FinishOpenAiSseAsync(context, shape, completionId, created, model, true, assistantText, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> TryStopRemoteChatStreamAsync(string streamId, string sessionId, string reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(streamId))
            return false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var client = new HttpClient
            {
                BaseAddress = _options.ServerEndpoint,
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SocketJack.CopilotMcpBridge/1.0");
            _options.ApplyAuthHeaders(client.DefaultRequestHeaders);

            var payload = new JsonObject
            {
                ["streamId"] = streamId,
                ["reason"] = reason
            };
            if (!string.IsNullOrWhiteSpace(sessionId))
                payload["sessionId"] = sessionId;

            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-stream/stop"));
            request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task ObserveCancelledReadAsync(Task<int>? pendingRead)
    {
        if (pendingRead == null || pendingRead.IsCompleted)
        {
            try
            {
                if (pendingRead != null)
                    await pendingRead.ConfigureAwait(false);
            }
            catch
            {
            }

            return;
        }

        try
        {
            Task completed = await Task.WhenAny(pendingRead, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            if (ReferenceEquals(completed, pendingRead))
                await pendingRead.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static string? GetDirectChatStreamTimeoutMessage(DateTimeOffset startedUtc, DateTimeOffset lastUpstreamDataUtc)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (DirectChatStreamMaxDuration > TimeSpan.Zero && now - startedUtc >= DirectChatStreamMaxDuration)
        {
            return "The selected model stream exceeded " + FormatTimeout(DirectChatStreamMaxDuration) + ", so the bridge ended this Copilot turn to avoid leaving Visual Studio waiting indefinitely.";
        }

        if (DirectChatStreamIdleTimeout > TimeSpan.Zero && now - lastUpstreamDataUtc >= DirectChatStreamIdleTimeout)
        {
            return "The selected model stream produced no upstream data for " + FormatTimeout(DirectChatStreamIdleTimeout) + ", so the bridge ended this Copilot turn to avoid leaving Visual Studio waiting indefinitely.";
        }

        return null;
    }

    private static string FormatTimeout(TimeSpan value)
    {
        if (value.TotalMinutes >= 1 && Math.Abs(value.TotalMinutes - Math.Round(value.TotalMinutes)) < 0.01)
            return ((int)Math.Round(value.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " minute" + (Math.Round(value.TotalMinutes) == 1 ? "" : "s");

        return ((int)Math.Round(value.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " second" + (Math.Round(value.TotalSeconds) == 1 ? "" : "s");
    }

    private static TimeSpan ReadPositiveEnvironmentTimeSpan(string name, TimeSpan fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;
    }

    private static (string SessionId, string StreamId) ReadChatStreamRequestIds(byte[] chatStreamBody)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(chatStreamBody);
            JsonElement root = document.RootElement;
            return (
                ReadJsonString(root, "sessionId"),
                ReadJsonString(root, "streamId"));
        }
        catch (JsonException)
        {
            return ("", "");
        }
    }

    private static string ReadJsonString(JsonElement root, string propertyName)
    {
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static async Task<string> ReadHttpResponsePreviewAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            text = text.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
            return text.Length <= 500 ? text : text.Substring(0, 500);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    private Uri SocketJackModelDiscoveryUri(string relativePath)
    {
        Uri endpoint = _options.PreferLocalWebChat ? _options.LocalWebChatEndpoint : _options.ServerEndpoint;
        string baseUri = endpoint.ToString();
        if (!baseUri.EndsWith("/", StringComparison.Ordinal))
            baseUri += "/";
        return new Uri(new Uri(baseUri, UriKind.Absolute), (relativePath ?? "").TrimStart('/'));
    }

    private async Task TryUploadVisualStudioReferenceFilesAsync(JsonObject chatStreamRequest, CancellationToken cancellationToken)
    {
        if (chatStreamRequest["files"] is not JsonArray files || files.Count == 0)
            return;

        string sessionId = chatStreamRequest["sessionId"]?.ToString() ?? "";
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        var uploadedOrInline = new JsonArray();
        foreach (JsonNode? node in files)
        {
            if (node is not JsonObject file)
                continue;

            JsonObject? uploaded = await TryUploadVisualStudioReferenceFileAsync(sessionId, file, cancellationToken).ConfigureAwait(false);
            uploadedOrInline.Add(uploaded ?? CloneJsonObject(file));
        }

        chatStreamRequest["files"] = uploadedOrInline;
    }

    private async Task<JsonObject?> TryUploadVisualStudioReferenceFileAsync(string sessionId, JsonObject file, CancellationToken cancellationToken)
    {
        JsonObject payload = BuildVisualStudioReferenceUploadPayload(sessionId, file);
        if (payload["text"] == null && payload["dataUrl"] == null)
            return null;

        byte[] body = Encoding.UTF8.GetBytes(payload.ToJsonString());

        JsonObject? direct = await TryUploadVisualStudioReferenceFileDirectAsync(body, cancellationToken).ConfigureAwait(false);
        if (direct != null)
            return direct;

        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/json"
            };
            ProxyResponse response = await _webChatClient.ForwardAsync("POST", "/api/chat-file", "", headers, body, cancellationToken).ConfigureAwait(false);
            return TryReadUploadedReferenceFile(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("Visual Studio reference upload via WebSocket failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            return null;
        }
    }

    private async Task<JsonObject?> TryUploadVisualStudioReferenceFileDirectAsync(byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri("api/chat-file"));
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.ParseAdd("application/json");

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return TryReadUploadedReferenceFile(new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/json", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), responseBody));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine("Visual Studio reference upload failed: " + SocketJackSecretRedactor.Redact(ex.Message));
            return null;
        }
    }

    private static JsonObject BuildVisualStudioReferenceUploadPayload(string sessionId, JsonObject file)
    {
        var payload = new JsonObject
        {
            ["sessionId"] = sessionId,
            ["name"] = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "name", "fileName", "filename"),
            ["type"] = string.IsNullOrWhiteSpace(SocketJackOpenAiChatAdapter.FirstReferenceString(file, "type", "mimeType", "contentType"))
                ? "text/plain"
                : SocketJackOpenAiChatAdapter.FirstReferenceString(file, "type", "mimeType", "contentType"),
            ["relativePath"] = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "relativePath", "path"),
            ["asFile"] = true
        };

        string dataUrl = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "dataUrl", "dataURL");
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            payload["dataUrl"] = dataUrl;
            return payload;
        }

        string text = SocketJackOpenAiChatAdapter.FirstReferenceString(file, "text", "content", "contents", "source", "body");
        if (!string.IsNullOrEmpty(text))
            payload["text"] = text;

        return payload;
    }

    private static JsonObject? TryReadUploadedReferenceFile(ProxyResponse response)
    {
        if (response.StatusCode < 200 || response.StatusCode >= 300 || response.Body.Length == 0)
            return null;

        try
        {
            JsonObject? root = JsonNode.Parse(response.Body) as JsonObject;
            if (root == null)
                return null;

            if (root["ok"] is JsonValue okValue && okValue.TryGetValue(out bool ok) && !ok)
                return null;

            if (root["file"] is JsonObject file)
                return CloneJsonObject(file);

            if (root["files"] is JsonArray files && files.Count > 0 && files[0] is JsonObject first)
                return CloneJsonObject(first);
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static JsonObject CloneJsonObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? new JsonObject();
    }

    private static JsonObject BuildWebChatEnvelope(string id, string path, byte[] body)
    {
        return new JsonObject
        {
            ["type"] = "request",
            ["id"] = id,
            ["method"] = "POST",
            ["path"] = path,
            ["queryString"] = "",
            ["headers"] = new JsonObject
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "application/x-ndjson, application/json, */*"
            },
            ["bodyBase64"] = Convert.ToBase64String(body)
        };
    }

    private readonly record struct ChatStreamBinaryWriteResult(string LineBuffer, bool WroteContent, bool FinalizedToolCalls);

    private static async Task<ChatStreamBinaryWriteResult> WriteChatStreamBinaryAsOpenAiSseAsync(HttpContext context, string requestId, byte[] messageBytes, string lineBuffer, SocketJackAssistantTextAccumulator assistantText, SocketJackToolCallTextAccumulator toolCallText, OpenAiProxyResponseShape shape, string completionId, long created, string model, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles, CancellationToken cancellationToken)
    {
        if (messageBytes.Length <= 32)
            return new ChatStreamBinaryWriteResult(lineBuffer, false, false);

        string prefix = Encoding.ASCII.GetString(messageBytes, 0, 32).Trim();
        if (!string.Equals(prefix, requestId, StringComparison.Ordinal))
            return new ChatStreamBinaryWriteResult(lineBuffer, false, false);

        string text = lineBuffer + Encoding.UTF8.GetString(messageBytes, 32, messageBytes.Length - 32);
        if (!ContainsLineBreak(text) && !IsHttpChunkSizeLine(text) && LooksLikeImmediateRawTextChunk(text))
        {
            string content = assistantText.AcceptDeltaOrSnapshot(text);
            if (string.IsNullOrEmpty(content))
                return new ChatStreamBinaryWriteResult("", false, false);

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return new ChatStreamBinaryWriteResult("", true, false);
        }

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string remainder = lines.Length == 0 ? "" : lines[^1];
        bool wroteContent = false;
        for (int i = 0; i < lines.Length - 1; i++)
        {
            ChatStreamLineResult lineResult = await WriteChatStreamLineAsOpenAiSseAsync(context, lines[i], assistantText, toolCallText, shape, completionId, created, model, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles, false, cancellationToken).ConfigureAwait(false);
            if (lineResult == ChatStreamLineResult.ToolCallsFinalized)
                return new ChatStreamBinaryWriteResult("", wroteContent, true);
            wroteContent = wroteContent || lineResult == ChatStreamLineResult.Content;
        }

        return new ChatStreamBinaryWriteResult(remainder, wroteContent, false);
    }

    private enum ChatStreamLineResult
    {
        Continue,
        Content,
        Done,
        SuppressedNoVisibleFallback,
        ToolCallsFinalized
    }

    private static async Task<ChatStreamLineResult> WriteChatStreamLineAsOpenAiSseAsync(HttpContext context, string line, SocketJackAssistantTextAccumulator assistantText, SocketJackToolCallTextAccumulator toolCallText, OpenAiProxyResponseShape shape, string completionId, long created, string model, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles, bool suppressNoVisibleAssistantFallback, CancellationToken cancellationToken)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return ChatStreamLineResult.Continue;

        if (IsHttpChunkSizeLine(trimmed))
            return ChatStreamLineResult.Continue;

        if (IsSseControlLine(trimmed))
            return ChatStreamLineResult.Continue;

        if (trimmed.Equals("[DONE]", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("data: [DONE]", StringComparison.OrdinalIgnoreCase))
        {
            return ChatStreamLineResult.Done;
        }

        JsonObject? evt = ParseJsonObject(trimmed);
        string type = evt?["type"]?.ToString() ?? "";
        if (type.Equals("done", StringComparison.OrdinalIgnoreCase))
            return ChatStreamLineResult.Done;

        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            string error = evt?["content"]?.ToString() ??
                evt?["body"]?.ToString() ??
                evt?["error"]?.ToString() ??
                evt?["message"]?.ToString() ??
                "The selected model stream returned an error.";
            if (SocketJackOpenAiChatAdapter.IsNoAssistantTextError(error))
                return ChatStreamLineResult.Done;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, "\n\nThe model returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(error)), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Content;
        }

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            string visibleUpdate = evt == null ? "" : SocketJackOpenAiChatAdapter.BuildVisibleChatStreamUpdate(evt);
            if (!string.IsNullOrEmpty(visibleUpdate))
            {
                await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, visibleUpdate), cancellationToken).ConfigureAwait(false);
                return ChatStreamLineResult.Content;
            }

            return ChatStreamLineResult.Continue;
        }

        if (toolCallText.TryParseNativeOpenAiToolCalls(line, out IReadOnlyList<SocketJackOpenAiToolCall> nativeToolCalls) &&
            nativeToolCalls.Count > 0)
        {
            await WriteOpenAiSseAsync(context, BuildOpenAiToolCallsSse(context, shape, completionId, created, model, nativeToolCalls, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.ToolCallsFinalized;
        }

        string rawDelta = SocketJackOpenAiChatAdapter.ExtractAssistantRawDeltaText(line);
        if (!string.IsNullOrEmpty(rawDelta) && toolCallText.Enabled)
        {
            SocketJackToolCallConsumeResult toolResult = toolCallText.Accept(rawDelta, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, out string passThrough);
            if (toolResult == SocketJackToolCallConsumeResult.ToolCalls)
            {
                await WriteOpenAiSseAsync(context, BuildOpenAiToolCallsSse(context, shape, completionId, created, model, toolCalls, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles), cancellationToken).ConfigureAwait(false);
                return ChatStreamLineResult.ToolCallsFinalized;
            }

            if (toolResult == SocketJackToolCallConsumeResult.Buffered)
                return ChatStreamLineResult.Continue;
        }

        string deltaText = SocketJackOpenAiChatAdapter.ExtractAssistantDeltaText(line);
        if (suppressNoVisibleAssistantFallback &&
            SocketJackOpenAiChatAdapter.IsNoVisibleAssistantFallbackText(deltaText))
        {
            return ChatStreamLineResult.SuppressedNoVisibleFallback;
        }

        string content;
        try
        {
            content = assistantText.AcceptDeltaOrSnapshot(deltaText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, "\n\n**Stream parse error:** " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message)), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Done;
        }

        if (!string.IsNullOrEmpty(content))
        {
            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return ChatStreamLineResult.Content;
        }

        return ChatStreamLineResult.Continue;
    }

    private static bool ContainsLineBreak(string text)
    {
        return text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0;
    }

    private static bool IsHttpChunkSizeLine(string text)
    {
        string value = (text ?? "").Trim();
        if (value.Length == 0)
            return false;

        int separator = value.IndexOf(';');
        if (separator >= 0)
            value = value.Substring(0, separator).Trim();

        if (value.Length == 0 || value.Length > 8)
            return false;

        bool hasDigit = false;
        bool hasHexLetter = false;
        foreach (char ch in value)
        {
            hasDigit = hasDigit || (ch >= '0' && ch <= '9');
            hasHexLetter = hasHexLetter ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            bool hex = (ch >= '0' && ch <= '9') ||
                (ch >= 'a' && ch <= 'f') ||
                (ch >= 'A' && ch <= 'F');
            if (!hex)
                return false;
        }

        return hasDigit && hasHexLetter;
    }

    private static bool IsSseControlLine(string trimmed)
    {
        if (string.IsNullOrWhiteSpace(trimmed))
            return true;

        return trimmed.StartsWith(":", StringComparison.Ordinal) ||
            trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("retry:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeImmediateRawTextChunk(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0)
            return true;

        return !trimmed.StartsWith("{", StringComparison.Ordinal) &&
            !trimmed.StartsWith("[", StringComparison.Ordinal) &&
            !trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TryWriteCapturedChatStreamTextAsync(HttpContext context, string captured, SocketJackAssistantTextAccumulator assistantText, OpenAiProxyResponseShape shape, string completionId, long created, string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(captured))
            return false;

        try
        {
            string content = SocketJackOpenAiChatAdapter.ExtractAssistantText(captured);
            content = assistantText.AcceptDeltaOrSnapshot(content);
            if (string.IsNullOrWhiteSpace(content))
                return false;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, content), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (SocketJackOpenAiChatAdapter.IsNoAssistantTextError(ex.Message))
                return false;

            await WriteOpenAiSseAsync(context, BuildOpenAiStreamDeltaSse(context, shape, completionId, created, model, "\n\nThe model returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message)), cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static string BuildNoVisibleAssistantTextFallback()
    {
        return SocketJackOpenAiChatAdapter.BuildNoVisibleAssistantTextFallback();
    }

    private static void StartOpenAiSseResponse(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers["Cache-Control"] = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
    }

    private static async Task WriteOpenAiSseAsync(HttpContext context, string sse, CancellationToken cancellationToken)
    {
        await context.Response.WriteAsync(sse, cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string BuildOpenAiStreamStartSse(HttpContext context, OpenAiProxyResponseShape shape, string id, long created, string model)
    {
        if (shape != OpenAiProxyResponseShape.Responses)
            return SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(id, created, model, null, true, null);

        MarkResponseLifecycleStarted(context);
        return SocketJackOpenAiChatAdapter.BuildResponseLifecycleStartSse(id, created, model);
    }

    private static string BuildOpenAiStreamDeltaSse(HttpContext context, OpenAiProxyResponseShape shape, string id, long created, string model, string content)
    {
        if (shape != OpenAiProxyResponseShape.Responses)
            return SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(id, created, model, content, false, null);

        var builder = new StringBuilder();
        if (!IsResponseLifecycleStarted(context))
        {
            MarkResponseLifecycleStarted(context);
            builder.Append(SocketJackOpenAiChatAdapter.BuildResponseLifecycleStartSse(id, created, model));
        }

        if (!IsResponseTextStarted(context))
        {
            MarkResponseTextStarted(context);
            builder.Append(SocketJackOpenAiChatAdapter.BuildResponseMessageStartSse(id));
        }

        builder.Append(SocketJackOpenAiChatAdapter.BuildResponseTextDeltaSse(id, content));
        return builder.ToString();
    }

    private static string BuildOpenAiToolCallsSse(HttpContext context, OpenAiProxyResponseShape shape, string id, long created, string model, IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, string visualStudioDefaultProjectPath = "", bool preferVisualStudioXamlCodeBehindForVbFiles = false)
    {
        IReadOnlyList<SocketJackOpenAiToolCall> normalizedToolCalls = SocketJackOpenAiChatAdapter.NormalizeVisualStudioToolCalls(toolCalls, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
        normalizedToolCalls = SocketJackOpenAiChatAdapter.AssignUniqueVisualStudioToolCallIds(normalizedToolCalls, id);
        if (shape != OpenAiProxyResponseShape.Responses)
            return SocketJackOpenAiChatAdapter.BuildStreamingToolCallsSse(id, created, model, normalizedToolCalls);

        bool includeLifecycle = !IsResponseLifecycleStarted(context);
        MarkResponseLifecycleStarted(context);
        return SocketJackOpenAiChatAdapter.BuildResponseToolCallsSse(id, created, model, normalizedToolCalls, includeLifecycle);
    }

    private static async Task FinishOpenAiSseAsync(HttpContext context, OpenAiProxyResponseShape shape, string completionId, long created, string model, bool responseStarted, string assistantText, CancellationToken cancellationToken)
    {
        if (!responseStarted)
            StartOpenAiSseResponse(context);

        string finishSse;
        if (shape == OpenAiProxyResponseShape.Responses)
        {
            var builder = new StringBuilder();
            if (!IsResponseLifecycleStarted(context))
            {
                MarkResponseLifecycleStarted(context);
                builder.Append(SocketJackOpenAiChatAdapter.BuildResponseLifecycleStartSse(completionId, created, model));
            }

            if (!IsResponseTextStarted(context))
            {
                MarkResponseTextStarted(context);
                builder.Append(SocketJackOpenAiChatAdapter.BuildResponseMessageStartSse(completionId));
            }

            builder.Append(SocketJackOpenAiChatAdapter.BuildResponseStreamFinishSse(completionId, created, model, assistantText));
            finishSse = builder.ToString();
        }
        else
        {
            finishSse = SocketJackOpenAiChatAdapter.BuildStreamingDeltaSse(completionId, created, model, null, false, "stop");
        }

        await WriteOpenAiSseAsync(context, finishSse, cancellationToken).ConfigureAwait(false);
        await WriteOpenAiSseAsync(context, SocketJackOpenAiChatAdapter.DoneSse, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsResponseLifecycleStarted(HttpContext context)
    {
        return context.Items.ContainsKey(ResponseLifecycleStartedItemKey);
    }

    private static void MarkResponseLifecycleStarted(HttpContext context)
    {
        context.Items[ResponseLifecycleStartedItemKey] = true;
    }

    private static bool IsResponseTextStarted(HttpContext context)
    {
        return context.Items.ContainsKey(ResponseTextStartedItemKey);
    }

    private static void MarkResponseTextStarted(HttpContext context)
    {
        context.Items[ResponseTextStartedItemKey] = true;
    }

    private static JsonObject? ParseJsonObject(string text)
    {
        string payload = (text ?? "").Trim();
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            payload = payload.Substring(5);
            if (payload.StartsWith(" ", StringComparison.Ordinal))
                payload = payload.Substring(1);
            payload = payload.Trim();
        }

        try
        {
            return JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node == null)
            return null;
        if (node is JsonValue value && value.TryGetValue(out int intValue))
            return intValue;
        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }

    private async Task<ProxyResponse> ForwardOpenAiChatViaWebChatAsync(byte[] openAiBody, OpenAiProxyResponseShape shape, CancellationToken cancellationToken)
    {
        JsonObject openAiRequest;
        try
        {
            openAiRequest = JsonNode.Parse(openAiBody) as JsonObject ?? new JsonObject();
        }
        catch (JsonException ex)
        {
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status400BadRequest, "Invalid OpenAI chat request JSON: " + ex.Message);
        }

        JsonObject chatStreamRequest = SocketJackOpenAiChatAdapter.BuildChatStreamRequest(openAiRequest, _options.ModelId);
        string canonicalModel = await ResolveCanonicalModelIdAsync(ChooseSelectedServerModel(chatStreamRequest["model"]?.ToString()), cancellationToken).ConfigureAwait(false);
        chatStreamRequest["model"] = canonicalModel;
        if (shape == OpenAiProxyResponseShape.ChatCompletions || shape == OpenAiProxyResponseShape.Responses)
        {
            JsonObject directOpenAiRequest = SocketJackOpenAiChatAdapter.BuildOpenAiChatCompletionsForwardRequest(openAiRequest, canonicalModel);
            byte[] directOpenAiBody = Encoding.UTF8.GetBytes(directOpenAiRequest.ToJsonString());
            ProxyResponse? direct = await TryForwardOpenAiChatViaDirectOpenAiAsync(directOpenAiBody, cancellationToken).ConfigureAwait(false);
            direct ??= await TryForwardOpenAiChatViaWebSocketOpenAiAsync(directOpenAiBody, cancellationToken).ConfigureAwait(false);
            if (direct != null)
            {
                if (shape == OpenAiProxyResponseShape.ChatCompletions)
                    return direct;

                if (shape == OpenAiProxyResponseShape.Responses)
                {
                    string directAssistantText = "";
                    try
                    {
                        directAssistantText = SocketJackOpenAiChatAdapter.ExtractAssistantText(Encoding.UTF8.GetString(direct.Body));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        directAssistantText = "";
                    }

                    if (!string.IsNullOrWhiteSpace(directAssistantText))
                    {
                        return SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest)
                            ? SocketJackOpenAiChatAdapter.BuildStreamingResponseResponse(openAiRequest, directAssistantText, canonicalModel)
                            : SocketJackOpenAiChatAdapter.BuildResponseResponse(openAiRequest, directAssistantText, canonicalModel);
                    }
                }
            }
        }

        await TryUploadVisualStudioReferenceFilesAsync(chatStreamRequest, cancellationToken).ConfigureAwait(false);
        byte[] chatStreamBody = Encoding.UTF8.GetBytes(chatStreamRequest.ToJsonString());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/json",
            ["Accept"] = "application/x-ndjson, application/json, */*"
        };

        ProxyResponse response;
        try
        {
            response = await _webChatClient.ForwardAsync("POST", "/api/chat-stream", "", headers, chatStreamBody, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(StatusCodes.Status502BadGateway, ex.Message))
            {
                return this.BuildServerOfflineResponse(openAiRequest, shape, canonicalModel);
            }

            throw;
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            string detail = response.Body.Length == 0 ? response.ReasonPhrase : Encoding.UTF8.GetString(response.Body);
            if (SocketJackOpenAiChatAdapter.LooksLikeOfflineServer(response.StatusCode, detail))
            {
                return this.BuildServerOfflineResponse(openAiRequest, shape, canonicalModel);
            }

            int status = response.StatusCode == StatusCodes.Status404NotFound ? StatusCodes.Status502BadGateway : response.StatusCode;
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(status, "The selected model stream adapter failed: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(detail));
        }

        string eventsText = Encoding.UTF8.GetString(response.Body);
        string assistantText;
        try
        {
            assistantText = SocketJackOpenAiChatAdapter.ExtractAssistantText(eventsText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return SocketJackOpenAiChatAdapter.BuildErrorResponse(StatusCodes.Status502BadGateway, "The selected model stream returned an error: " + SocketJackOpenAiChatAdapter.ToSafeMarkdownStatus(ex.Message));
        }

        if (string.IsNullOrWhiteSpace(assistantText))
            assistantText = BuildNoVisibleAssistantTextFallback();

        if (shape == OpenAiProxyResponseShape.Responses)
        {
            return SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest)
                ? SocketJackOpenAiChatAdapter.BuildStreamingResponseResponse(openAiRequest, assistantText, canonicalModel)
                : SocketJackOpenAiChatAdapter.BuildResponseResponse(openAiRequest, assistantText, canonicalModel);
        }

        return SocketJackOpenAiChatAdapter.WantsStreaming(openAiRequest)
            ? SocketJackOpenAiChatAdapter.BuildStreamingChatResponse(openAiRequest, assistantText, canonicalModel)
            : SocketJackOpenAiChatAdapter.BuildChatResponse(openAiRequest, assistantText, canonicalModel);
    }

    private async Task<string> ResolveCanonicalModelIdAsync(string requestedModel, CancellationToken cancellationToken)
    {
        requestedModel = string.IsNullOrWhiteSpace(requestedModel) ? _options.ModelId : requestedModel.Trim();
        if (string.IsNullOrWhiteSpace(requestedModel))
            return "socketjack-model";
        if (IsSocketJackProxyEndpoint(_options.ServerEndpoint))
            return requestedModel;

        foreach (string path in new[] { "api/models", "v1/models" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SocketJackModelDiscoveryUri(path));
                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                JsonObject? root = JsonNode.Parse(json) as JsonObject;
                JsonArray? models = ReadModelsArray(root);
                if (models == null)
                    continue;

                if (TryResolveModelIdFromModels(models, requestedModel, requireSelectable: true, out string resolved))
                    return resolved;

                string selectedModel = _options.ModelId?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(selectedModel) &&
                    !string.Equals(selectedModel, requestedModel, StringComparison.OrdinalIgnoreCase) &&
                    TryResolveModelIdFromModels(models, selectedModel, requireSelectable: true, out resolved))
                {
                    return resolved;
                }

                if (TryFindBestSelectableModelId(models, out resolved))
                    return resolved;

                if (TryResolveModelIdFromModels(models, requestedModel, requireSelectable: false, out resolved))
                    return resolved;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return requestedModel;
    }

    private static bool IsSocketJackProxyEndpoint(Uri endpoint)
    {
        string path = endpoint?.AbsolutePath ?? "";
        return path.StartsWith("/proxy/", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/proxy", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray? ReadModelsArray(JsonObject? root)
    {
        if (root == null)
            return null;

        return root["models"] as JsonArray ?? root["data"] as JsonArray;
    }

    private static bool TryResolveModelIdFromModels(JsonArray models, string requestedModel, bool requireSelectable, out string resolved)
    {
        resolved = "";
        string normalizedRequest = NormalizeModelIdForMatch(requestedModel);
        foreach (JsonNode? node in models)
        {
            if (node is not JsonObject model)
                continue;

            string id = model["id"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (requireSelectable && !IsSelectableProxyModel(model))
                continue;

            if (string.Equals(id, requestedModel, StringComparison.OrdinalIgnoreCase))
            {
                resolved = id;
                return true;
            }
        }

        foreach (JsonNode? node in models)
        {
            if (node is not JsonObject model)
                continue;

            string id = model["id"]?.ToString() ?? "";
            string normalizedCandidate = NormalizeModelIdForMatch(id);
            if (normalizedCandidate.Length == 0)
                continue;
            if (requireSelectable && !IsSelectableProxyModel(model))
                continue;

            if (normalizedCandidate.Equals(normalizedRequest, StringComparison.Ordinal) ||
                normalizedCandidate.Contains(normalizedRequest, StringComparison.Ordinal) ||
                normalizedRequest.Contains(normalizedCandidate, StringComparison.Ordinal))
            {
                resolved = id;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindBestSelectableModelId(JsonArray models, out string resolved)
    {
        resolved = "";
        int bestScore = int.MinValue;
        foreach (JsonNode? node in models)
        {
            if (node is not JsonObject model)
                continue;

            string id = FirstProxyModelString(model, "id", "key", "model", "name", "modelId");
            if (string.IsNullOrWhiteSpace(id) || !IsSelectableProxyModel(model))
                continue;

            int score = ScoreProxyModelForSelection(model, id);
            if (score > bestScore)
            {
                bestScore = score;
                resolved = id;
            }
        }

        return !string.IsNullOrWhiteSpace(resolved);
    }

    private static int ScoreProxyModelForSelection(JsonObject model, string id)
    {
        int score = 0;
        if (ReadProxyModelBool(model, "isLoaded", "loaded", "selected", "active") == true || ProxyModelHasLoadedInstances(model))
            score += 1000;
        if (ReadProxyModelBool(model, "supportsTools", "supports_tools", "tools", "toolCalling", "tool_calling", "functionCalling", "trained_for_tool_use") == true)
            score += 500;
        if (ReadProxyModelBool(model, "enabled", "isEnabled", "available", "isAvailable") != false)
            score += 200;
        if (ReadProxyModelBool(model, "runtime_load", "runtimeLoad", "runtimeLoadEnabled", "canLoad") == true)
            score += 100;
        if (ReadProxyModelBool(model, "dynamicLoadEnabled", "serverDynamicLoadingEnabled", "web_chat_dynamic_load_enabled", "webChatDynamicLoadEnabled", "web_chat_model_load_api_enabled") == true)
            score += 80;
        if (ReadProxyModelBool(model, "chat_completion", "chat", "chatCompletion") == true || Regex.IsMatch(id, @"qwen|llama|mistral|gemma|phi|claude|opus|instruct|chat|gpt", RegexOptions.IgnoreCase))
            score += 40;
        if (ReadProxyModelBool(model, "supportsImageGeneration", "imageGeneration", "generatesImages") == true ||
            ReadProxyModelBool(model, "supportsVideoGeneration", "videoGeneration", "generatesVideo") == true ||
            ReadProxyModelBool(model, "supportsAudioGeneration", "audioGeneration", "generatesAudio") == true)
            score -= 300;
        return score;
    }

    private static bool IsSelectableProxyModel(JsonObject model)
    {
        if (ReadProxyModelBool(model, "disabled", "isDisabled") == true)
            return false;

        bool loaded = ReadProxyModelBool(model, "isLoaded", "loaded", "selected", "active") == true || ProxyModelHasLoadedInstances(model);
        string disabledReason = FirstProxyModelString(model, "disabledReason", "disabled_reason", "load_disabled_reason", "loadDisabledReason", "web_chat_load_disabled_reason", "webChatLoadDisabledReason");
        if (!loaded && !string.IsNullOrWhiteSpace(disabledReason))
            return false;

        bool enabled = ReadProxyModelBool(model, "enabled", "isEnabled", "available", "isAvailable") != false;
        bool loadable =
            ReadProxyModelBool(model, "runtime_load", "runtimeLoad", "runtimeLoadEnabled", "canLoad") == true ||
            ReadProxyModelBool(model, "dynamicLoadEnabled", "serverDynamicLoadingEnabled", "web_chat_dynamic_load_enabled", "webChatDynamicLoadEnabled", "web_chat_model_load_api_enabled") == true;
        return enabled && (loaded || loadable);
    }

    private static bool? ReadProxyModelBool(JsonObject model, params string[] names)
    {
        foreach (string name in names)
        {
            if (TryReadProxyBool(model[name], out bool value))
                return value;
        }

        if (model["capabilities"] is JsonObject capabilities)
        {
            foreach (string name in names)
            {
                if (TryReadProxyBool(capabilities[name], out bool value))
                    return value;
            }
        }

        return null;
    }

    private static bool TryReadProxyBool(JsonNode? node, out bool value)
    {
        value = false;
        if (node is not JsonValue jsonValue)
            return false;
        if (jsonValue.TryGetValue(out bool boolValue))
        {
            value = boolValue;
            return true;
        }

        return bool.TryParse(node.ToString(), out value);
    }

    private static bool ProxyModelHasLoadedInstances(JsonObject model) =>
        model["loaded_instances"] is JsonArray instances && instances.Count > 0;

    private static string FirstProxyModelString(JsonObject model, params string[] names)
    {
        foreach (string name in names)
        {
            string value = model[name]?.ToString()?.Trim() ?? "";
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("null", StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return "";
    }

    private async Task<ProxyResponse?> TryForwardOpenAiChatViaDirectOpenAiAsync(byte[] openAiBody, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string path in OpenAiChatForwardPaths)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, SocketJackModelDiscoveryUri(path));
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Content = new ByteArrayContent(openAiBody);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                byte[] body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/json", FlattenHeaders(response), body);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private async Task<ProxyResponse?> TryForwardOpenAiChatViaWebSocketOpenAiAsync(byte[] openAiBody, CancellationToken cancellationToken)
    {
        try
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Content-Type"] = "application/json",
                ["Accept"] = "text/event-stream, application/json, */*"
            };
            foreach (string path in OpenAiChatForwardPaths)
            {
                ProxyResponse response = await _webChatClient.ForwardAsync("POST", "/" + path.TrimStart('/'), "", headers, openAiBody, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode >= 200 && response.StatusCode < 300)
                    return response;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        return null;
    }

    private static readonly string[] OpenAiChatForwardPaths =
    {
        "api/model-runtime/v1/chat/completions",
        "v1/chat/completions"
    };

    private string ChooseSelectedServerModel(string? requestedModel)
    {
        requestedModel = requestedModel?.Trim() ?? "";
        string selectedModel = _options.ModelId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(requestedModel))
            return string.IsNullOrWhiteSpace(selectedModel) ? "socketjack-model" : selectedModel;

        if (!string.IsNullOrWhiteSpace(selectedModel) && IsCopilotPlaceholderModelId(requestedModel))
            return selectedModel;

        return requestedModel;
    }

    private static bool IsCopilotPlaceholderModelId(string model)
    {
        string normalized = NormalizeModelIdForMatch(model);
        return normalized.Length == 0 ||
            normalized is "model" or "default" or "copilot" or "ollama" or "lmstudio" or "socketjackmodel" or "openai" or
                "gpt4" or "gpt4o" or "gpt41" or "gpt5" or "gpt5mini" or "gpt5nano" or "o1" or "o3" or "o3mini" or "o4mini";
    }

    private async Task<bool> IsRuntimeModelLoadedAsync(string model, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        foreach (string path in new[] { "api/model-runtime/models", "api/models" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SocketJackModelDiscoveryUri(path));
                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;

                string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                JsonNode? root = JsonNode.Parse(json);
                if (RuntimeModelNodeContainsLoaded(root, model))
                    return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }
        }

        return false;
    }

    private static bool RuntimeModelNodeContainsLoaded(JsonNode? node, string requestedModel)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? child in array)
            {
                if (RuntimeModelNodeContainsLoaded(child, requestedModel))
                    return true;
            }
            return false;
        }

        if (node is not JsonObject obj)
            return false;

        if (RuntimeModelObjectMatches(obj, requestedModel) && RuntimeModelObjectIsLoaded(obj))
            return true;

        foreach (string property in new[] { "models", "data", "items", "availableModels" })
        {
            if (obj[property] != null && RuntimeModelNodeContainsLoaded(obj[property], requestedModel))
                return true;
        }

        return false;
    }

    private static bool RuntimeModelObjectMatches(JsonObject obj, string requestedModel)
    {
        string normalizedRequest = NormalizeModelIdForMatch(requestedModel);
        foreach (string property in new[] { "id", "key", "model", "name", "display_name", "displayName", "selected_variant", "selectedVariant" })
        {
            string value = obj[property]?.ToString() ?? "";
            if (RuntimeModelIdMatches(value, requestedModel, normalizedRequest))
                return true;
        }

        foreach (string property in new[] { "aliases", "variants" })
        {
            if (obj[property] is not JsonArray array)
                continue;

            foreach (JsonNode? alias in array)
            {
                if (RuntimeModelIdMatches(alias?.ToString() ?? "", requestedModel, normalizedRequest))
                    return true;
            }
        }

        return false;
    }

    private static bool RuntimeModelIdMatches(string candidate, string requestedModel, string normalizedRequest)
    {
        candidate = (candidate ?? "").Trim();
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(requestedModel))
            return false;

        if (string.Equals(candidate, requestedModel, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(candidate), Path.GetFileName(requestedModel), StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(candidate), Path.GetFileNameWithoutExtension(requestedModel), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedCandidate = NormalizeModelIdForMatch(candidate);
        if (normalizedCandidate.Length == 0 || normalizedRequest.Length == 0)
            return false;

        return normalizedCandidate.Equals(normalizedRequest, StringComparison.Ordinal) ||
            normalizedCandidate.Contains(normalizedRequest, StringComparison.Ordinal) ||
            normalizedRequest.Contains(normalizedCandidate, StringComparison.Ordinal);
    }

    private static bool RuntimeModelObjectIsLoaded(JsonObject obj)
    {
        foreach (string property in new[] { "loaded_instances", "loadedInstances", "loaded_models", "loadedModels" })
        {
            JsonNode? value = obj[property];
            if (value is JsonArray array && array.Count > 0)
                return true;
            if (value is JsonObject)
                return true;
            if (value is JsonValue scalar && ReadJsonBoolOrNonEmptyString(scalar))
                return true;
        }

        foreach (string property in new[] { "loaded_instance_count", "loadedInstanceCount", "loaded_count", "loadedCount" })
        {
            string text = obj[property]?.ToString() ?? "";
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) && count > 0)
                return true;
        }

        foreach (string property in new[] { "isLoaded", "loaded", "active", "ready" })
        {
            if (obj[property] is JsonValue scalar && ReadJsonBoolOrNonEmptyString(scalar))
                return true;
        }

        string status = obj["status"]?.ToString() ?? "";
        return status.Contains("loaded", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("ready", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadJsonBoolOrNonEmptyString(JsonValue value)
    {
        if (value.TryGetValue(out bool boolValue))
            return boolValue;
        string text = value.ToString();
        return !string.IsNullOrWhiteSpace(text) &&
            !text.Equals("false", StringComparison.OrdinalIgnoreCase) &&
            !text.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeModelIdForMatch(string value)
    {
        var builder = new StringBuilder();
        foreach (char ch in (value ?? "").ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
        }

        string normalized = builder.ToString();
        return normalized.EndsWith("gguf", StringComparison.Ordinal) ? normalized.Substring(0, normalized.Length - 4) : normalized;
    }

    private async Task<ProxyResponse?> TryForwardDirectAsync(HttpRequest source, string path, byte[] body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(source.Method), BuildDirectUri(path, source.QueryString.Value));
            CopyHeaders(source, request);
            if (body.Length > 0 || RequiresBody(source.Method))
                request.Content = new ByteArrayContent(body);

            if (request.Content != null)
            {
                foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in source.Headers)
                {
                    if (!IsContentHeader(header.Key))
                        continue;
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return new ProxyResponse((int)response.StatusCode, response.ReasonPhrase ?? "", response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream", FlattenHeaders(response), responseBody);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private Uri BuildDirectUri(string path, string? query)
    {
        string relative = path.TrimStart('/');
        if (!string.IsNullOrEmpty(query))
            relative += query;
        return new Uri(_options.ServerEndpoint, relative);
    }

    private static bool ShouldFallback(int statusCode)
    {
        return statusCode == StatusCodes.Status404NotFound ||
            statusCode == StatusCodes.Status502BadGateway ||
            statusCode == StatusCodes.Status503ServiceUnavailable ||
            statusCode == StatusCodes.Status504GatewayTimeout ||
            statusCode >= 500;
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.Body == null)
            return Array.Empty<byte>();
        using var memory = new MemoryStream();
        await request.Body.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
        return memory.ToArray();
    }

    private static void CopyHeaders(HttpRequest source, HttpRequestMessage target)
    {
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in source.Headers)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                IsContentHeader(header.Key))
            {
                continue;
            }

            target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }
    }

    private static bool RequiresBody(string method)
    {
        return method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
            method.Equals("PATCH", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsContentHeader(string header)
    {
        return header.StartsWith("Content-", StringComparison.OrdinalIgnoreCase);
    }

    private static IDictionary<string, string> FlattenHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            headers[header.Key] = string.Join(", ", header.Value);
        return headers;
    }

    private static async Task WriteProxyResponseAsync(HttpContext context, ProxyResponse response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        foreach (KeyValuePair<string, string> header in response.Headers)
        {
            if (header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            context.Response.Headers[header.Key] = header.Value;
        }

        if (!string.IsNullOrWhiteSpace(response.ContentType))
            context.Response.ContentType = response.ContentType;

        if (response.Body.Length > 0)
            await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class SocketJackWebChatApiClient
{
    private readonly CopilotBridgeOptions _options;

    public SocketJackWebChatApiClient(CopilotBridgeOptions options)
    {
        _options = options;
    }

    public static Uri BuildWebSocketUri(Uri endpoint)
    {
        UriBuilder builder = new(endpoint)
        {
            Scheme = endpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? "wss" : "ws",
            Path = endpoint.AbsolutePath.TrimEnd('/') + "/api/web-chat/ws",
            Query = ""
        };
        return builder.Uri;
    }

    public async Task<ProxyResponse> ForwardAsync(HttpRequest source, string path, byte[] body, CancellationToken cancellationToken)
    {
        Dictionary<string, string> headers = BuildHeaderDictionary(source.Headers);
        string queryString = source.QueryString.HasValue ? source.QueryString.Value!.TrimStart('?') : "";
        return await ForwardAsync(source.Method, path, queryString, headers, body, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProxyResponse> ForwardAsync(string method, string path, string queryString, IDictionary<string, string> headers, byte[] body, CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        _options.ApplyAuthHeaders(socket.Options);

        await socket.ConnectAsync(BuildWebSocketUri(_options.ServerEndpoint), cancellationToken).ConfigureAwait(false);
        string id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        JsonObject envelope = BuildRequestEnvelope(method, id, path, queryString, headers, body);
        byte[] payload = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);

        ProxyResponseBuilder response = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket closed before response completed.");
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                byte[] messageBytes = message.ToArray();
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    response.AppendBinaryChunk(id, messageBytes);
                    continue;
                }

                string text = Encoding.UTF8.GetString(messageBytes);
                if (HandleTextMessage(id, text, response, out ProxyResponse? completed))
                    return completed ?? response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket response ended without a payload.");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return response.ToResponseOrError(StatusCodes.Status502BadGateway, "WebSocket ended before response completed.");
    }

    private static Dictionary<string, string> BuildHeaderDictionary(IHeaderDictionary sourceHeaders)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in sourceHeaders)
        {
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers[header.Key] = string.Join(", ", header.Value.ToArray());
        }

        return headers;
    }

    private static JsonObject BuildRequestEnvelope(string method, string id, string path, string queryString, IDictionary<string, string> requestHeaders, byte[] body)
    {
        JsonObject headers = new();
        foreach (KeyValuePair<string, string> header in requestHeaders)
            headers[header.Key] = header.Value;

        return new JsonObject
        {
            ["type"] = "request",
            ["id"] = id,
            ["method"] = string.IsNullOrWhiteSpace(method) ? "GET" : method,
            ["path"] = path,
            ["queryString"] = queryString ?? "",
            ["headers"] = headers,
            ["bodyBase64"] = body.Length == 0 ? "" : Convert.ToBase64String(body)
        };
    }

    private static bool HandleTextMessage(string id, string text, ProxyResponseBuilder response, out ProxyResponse? completed)
    {
        completed = null;
        JsonObject? message;
        try
        {
            message = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }

        if (message == null)
            return false;

        string type = message["type"]?.ToString() ?? "";
        string messageId = message["id"]?.ToString() ?? "";
        if (!string.IsNullOrWhiteSpace(messageId) && !string.Equals(messageId, id, StringComparison.Ordinal))
            return false;

        if (type.Equals("response-start", StringComparison.OrdinalIgnoreCase))
        {
            response.Start(message);
            return false;
        }

        if (type.Equals("response-end", StringComparison.OrdinalIgnoreCase))
        {
            completed = response.ToResponseOrError(StatusCodes.Status200OK, "");
            return true;
        }

        if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            completed = response.ToResponseOrError(StatusCodes.Status502BadGateway, message["error"]?.ToString() ?? "SocketJack WebSocket proxy error.");
            return true;
        }

        return false;
    }
}

public sealed class ProxyResponseBuilder
{
    private readonly MemoryStream _body = new();
    private int _statusCode = StatusCodes.Status200OK;
    private string _reasonPhrase = "OK";
    private string _contentType = "application/octet-stream";
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    public void Start(JsonObject message)
    {
        _started = true;
        _statusCode = ReadInt(message, "statusCode") ?? StatusCodes.Status200OK;
        _reasonPhrase = message["reasonPhrase"]?.ToString() ?? "OK";
        _contentType = message["contentType"]?.ToString() ?? "application/octet-stream";
        if (message["headers"] is JsonObject headers)
        {
            foreach (KeyValuePair<string, JsonNode?> header in headers)
                _headers[header.Key] = header.Value?.ToString() ?? "";
        }
    }

    public void AppendBinaryChunk(string requestId, byte[] messageBytes)
    {
        if (messageBytes.Length <= 32)
            return;

        string prefix = Encoding.ASCII.GetString(messageBytes, 0, 32).Trim();
        if (!string.Equals(prefix, requestId, StringComparison.Ordinal))
            return;

        _body.Write(messageBytes, 32, messageBytes.Length - 32);
    }

    public ProxyResponse ToResponseOrError(int fallbackStatus, string fallbackError)
    {
        if (!_started && !string.IsNullOrWhiteSpace(fallbackError))
        {
            byte[] body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = fallbackError }));
            return new ProxyResponse(fallbackStatus, "Bad Gateway", "application/json", new Dictionary<string, string>(), body);
        }

        return new ProxyResponse(_statusCode, _reasonPhrase, _contentType, _headers, _body.ToArray());
    }

    private static int? ReadInt(JsonObject obj, string name)
    {
        JsonNode? node = obj[name];
        if (node == null)
            return null;
        if (node is JsonValue value && value.TryGetValue(out int intValue))
            return intValue;
        if (int.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            return parsed;
        return null;
    }
}

public sealed class ProxyResponse
{
    public ProxyResponse(int statusCode, string reasonPhrase, string contentType, IDictionary<string, string> headers, byte[] body)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        ContentType = contentType;
        Headers = headers;
        Body = body;
    }

    public int StatusCode { get; }
    public string ReasonPhrase { get; }
    public string ContentType { get; }
    public IDictionary<string, string> Headers { get; }
    public byte[] Body { get; }
}

public static class SocketJackSafePath
{
    public static bool IsSafeGetPath(string? path)
    {
        if (!IsSafePathSyntax(path))
            return false;
        return path!.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/models", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeProxyPath(string? path)
    {
        if (!IsSafePathSyntax(path))
            return false;
        return path!.Equals("/", StringComparison.Ordinal) ||
            path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/completions", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/embeddings", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/models", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/responses", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafePathSyntax(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains("://", StringComparison.Ordinal) ||
            path.Contains("..", StringComparison.Ordinal) ||
            path.Contains('\\'))
        {
            return false;
        }

        return true;
    }
}

public static class SocketJackProxyPath
{
    public static string NormalizeForUpstream(string path)
    {
        if (path.Equals("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return "/v1/chat/completions";
        if (path.Equals("/completions", StringComparison.OrdinalIgnoreCase))
            return "/v1/completions";
        if (path.Equals("/embeddings", StringComparison.OrdinalIgnoreCase))
            return "/v1/embeddings";
        if (path.Equals("/models", StringComparison.OrdinalIgnoreCase))
            return "/v1/models";
        if (path.Equals("/responses", StringComparison.OrdinalIgnoreCase))
            return "/v1/responses";
        return path;
    }

    public static bool IsOpenAiChatCompletionsPath(string path)
    {
        return path.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiResponsesPath(string path)
    {
        return path.Equals("/v1/responses", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOpenAiModelsPath(string path)
    {
        return path.Equals("/v1/models", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOllamaTagsPath(string path)
    {
        return path.Equals("/api/tags", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsOllamaChatPath(string path)
    {
        return path.Equals("/api/chat", StringComparison.OrdinalIgnoreCase);
    }
}

public static class SocketJackOllamaChatAdapter
{
    public static byte[] BuildOpenAiChatRequest(byte[] requestBody, string selectedModelId)
    {
        JsonObject ollama = JsonNode.Parse(requestBody) as JsonObject ?? new JsonObject();
        var openAi = new JsonObject
        {
            ["model"] = FirstNonEmpty(ollama["model"]?.ToString(), selectedModelId, "socketjack-model"),
            // A completed Ollama response is valid even when the caller requested streaming,
            // and avoids translating SSE fragments into Ollama NDJSON here.
            ["stream"] = false
        };

        if (ollama["messages"] is JsonArray messages)
            openAi["messages"] = JsonNode.Parse(messages.ToJsonString());
        else
            openAi["messages"] = new JsonArray();

        if (ollama["tools"] is JsonArray tools)
            openAi["tools"] = JsonNode.Parse(tools.ToJsonString());
        if (ollama["format"] != null)
            openAi["response_format"] = JsonNode.Parse(ollama["format"]!.ToJsonString());

        if (ollama["options"] is JsonObject options)
        {
            CopyOption(options, openAi, "temperature", "temperature");
            CopyOption(options, openAi, "top_p", "top_p");
            CopyOption(options, openAi, "num_predict", "max_tokens");
            CopyOption(options, openAi, "stop", "stop");
        }

        return Encoding.UTF8.GetBytes(openAi.ToJsonString());
    }

    public static ProxyResponse BuildChatResponse(ProxyResponse openAiResponse, string selectedModelId)
    {
        if (openAiResponse.StatusCode < StatusCodes.Status200OK ||
            openAiResponse.StatusCode >= StatusCodes.Status300MultipleChoices)
        {
            return openAiResponse;
        }

        JsonObject openAi = JsonNode.Parse(openAiResponse.Body) as JsonObject ?? new JsonObject();
        JsonObject? choice = (openAi["choices"] as JsonArray)?[0] as JsonObject;
        JsonObject? sourceMessage = choice?["message"] as JsonObject;
        var message = new JsonObject
        {
            ["role"] = sourceMessage?["role"]?.ToString() ?? "assistant",
            ["content"] = sourceMessage?["content"]?.ToString() ?? ""
        };
        if (sourceMessage?["tool_calls"] is JsonArray toolCalls)
            message["tool_calls"] = JsonNode.Parse(toolCalls.ToJsonString());

        JsonObject? usage = openAi["usage"] as JsonObject;
        var root = new JsonObject
        {
            ["model"] = FirstNonEmpty(openAi["model"]?.ToString(), selectedModelId, "socketjack-model"),
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["message"] = message,
            ["done"] = true,
            ["done_reason"] = choice?["finish_reason"]?.ToString() ?? "stop",
            ["prompt_eval_count"] = ReadInt(usage?["prompt_tokens"]),
            ["eval_count"] = ReadInt(usage?["completion_tokens"])
        };
        return new ProxyResponse(
            StatusCodes.Status200OK,
            "OK",
            "application/json",
            new Dictionary<string, string>(),
            Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    public static ProxyResponse BuildTagsResponse(string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();
        var root = new JsonObject
        {
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = model,
                    ["model"] = model,
                    ["modified_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    ["size"] = 0,
                    ["digest"] = "socketjack",
                    ["details"] = new JsonObject
                    {
                        ["format"] = "gguf",
                        ["family"] = "socketjack",
                        ["families"] = new JsonArray("socketjack"),
                        ["parameter_size"] = "",
                        ["quantization_level"] = ""
                    }
                }
            }
        };
        byte[] body = Encoding.UTF8.GetBytes(root.ToJsonString());
        return new ProxyResponse(StatusCodes.Status200OK, "OK", "application/json", new Dictionary<string, string>(), body);
    }

    private static void CopyOption(JsonObject options, JsonObject destination, string sourceName, string destinationName)
    {
        if (options[sourceName] != null)
            destination[destinationName] = JsonNode.Parse(options[sourceName]!.ToJsonString());
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
    }

    private static int ReadInt(JsonNode? value)
    {
        return int.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;
    }
}

public sealed class SocketJackAssistantTextAccumulator
{
    private const int LargeRepeatThreshold = 64;
    private readonly StringBuilder _visible = new();

    public string VisibleText => _visible.ToString();

    public bool HasVisibleText => _visible.Length > 0;

    public string AcceptDeltaOrSnapshot(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        string value = SocketJackOpenAiChatAdapter.NormalizeVisibleAssistantDeltaText(text);
        if (string.IsNullOrEmpty(value))
            return "";
        if (_visible.Length == 0)
        {
            _visible.Append(value);
            return value;
        }

        string current = _visible.ToString();
        if (string.Equals(value, current, StringComparison.Ordinal))
            return "";

        if (value.StartsWith(current, StringComparison.Ordinal))
        {
            string suffix = value.Substring(current.Length);
            _visible.Clear();
            _visible.Append(value);
            return suffix;
        }

        if (value.Length >= LargeRepeatThreshold && current.Contains(value, StringComparison.Ordinal))
            return "";

        if (value.Length >= LargeRepeatThreshold && current.EndsWith(value, StringComparison.Ordinal))
            return "";

        int commonPrefix = CountCommonPrefix(current, value);
        int snapshotThreshold = Math.Min(current.Length, Math.Max(16, Math.Min(LargeRepeatThreshold, current.Length / 2)));
        if (value.Length > current.Length && commonPrefix >= snapshotThreshold)
        {
            string suffix = value.Substring(commonPrefix);
            _visible.Clear();
            _visible.Append(value);
            return suffix;
        }

        _visible.Append(value);
        return value;
    }

    private static int CountCommonPrefix(string left, string right)
    {
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
        {
            if (left[i] != right[i])
                return i;
        }

        return count;
    }
}

public enum SocketJackToolCallConsumeResult
{
    PassThrough,
    Buffered,
    ToolCalls
}

public sealed class SocketJackOpenAiToolCall
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";
}

public sealed class SocketJackToolCallTextAccumulator
{
    private const int MaxBufferedToolCallChars = 65536;
    private readonly HashSet<string> _availableTools;
    private readonly StringBuilder _buffer = new();
    private IReadOnlyList<SocketJackOpenAiToolCall> _pendingToolCalls = Array.Empty<SocketJackOpenAiToolCall>();
    private bool _capturing;

    public SocketJackToolCallTextAccumulator(IReadOnlyCollection<string>? availableTools)
    {
        _availableTools = availableTools == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(availableTools.Where(tool => !string.IsNullOrWhiteSpace(tool)).Select(tool => tool.Trim()), StringComparer.OrdinalIgnoreCase);
    }

    public bool Enabled => _availableTools.Count > 0;

    public SocketJackToolCallConsumeResult Accept(string text, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, out string passThrough)
    {
        toolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        passThrough = "";
        if (!Enabled || string.IsNullOrEmpty(text))
        {
            passThrough = text ?? "";
            return SocketJackToolCallConsumeResult.PassThrough;
        }

        if (!_capturing && !SocketJackOpenAiChatAdapter.LooksLikeToolCallTextStart(text))
        {
            passThrough = text;
            return SocketJackToolCallConsumeResult.PassThrough;
        }

        _capturing = true;
        _buffer.Append(text);
        string candidate = _buffer.ToString();
        if (SocketJackOpenAiChatAdapter.TryParseAssistantToolCalls(candidate, _availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> parsedToolCalls))
        {
            _pendingToolCalls = parsedToolCalls;
            return SocketJackToolCallConsumeResult.Buffered;
        }

        if (_buffer.Length > MaxBufferedToolCallChars ||
            (_pendingToolCalls.Count == 0 && !SocketJackOpenAiChatAdapter.MightStillBeToolCallText(candidate)))
        {
            if (SocketJackOpenAiChatAdapter.TryParseAssistantToolCalls(candidate, null, out IReadOnlyList<SocketJackOpenAiToolCall> unavailableToolCalls) &&
                unavailableToolCalls.Count > 0)
            {
                Clear();
                return SocketJackToolCallConsumeResult.Buffered;
            }

            passThrough = candidate;
            Clear();
            return SocketJackToolCallConsumeResult.PassThrough;
        }

        return SocketJackToolCallConsumeResult.Buffered;
    }

    public bool TryComplete(out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        toolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        if (!_capturing || _buffer.Length == 0)
            return false;

        if (_pendingToolCalls.Count > 0)
        {
            toolCalls = _pendingToolCalls;
            Clear();
            return true;
        }

        string candidate = _buffer.ToString();
        if (SocketJackOpenAiChatAdapter.TryParseAssistantToolCalls(candidate, _availableTools, out toolCalls))
        {
            Clear();
            return true;
        }

        Clear();
        return false;
    }

    public bool TryParseNativeOpenAiToolCalls(string text, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        return SocketJackOpenAiChatAdapter.TryParseOpenAiStreamingToolCalls(text, _availableTools, out toolCalls);
    }

    private void Clear()
    {
        _buffer.Clear();
        _pendingToolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        _capturing = false;
    }
}

public static class SocketJackOpenAiChatAdapter
{
    public static string DoneSse => "data: [DONE]\n\n";

    public static string CreateCompletionId()
    {
        return "chatcmpl-socketjack-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public static string CreateResponseId()
    {
        return "resp_socketjack_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    public static string GetResponseModel(JsonObject openAiRequest, string selectedModelId)
    {
        return FirstNonEmpty(FirstString(openAiRequest, "model"), selectedModelId, "socketjack-model");
    }

    public static JsonObject BuildChatStreamRequest(JsonObject openAiRequest, string selectedModelId)
    {
        string model = FirstString(openAiRequest, "model");
        if (string.IsNullOrWhiteSpace(model))
            model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();

        JsonArray messages = BuildChatMessages(openAiRequest);
        AddVisualStudioFeatureMessages(openAiRequest, messages);
        AddVisualStudioToolRecoveryMessage(openAiRequest, messages);

        var payload = new JsonObject
        {
            ["model"] = model,
            ["sessionId"] = "copilot_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ["streamId"] = "copilot_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            ["messages"] = messages,
            ["files"] = ExtractVisualStudioReferenceFiles(openAiRequest)
        };

        CopyIfPresent(openAiRequest, payload, "temperature");
        CopyIfPresent(openAiRequest, payload, "top_p");
        CopyIfPresent(openAiRequest, payload, "presence_penalty");
        CopyIfPresent(openAiRequest, payload, "frequency_penalty");
        CopyIfPresent(openAiRequest, payload, "metadata");
        CopyIfPresent(openAiRequest, payload, "reasoning");
        CopyIfPresent(openAiRequest, payload, "response_format");
        CopyIfPresent(openAiRequest, payload, "tool_choice");
        if (openAiRequest["tools"] is JsonArray tools)
            payload["tools"] = BuildChatCompletionsTools(tools, LooksLikeUserRequestedDotNetModernization(openAiRequest), openAiRequest);
        CopyIfPresent(openAiRequest, payload, "parallel_tool_calls");
        CopyIfPresent(openAiRequest, payload, "previous_response_id");
        CopyIfPresent(openAiRequest, payload, "summary");
        CopyIfPresent(openAiRequest, payload, "conversation_summary");
        CopyIfPresent(openAiRequest, payload, "context_summary");
        CopyMaxTokens(openAiRequest, payload);
        RequireVisualStudioToolChoiceWhenWorkingPlan(openAiRequest, payload);

        if (openAiRequest.ContainsKey("tools") ||
            openAiRequest.ContainsKey("tool_choice") ||
            openAiRequest.ContainsKey("functions") ||
            openAiRequest.ContainsKey("function_call") ||
            !string.IsNullOrWhiteSpace(DetectVisualStudioAiMode(openAiRequest)) ||
            (payload["files"] is JsonArray payloadFiles && payloadFiles.Count > 0))
        {
            payload["service"] = "agent";
        }

        return payload;
    }

    public static HashSet<string> ReadAvailableToolNames(byte[] requestBody)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            return ReadAvailableToolNames(root);
        }
        catch (JsonException)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static int GetEffectiveOpenAiStreamTimeoutSeconds(byte[] requestBody, int configuredTimeoutSeconds)
    {
        if (configuredTimeoutSeconds <= 0)
            return configuredTimeoutSeconds;

        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            if (root == null)
                return configuredTimeoutSeconds;

            HashSet<string> availableTools = ReadAvailableToolNames(root);
            if (availableTools.Count > 0 && LooksLikeVisualStudioToolRequest(root, availableTools))
                return Math.Max(configuredTimeoutSeconds, SocketJackModelProxyForwarder.VisualStudioStreamMinimumTimeoutSeconds);
        }
        catch (JsonException)
        {
        }

        return configuredTimeoutSeconds;
    }

    public static bool IsVisualStudioToolRequest(byte[] requestBody)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            if (root == null)
                return false;

            HashSet<string> availableTools = ReadAvailableToolNames(root);
            return availableTools.Count > 0 && LooksLikeVisualStudioToolRequest(root, availableTools);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool ShouldPreferVisualStudioXamlCodeBehindForVbFiles(byte[] requestBody)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            return root != null && ShouldPreferVisualStudioXamlCodeBehindForVbFiles(root);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool ShouldPreferVisualStudioXamlCodeBehindForVbFiles(JsonObject openAiRequest)
    {
        if (!LooksLikeImplementationPlanRequest(openAiRequest))
            return false;

        string userText = CollectUserFacingRequestText(openAiRequest);
        string text = userText + "\n" + CollectVisualStudioToolResultText(openAiRequest);
        if (!text.Contains("xaml", StringComparison.OrdinalIgnoreCase))
            return false;

        return userText.Contains("vba", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(userText, @"\.xaml\.vb\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(userText, @"\b[A-Za-z_][\w.-]*\.vb\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            text.Contains("vba", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\.xaml\.vb\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            Regex.IsMatch(text, @"\b[A-Za-z_][\w.-]*\.vb\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool IsRecoverableMalformedToolAttemptText(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            text.Contains("attempted a tool call but returned malformed JSON", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRecoverableVisualStudioToolTranscriptText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return text.Contains("Assistant requested tool call(s)", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("<tool_result>", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("tool_call id=", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsRecoverableVisualStudioPrematureClarificationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return (text.Contains("Implementation Plan Clarification", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Missing Information Required", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("plan was not specified", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("need clarification", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("please specify", StringComparison.OrdinalIgnoreCase)) &&
            text.Contains("plan", StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryBuildVisualStudioMalformedRecoveryToolCalls(byte[] requestBody, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        toolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            return root != null && TryBuildVisualStudioMalformedRecoveryToolCalls(root, out toolCalls);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryBuildVisualStudioMalformedRecoveryToolCalls(JsonObject openAiRequest, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        toolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        HashSet<string> availableTools = ReadAvailableToolNames(openAiRequest);
        bool canReadProjects = availableTools.Contains("get_projects_in_solution");
        bool canReadProjectFiles = availableTools.Contains("get_files_in_project");
        bool canReadFile = availableTools.Contains("get_file");
        bool implementationPlanRequest = LooksLikeImplementationPlanRequest(openAiRequest);
        bool readmeSummaryRequest = LooksLikeVisualStudioProjectReadmeSummaryRequest(openAiRequest);
        if (availableTools.Count == 0 ||
            !LooksLikeVisualStudioToolRequest(openAiRequest, availableTools) ||
            (!canReadProjects && !canReadProjectFiles && !canReadFile))
        {
            return false;
        }

        List<VisualStudioToolResultRecord> results = BuildVisualStudioToolResultHistory(openAiRequest);
        if (results.Count == 0)
        {
            if (canReadProjects)
            {
                toolCalls = new[] { BuildVisualStudioGetProjectsToolCall() };
                return true;
            }

            return false;
        }

        if (canReadProjectFiles &&
            TryBuildVisualStudioGetFilesAfterProjectList(results, out SocketJackOpenAiToolCall projectFiles))
        {
            toolCalls = new[] { projectFiles };
            return true;
        }

        if (implementationPlanRequest && canReadFile &&
            TryFindUnreadImplementationPlanPath(results, out string planPath))
        {
            toolCalls = new[]
            {
                BuildVisualStudioGetFileToolCall(planPath, 1, 120)
            };
            return true;
        }

        if (readmeSummaryRequest && canReadFile &&
            TryFindUnreadProjectSummarySourcePath(results, out string summarySourcePath))
        {
            toolCalls = new[]
            {
                BuildVisualStudioGetFileToolCall(summarySourcePath, 1, 160)
            };
            return true;
        }

        if (canReadFile &&
            TryBuildVisualStudioGetFileContinuation(results, out SocketJackOpenAiToolCall continuation))
        {
            toolCalls = new[] { continuation };
            return true;
        }

        return false;
    }

    private static bool TryFindUnreadProjectSummarySourcePath(IReadOnlyList<VisualStudioToolResultRecord> results, out string sourcePath)
    {
        sourcePath = "";
        var candidatesByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var readPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (VisualStudioToolResultRecord result in results)
        {
            if (result.Name.Equals("get_projects_in_solution", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Content) &&
                !IsFailedOrNoOpVisualStudioToolResult(result))
            {
                AddProjectSummaryPathCandidate(candidatesByPath, FirstNonEmptyLine(result.Content));
            }

            if ((result.Name.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(result.Content) &&
                !IsFailedOrNoOpVisualStudioToolResult(result))
            {
                foreach (string path in EnumerateVisualStudioPathCandidates(result.Content))
                    AddProjectSummaryPathCandidate(candidatesByPath, path);
            }

            if (result.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase))
            {
                string readPath = FirstNonEmpty(
                    TryReadStringArgument(result.ArgumentsJson, "filePath", "filename", "path"),
                    TryExtractGetFileResultPath(result.Content));
                if (!string.IsNullOrWhiteSpace(readPath))
                    readPaths.Add(NormalizeVisualStudioPathForComparison(readPath));
            }
        }

        if (readPaths.Count >= 6)
            return false;

        sourcePath = candidatesByPath.Values
            .Where(path => !readPaths.Contains(NormalizeVisualStudioPathForComparison(path)))
            .OrderBy(ProjectSummaryPathPriority)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault() ?? "";
        return !string.IsNullOrWhiteSpace(sourcePath);
    }

    private static void AddProjectSummaryPathCandidate(Dictionary<string, string> candidatesByPath, string path)
    {
        string candidate = CleanVisualStudioPathCandidate(path);
        if (string.IsNullOrWhiteSpace(candidate) ||
            !LooksLikeProjectSummarySourcePath(candidate))
        {
            return;
        }

        string key = NormalizeVisualStudioPathForComparison(candidate);
        if (!candidatesByPath.ContainsKey(key))
            candidatesByPath[key] = candidate;
    }

    private static IEnumerable<string> EnumerateVisualStudioPathCandidates(string content)
    {
        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string candidate = CleanVisualStudioPathCandidate(line);
            if (!string.IsNullOrWhiteSpace(candidate))
                yield return candidate;
        }
    }

    private static string CleanVisualStudioPathCandidate(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";

        text = Regex.Replace(text, @"^\s*(?:[-*+]\s+|\d+[.)]\s+)", "", RegexOptions.CultureInvariant);
        text = text.Trim().Trim('`', '"', '\'');
        int trailingComment = text.IndexOf(" (", StringComparison.Ordinal);
        if (trailingComment > 0)
            text = text.Substring(0, trailingComment).Trim();

        return text;
    }

    private static bool LooksLikeProjectSummarySourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalized = NormalizeVisualStudioPathForComparison(path);
        if (normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith("/readme.md", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("readme.md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(path);
        return extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".vb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".md", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".resx", StringComparison.OrdinalIgnoreCase);
    }

    private static int ProjectSummaryPathPriority(string path)
    {
        string fileName = Path.GetFileName(path);
        string extension = Path.GetExtension(path);
        if (extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (fileName.Equals("App.xaml", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (fileName.Contains("Main", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
            return 4;
        if (extension.Equals(".vb", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return 5;
        if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            return 6;

        return 9;
    }

    private static bool TryBuildVisualStudioGetFilesAfterProjectList(IReadOnlyList<VisualStudioToolResultRecord> results, out SocketJackOpenAiToolCall toolCall)
    {
        toolCall = new SocketJackOpenAiToolCall();
        if (results.Count == 0)
            return false;

        VisualStudioToolResultRecord last = results[^1];
        if (!last.Name.Equals("get_projects_in_solution", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(last.Content) ||
            IsFailedOrNoOpVisualStudioToolResult(last))
        {
            return false;
        }

        string projectPath = FirstNonEmptyLine(last.Content);
        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        toolCall = new SocketJackOpenAiToolCall
        {
            Id = "call_sj_recovery_0",
            Name = "get_files_in_project",
            ArgumentsJson = new JsonObject
            {
                ["projectPath"] = projectPath
            }.ToJsonString()
        };
        return true;
    }

    private static bool TryFindUnreadImplementationPlanPath(IReadOnlyList<VisualStudioToolResultRecord> results, out string planPath)
    {
        planPath = "";
        foreach (VisualStudioToolResultRecord result in results)
        {
            if ((result.Name.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
                TryExtractImplementationPlanPath(result.Content, out string candidate))
            {
                planPath = candidate;
            }
        }

        if (string.IsNullOrWhiteSpace(planPath))
            return false;

        string normalizedPlanPath = NormalizeVisualStudioPathForComparison(planPath);
        foreach (VisualStudioToolResultRecord result in results)
        {
            if (!result.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.Content) ||
                IsFailedOrNoOpVisualStudioToolResult(result))
            {
                continue;
            }

            string readPath = NormalizeVisualStudioPathForComparison(FirstNonEmpty(
                TryReadStringArgument(result.ArgumentsJson, "filePath", "filename", "path"),
                TryExtractGetFileResultPath(result.Content)));
            if (readPath.Equals(normalizedPlanPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool TryBuildVisualStudioGetFileContinuation(IReadOnlyList<VisualStudioToolResultRecord> results, out SocketJackOpenAiToolCall toolCall)
    {
        toolCall = new SocketJackOpenAiToolCall();
        for (int i = results.Count - 1; i >= 0; i--)
        {
            VisualStudioToolResultRecord result = results[i];
            if (!result.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.Content) ||
                IsFailedOrNoOpVisualStudioToolResult(result))
            {
                continue;
            }

            if (!TryReadGetFileResultLineSpan(result.Content, out int _, out int endLine, out int totalLines) ||
                endLine >= totalLines)
            {
                return false;
            }

            string path = FirstNonEmpty(
                TryReadStringArgument(result.ArgumentsJson, "filePath", "filename", "path"),
                TryExtractGetFileResultPath(result.Content));
            if (string.IsNullOrWhiteSpace(path))
                return false;

            int nextStartLine = endLine + 1;
            int nextEndLine = Math.Min(totalLines, nextStartLine + 79);
            toolCall = BuildVisualStudioGetFileToolCall(path, nextStartLine, nextEndLine);
            return true;
        }

        return false;
    }

    private static SocketJackOpenAiToolCall BuildVisualStudioGetFileToolCall(string path, int startLine, int endLine)
    {
        return new SocketJackOpenAiToolCall
        {
            Id = "call_sj_recovery_0",
            Name = "get_file",
            ArgumentsJson = new JsonObject
            {
                ["filename"] = path,
                ["startLine"] = Math.Max(1, startLine),
                ["endLine"] = Math.Max(Math.Max(1, startLine), endLine),
                ["includeLineNumbers"] = true
            }.ToJsonString()
        };
    }

    private static SocketJackOpenAiToolCall BuildVisualStudioGetProjectsToolCall()
    {
        return new SocketJackOpenAiToolCall
        {
            Id = "call_sj_recovery_0",
            Name = "get_projects_in_solution",
            ArgumentsJson = "{}"
        };
    }

    private static bool TryReadGetFileResultLineSpan(string content, out int startLine, out int endLine, out int totalLines)
    {
        startLine = 0;
        endLine = 0;
        totalLines = 0;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        Match match = Regex.Match(content, @"\bLines\s+(?<start>\d+)-(?<end>\d+)\s+of\s+(?<total>\d+)\s+total\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success &&
            int.TryParse(match.Groups["start"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out startLine) &&
            int.TryParse(match.Groups["end"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out endLine) &&
            int.TryParse(match.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out totalLines);
    }

    private static string TryExtractGetFileResultPath(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        Match match = Regex.Match(content, @"^```[^\s`]*\s+(?<path>.+?)\s+\(Lines\s+\d+-\d+\s+of\s+\d+\s+total\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["path"].Value.Trim() : "";
    }

    public static string FindLatestVisualStudioProjectPath(byte[] requestBody)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(requestBody) as JsonObject;
            return root == null ? "" : FindLatestVisualStudioProjectPath(root);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    internal static string FindLatestVisualStudioProjectPath(JsonObject openAiRequest)
    {
        string lastProjectPath = "";
        foreach (VisualStudioToolResultRecord result in BuildVisualStudioToolResultHistory(openAiRequest))
        {
            if (result.Name.Equals("get_projects_in_solution", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Content))
            {
                lastProjectPath = FirstNonEmptyLine(result.Content);
            }
        }

        return lastProjectPath;
    }

    public static HashSet<string> ReadAvailableToolNames(JsonObject? request)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (request?["tools"] is not JsonArray tools)
            return names;

        foreach (JsonNode? node in tools)
        {
            if (node is not JsonObject tool)
                continue;

            string name = FirstNonEmpty(
                FirstStringFromChild(tool, "function", "name"),
                FirstString(tool, "name"),
                FirstString(tool, "tool"),
                FirstString(tool, "id"));
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }

        return names;
    }

    public static JsonObject BuildOpenAiChatCompletionsForwardRequest(JsonObject openAiRequest, string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId)
            ? FirstNonEmpty(FirstString(openAiRequest, "model"), "socketjack-model")
            : selectedModelId.Trim();

        var payload = CloneObject(openAiRequest);
        payload["model"] = model;
        JsonArray messages = BuildChatMessages(openAiRequest);
        AddVisualStudioFeatureMessages(openAiRequest, messages);
        AddVisualStudioReferenceFileMessage(openAiRequest, messages);
        AddVisualStudioToolRecoveryMessage(openAiRequest, messages);
        payload["messages"] = messages;
        if (openAiRequest["tools"] is JsonArray tools)
            payload["tools"] = BuildChatCompletionsTools(tools, LooksLikeUserRequestedDotNetModernization(openAiRequest), openAiRequest);
        if (openAiRequest["stream"] == null)
            payload["stream"] = true;

        RequireVisualStudioToolChoiceWhenWorkingPlan(openAiRequest, payload);
        CompactVisualStudioMessagesForLocalRuntime(payload);
        CopyMaxTokens(openAiRequest, payload);
        return payload;
    }

    public static JsonObject BuildNoVisibleAssistantRecoveryRequest(JsonObject openAiRequest, string selectedModelId)
    {
        JsonObject payload = BuildOpenAiChatCompletionsForwardRequest(openAiRequest, selectedModelId);
        payload.Remove("tools");
        payload.Remove("tool_choice");
        payload.Remove("functions");
        payload.Remove("function_call");
        payload.Remove("parallel_tool_calls");
        payload.Remove("response_format");
        payload.Remove("stream_options");
        payload["stream"] = false;
        payload["temperature"] = 0;

        int requestedMaxTokens = ReadPositiveInt(FirstMaxTokensNode(payload)) ?? 0;
        payload["max_tokens"] = Math.Max(requestedMaxTokens, SocketJackModelProxyForwarder.VisualStudioAutopilotMinimumMaxTokens);
        payload.Remove("max_completion_tokens");
        payload.Remove("max_output_tokens");

        JsonArray messages = payload["messages"] as JsonArray ?? new JsonArray();
        payload["messages"] = messages;
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = "[SocketJack final-answer recovery] The prior model pass ended without visible assistant text after the available Visual Studio references or tool results were gathered. Answer the user's request now using the existing context. Do not call tools, restart the task, mention this recovery instruction, or return hidden reasoning. Produce a complete visible final answer. /no_think"
        });
        return payload;
    }

    private static void RequireVisualStudioToolChoiceWhenWorkingPlan(JsonObject openAiRequest, JsonObject payload)
    {
        if ((!LooksLikeImplementationPlanRequest(openAiRequest) &&
                !LooksLikeVisualStudioProjectReadmeSummaryRequest(openAiRequest)) ||
            HasSuccessfulVisualStudioTaskComplete(openAiRequest) ||
            payload["tools"] is not JsonArray tools ||
            tools.Count == 0)
        {
            return;
        }

        HashSet<string> availableTools = ReadAvailableToolNames(openAiRequest);
        if (availableTools.Count == 0 || !LooksLikeVisualStudioToolRequest(openAiRequest, availableTools))
            return;

        payload["tool_choice"] = "required";
    }

    private static JsonArray BuildChatCompletionsTools(JsonArray tools, bool allowDotNetModernizationTools, JsonObject openAiRequest)
    {
        var result = new JsonArray();
        HashSet<string>? localRuntimeToolAllowList = allowDotNetModernizationTools ? null : BuildVisualStudioLocalRuntimeToolAllowList(openAiRequest);
        foreach (JsonNode? node in tools)
        {
            if (node is not JsonObject tool)
                continue;

            string type = FirstString(tool, "type");
            JsonObject? sourceFunction = tool["function"] as JsonObject;
            string name = FirstNonEmpty(
                sourceFunction == null ? "" : FirstString(sourceFunction, "name"),
                FirstString(tool, "name"),
                FirstString(tool, "tool"),
                FirstString(tool, "id"));
            if (!allowDotNetModernizationTools && !IsGeneralVisualStudioImplementationToolName(name))
                continue;
            if (localRuntimeToolAllowList != null && !localRuntimeToolAllowList.Contains(name.Trim()))
                continue;

            if (!type.Equals("function", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(name))
            {
                result.Add(CloneNode(tool));
                continue;
            }

            JsonObject source = sourceFunction ?? tool;
            var function = new JsonObject
            {
                ["name"] = name,
                ["description"] = TruncateToolText(FirstString(source, "description"), 90)
            };
            JsonObject? compactParameters = BuildCompactToolParameters(source["parameters"] as JsonObject);
            if (compactParameters != null)
                function["parameters"] = compactParameters;
            CopyIfPresent(source, function, "strict");
            result.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = function
            });
        }

        return result;
    }

    private static HashSet<string>? BuildVisualStudioLocalRuntimeToolAllowList(JsonObject openAiRequest)
    {
        HashSet<string> availableTools = ReadAvailableToolNames(openAiRequest);
        if (availableTools.Count == 0 || !LooksLikeVisualStudioToolRequest(openAiRequest, availableTools))
            return null;

        IReadOnlyList<string> preferredTools = SelectVisualStudioLocalRuntimeTools(openAiRequest);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string toolName in preferredTools)
        {
            if (availableTools.Contains(toolName))
                allowed.Add(toolName);
        }

        if (LooksLikeImplementationPlanRequest(openAiRequest))
        {
            bool hasSuccessfulFileMutation = HasSuccessfulVisualStudioFileMutation(openAiRequest);
            bool hasSuccessfulProgressUpdate = HasSuccessfulVisualStudioProgressUpdate(openAiRequest);
            bool hasCreateFileAlreadyExists = HasVisualStudioCreateFileAlreadyExists(openAiRequest);
            if (hasSuccessfulFileMutation &&
                !hasSuccessfulProgressUpdate &&
                availableTools.Contains("update_plan_progress"))
            {
                allowed.Clear();
                allowed.Add("update_plan_progress");
            }

            if (!hasSuccessfulFileMutation)
            {
                allowed.Remove("update_plan_progress");
                allowed.Remove("finish_plan");
                allowed.Remove("run_build");
                allowed.Remove("run_tests");
                allowed.Remove("get_errors");
            }

            if (hasCreateFileAlreadyExists)
                allowed.Remove("create_file");

            if (!hasSuccessfulFileMutation || !hasSuccessfulProgressUpdate)
                allowed.Remove("task_complete");
        }

        return allowed.Count == 0 ? null : allowed;
    }

    private static IReadOnlyList<string> SelectVisualStudioLocalRuntimeTools(JsonObject openAiRequest)
    {
        bool implementationPlanRequest = LooksLikeImplementationPlanRequest(openAiRequest);
        bool readmeSummaryRequest = LooksLikeVisualStudioProjectReadmeSummaryRequest(openAiRequest);
        List<VisualStudioToolResultRecord> results = BuildVisualStudioToolResultHistory(openAiRequest);
        if (results.Count == 0)
            return new[] { "get_projects_in_solution" };

        VisualStudioToolResultRecord last = results[^1];
        if (last.Name.Equals("get_projects_in_solution", StringComparison.OrdinalIgnoreCase))
        {
            if (implementationPlanRequest || readmeSummaryRequest)
                return new[] { "get_files_in_project" };

            return new[] { "get_files_in_project", "get_file", "file_search", "grep_search" };
        }

        if (last.Name.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(last.Content))
        {
            return new[] { "get_files_in_project", "file_search", "grep_search", "get_file" };
        }

        if ((last.Name.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                last.Name.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
            string.IsNullOrWhiteSpace(last.Content))
        {
            return new[] { "get_files_in_project", "file_search", "grep_search", "get_file" };
        }

        if (last.Name.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase))
        {
            if (implementationPlanRequest || readmeSummaryRequest)
                return new[] { "get_file" };

            return new[] { "get_file", "file_search", "grep_search" };
        }

        if (last.Name.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("grep_search", StringComparison.OrdinalIgnoreCase))
        {
            if (implementationPlanRequest || readmeSummaryRequest)
                return new[] { "get_file" };

            return new[] { "get_file", "file_search", "grep_search" };
        }

        if (last.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase))
        {
            if (IsFailedOrNoOpVisualStudioToolResult(last))
                return new[] { "get_files_in_project", "file_search", "grep_search", "get_file" };

            if (IsRepeatedSuccessfulVisualStudioGetFileRead(results, out _))
                return new[] { "replace_string_in_file", "multi_replace_string_in_file", "update_plan_progress", "run_build", "get_errors", "file_search", "grep_search" };

            if (readmeSummaryRequest)
                return new[] { "get_file", "create_file", "replace_string_in_file", "multi_replace_string_in_file", "file_search", "grep_search", "task_complete" };

            return new[] { "get_file", "replace_string_in_file", "multi_replace_string_in_file", "file_search", "grep_search", "update_plan_progress", "run_build" };
        }

        if (last.Name.Equals("create_file", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("update_plan_progress", StringComparison.OrdinalIgnoreCase))
        {
            if (IsFailedOrNoOpVisualStudioToolResult(last))
                return new[] { "get_file", "replace_string_in_file", "multi_replace_string_in_file", "update_plan_progress", "run_build", "get_errors" };

            if (readmeSummaryRequest)
                return new[] { "get_file", "create_file", "replace_string_in_file", "multi_replace_string_in_file", "task_complete" };

            return new[] { "update_plan_progress", "get_file", "replace_string_in_file", "multi_replace_string_in_file", "run_build", "get_errors", "task_complete" };
        }

        if (last.Name.Equals("run_build", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("run_tests", StringComparison.OrdinalIgnoreCase) ||
            last.Name.Equals("get_errors", StringComparison.OrdinalIgnoreCase))
        {
            if (IsFailedOrNoOpVisualStudioToolResult(last))
                return new[] { "get_errors", "get_file", "replace_string_in_file", "multi_replace_string_in_file", "update_plan_progress" };

            return new[] { "get_errors", "get_file", "replace_string_in_file", "multi_replace_string_in_file", "update_plan_progress", "task_complete" };
        }

        return VisualStudioLocalRuntimeToolNames.ToArray();
    }

    private sealed record VisualStudioToolResultRecord(string Name, string ArgumentsJson, string Content);

    private static bool IsFailedVisualStudioToolResult(string content)
    {
        string text = content ?? "";
        return text.Contains("Did not find a match", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("couldn't run", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("could not run", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("cannot be used", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsEmptyVisualStudioGetFileResult(string content)
    {
        string text = (content ?? "").Trim();
        if (text.Length == 0)
            return true;

        if (!text.StartsWith("```", StringComparison.Ordinal))
            return false;

        int firstLineBreak = text.IndexOf('\n');
        if (firstLineBreak < 0)
            return false;

        string body = text.Substring(firstLineBreak + 1).Trim();
        if (body.Equals("```", StringComparison.Ordinal))
            return true;

        int closingFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFence < 0)
            return false;

        return string.IsNullOrWhiteSpace(body.Substring(0, closingFence));
    }

    private static bool IsFailedOrNoOpVisualStudioToolResult(VisualStudioToolResultRecord result)
    {
        return IsFailedVisualStudioToolResult(result.Content) ||
            (result.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase) && IsEmptyVisualStudioGetFileResult(result.Content)) ||
            (IsVisualStudioReplaceTool(result.Name) && TryReadNoOpVisualStudioReplacePath(result.ArgumentsJson, out _));
    }

    private static bool HasSuccessfulVisualStudioFileMutation(JsonObject openAiRequest)
    {
        foreach (VisualStudioToolResultRecord result in BuildVisualStudioToolResultHistory(openAiRequest))
        {
            if ((result.Name.Equals("create_file", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
                    result.Name.Equals("remove_file", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(result.Content) &&
                !IsFailedOrNoOpVisualStudioToolResult(result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSuccessfulVisualStudioProgressUpdate(JsonObject openAiRequest)
    {
        foreach (VisualStudioToolResultRecord result in BuildVisualStudioToolResultHistory(openAiRequest))
        {
            if (result.Name.Equals("update_plan_progress", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Content) &&
                !IsFailedOrNoOpVisualStudioToolResult(result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasVisualStudioCreateFileAlreadyExists(JsonObject openAiRequest)
    {
        foreach (VisualStudioToolResultRecord result in BuildVisualStudioToolResultHistory(openAiRequest))
        {
            if (result.Name.Equals("create_file", StringComparison.OrdinalIgnoreCase) &&
                result.Content.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSuccessfulVisualStudioTaskComplete(JsonObject openAiRequest)
    {
        foreach (VisualStudioToolResultRecord result in BuildVisualStudioToolResultHistory(openAiRequest))
        {
            if (result.Name.Equals("task_complete", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(result.Content) &&
                !IsFailedOrNoOpVisualStudioToolResult(result))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRepeatedSuccessfulVisualStudioGetFileRead(IReadOnlyList<VisualStudioToolResultRecord> results, out string repeatedPath)
    {
        repeatedPath = "";
        if (results.Count == 0 || !results[^1].Name.Equals("get_file", StringComparison.OrdinalIgnoreCase))
            return false;

        string lastPath = NormalizeVisualStudioPathForComparison(TryReadStringArgument(results[^1].ArgumentsJson, "filePath", "filename", "path"));
        if (string.IsNullOrWhiteSpace(lastPath) ||
            string.IsNullOrWhiteSpace(results[^1].Content) ||
            IsFailedOrNoOpVisualStudioToolResult(results[^1]))
        {
            return false;
        }

        int matches = 0;
        foreach (VisualStudioToolResultRecord result in results)
        {
            if (!result.Name.Equals("get_file", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(result.Content) ||
                IsFailedOrNoOpVisualStudioToolResult(result))
            {
                continue;
            }

            string path = NormalizeVisualStudioPathForComparison(TryReadStringArgument(result.ArgumentsJson, "filePath", "filename", "path"));
            if (path.Equals(lastPath, StringComparison.OrdinalIgnoreCase))
                matches++;
        }

        repeatedPath = lastPath;
        return matches >= 2;
    }

    private static List<VisualStudioToolResultRecord> BuildVisualStudioToolResultHistory(JsonObject openAiRequest)
    {
        var toolCallsById = new Dictionary<string, VisualStudioToolCallRecord>(StringComparer.OrdinalIgnoreCase);
        var results = new List<VisualStudioToolResultRecord>();

        foreach (JsonObject message in EnumerateVisualStudioMessages(openAiRequest["messages"]).Concat(EnumerateVisualStudioMessages(openAiRequest["input"])))
        {
            string role = FirstString(message, "role");
            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                foreach (VisualStudioToolCallRecord toolCall in EnumerateVisualStudioToolCalls(message["tool_calls"]))
                {
                    if (!string.IsNullOrWhiteSpace(toolCall.Id))
                        toolCallsById[toolCall.Id] = toolCall;
                }

                continue;
            }

            if (!role.Equals("tool", StringComparison.OrdinalIgnoreCase) && !role.Equals("function", StringComparison.OrdinalIgnoreCase))
                continue;

            string toolCallId = FirstString(message, "tool_call_id", "toolCallId", "id");
            toolCallsById.TryGetValue(toolCallId, out VisualStudioToolCallRecord? toolCallRecord);
            string toolName = FirstNonEmpty(toolCallRecord?.Name ?? "", FirstString(message, "name"));
            if (string.IsNullOrWhiteSpace(toolName))
                continue;

            results.Add(new VisualStudioToolResultRecord(toolName.Trim(), toolCallRecord?.ArgumentsJson ?? "", MessageContentText(message["content"])));
        }

        return results;
    }

    private static bool LooksLikeVisualStudioToolRequest(JsonObject openAiRequest, HashSet<string> availableTools)
    {
        if (availableTools.Contains("get_projects_in_solution") ||
            availableTools.Contains("update_plan_progress") ||
            availableTools.Contains("replace_string_in_file"))
        {
            return true;
        }

        string text = CollectUserFacingRequestText(openAiRequest);
        return text.Contains("# IDESTATE CONTEXT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("solution file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDotNetModernizationToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return DotNetModernizationToolNames.Contains(name.Trim());
    }

    private static bool IsGeneralVisualStudioImplementationToolName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return GeneralVisualStudioImplementationToolNames.Contains(name.Trim());
    }

    private static readonly HashSet<string> GeneralVisualStudioImplementationToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "code_search",
        "get_errors",
        "file_search",
        "get_files_in_project",
        "get_projects_in_solution",
        "run_build",
        "remove_file",
        "create_file",
        "plan",
        "update_plan_progress",
        "finish_plan",
        "record_observation",
        "adapt_plan",
        "signal_plan_ready",
        "clarify_requirements",
        "get_output_window_logs",
        "run_tests",
        "get_tests",
        "find_symbol",
        "grep_search",
        "get_file",
        "replace_string_in_file",
        "multi_replace_string_in_file",
        "task_complete"
    };

    private static readonly HashSet<string> VisualStudioLocalRuntimeToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_projects_in_solution",
        "get_files_in_project",
        "file_search",
        "grep_search",
        "get_file",
        "create_file",
        "replace_string_in_file",
        "multi_replace_string_in_file",
        "get_errors",
        "run_build",
        "run_tests",
        "update_plan_progress",
        "task_complete"
    };

    private static readonly HashSet<string> DotNetModernizationToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "start_modernization",
        "initialize_scenario",
        "start_task",
        "complete_task",
        "break_down_task",
        "get_state",
        "resume_scenario",
        "query_dotnet_assessment",
        "get_code_dependencies",
        "discover_upgrade_scenarios",
        "get_projects_in_topological_order",
        "get_instructions",
        "get_scenarios",
        "generate_dotnet_upgrade_assessment",
        "open_dashboard",
        "unload_project",
        "reload_project",
        "get_projects_info",
        "get_solution_path",
        "open_file_in_editor",
        "add_existing_project",
        "set_startup_project"
    };

    private static bool LooksLikeUserRequestedDotNetModernization(JsonObject openAiRequest)
    {
        string text = CollectUserFacingRequestText(openAiRequest);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"\b(moderniz(e|ation)|migrat(e|ion)|upgrade|sdk-style|target framework|\.net framework|net[0-9]+\.[0-9]+|net[0-9]+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeImplementationPlanRequest(JsonObject openAiRequest)
    {
        string text = CollectUserFacingRequestText(openAiRequest);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"\bimplement(ing)?\s+(the\s+)?plan\b|\bimplementation\s+plan\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeVisualStudioProjectReadmeSummaryRequest(JsonObject openAiRequest)
    {
        string text = CollectUserFacingRequestText(openAiRequest);
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"\bsummariz(e|ing)|\boverview\b|\bdescribe\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            Regex.IsMatch(text, @"\bproject\b|\bsolution\b|\bcodebase\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            Regex.IsMatch(text, @"\breadme(?:\.md)?\b|\bsummary(?:\.md)?\b|[A-Za-z0-9_.-]*summary[A-Za-z0-9_.-]*\.md\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) &&
            Regex.IsMatch(text, @"\bsave\b|\bwrite\b|\bcreate\b|\bupdate\b|\bto\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string CollectUserFacingRequestText(JsonObject openAiRequest)
    {
        var builder = new StringBuilder();
        AppendIfText(builder, FirstString(openAiRequest, "prompt", "query"));
        AppendUserTextFromMessages(openAiRequest["messages"], builder);
        AppendUserTextFromMessages(openAiRequest["input"], builder);
        return builder.ToString();
    }

    private static string CollectVisualStudioToolResultText(JsonObject openAiRequest)
    {
        var builder = new StringBuilder();
        foreach (JsonObject message in EnumerateVisualStudioMessages(openAiRequest["messages"]).Concat(EnumerateVisualStudioMessages(openAiRequest["input"])))
        {
            string role = FirstString(message, "role");
            if (!role.Equals("tool", StringComparison.OrdinalIgnoreCase) &&
                !role.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendIfText(builder, MessageContentText(message["content"]));
        }

        return builder.ToString();
    }

    private static void AppendUserTextFromMessages(JsonNode? node, StringBuilder builder)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                AppendUserTextFromMessages(item, builder);
            return;
        }

        if (node is not JsonObject obj)
            return;

        string role = FirstString(obj, "role");
        if (!string.Equals(role, "user", StringComparison.OrdinalIgnoreCase))
            return;

        AppendIfText(builder, MessageContentText(obj["content"]));
    }

    private static void AppendIfText(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (builder.Length > 0)
            builder.AppendLine();
        builder.Append(value);
    }

    private static JsonObject? BuildCompactToolParameters(JsonObject? parameters)
    {
        if (parameters == null)
            return null;

        var compact = new JsonObject
        {
            ["type"] = FirstNonEmpty(FirstString(parameters, "type"), "object")
        };
        if (parameters["required"] is JsonArray required)
            compact["required"] = CloneArray(required);

        if (parameters["properties"] is JsonObject properties)
        {
            var compactProperties = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> property in properties)
            {
                if (property.Value is not JsonObject propertyObject)
                {
                    compactProperties[property.Key] = new JsonObject { ["type"] = "string" };
                    continue;
                }

                var compactProperty = new JsonObject
                {
                    ["type"] = FirstNonEmpty(FirstString(propertyObject, "type"), "string")
                };
                if (propertyObject["enum"] is JsonArray enumValues && enumValues.Count <= 20)
                    compactProperty["enum"] = CloneArray(enumValues);
                compactProperties[property.Key] = compactProperty;
            }

            compact["properties"] = compactProperties;
        }

        return compact;
    }

    private static string TruncateToolText(string value, int maxLength)
    {
        string text = Regex.Replace(value ?? "", @"\s+", " ", RegexOptions.CultureInvariant).Trim();
        if (text.Length <= maxLength)
            return text;
        return text[..Math.Max(0, maxLength - 1)].TrimEnd() + "...";
    }

    private static void CompactVisualStudioMessagesForLocalRuntime(JsonObject payload)
    {
        if (payload["messages"] is not JsonArray messages || messages.Count == 0)
            return;

        bool toolTurn = payload["tools"] is JsonArray tools && tools.Count > 0;
        int totalChars = messages.OfType<JsonObject>().Sum(MessageContentLength);
        if (totalChars <= (toolTurn ? 5000 : 12000))
            return;
        if (toolTurn)
        {
            payload["messages"] = BuildCompactVisualStudioToolTurnMessages(messages);
            return;
        }

        var selected = new SortedSet<int>();
        var systemParts = new List<string>();
        var systemIndexes = new HashSet<int>();
        for (int i = 0; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject message)
                continue;

            string role = FirstString(message, "role");
            string content = FirstString(message, "content");
            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(content))
                    systemParts.Add(content);
                systemIndexes.Add(i);
                continue;
            }

            if (IsImportantVisualStudioContextMessage(content))
                selected.Add(i);
        }

        int recentToKeep = Math.Min(messages.Count, toolTurn ? 4 : 6);
        for (int i = Math.Max(0, messages.Count - recentToKeep); i < messages.Count; i++)
        {
            if (!toolTurn || !systemIndexes.Contains(i))
                selected.Add(i);
        }

        var compacted = new JsonArray();
        if (systemParts.Count > 0)
        {
            compacted.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = TruncateMiddle(string.Join("\n\n", systemParts), toolTurn ? 900 : 1400)
            });
        }

        foreach (int index in selected)
        {
            if (messages[index] is not JsonObject original)
                continue;

            var clone = CloneObject(original);
            string content = FirstString(clone, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                int maxLength;
                if (content.Contains("# FILE CONTEXT", StringComparison.OrdinalIgnoreCase))
                    maxLength = toolTurn ? 2200 : 4500;
                else if (content.Contains("# IDESTATE CONTEXT", StringComparison.OrdinalIgnoreCase))
                    maxLength = toolTurn ? 800 : 1800;
                else if (IsImportantVisualStudioContextMessage(content))
                    maxLength = toolTurn ? 1000 : 1800;
                else
                    maxLength = toolTurn ? 400 : 900;
                clone["content"] = TruncateMiddle(content, maxLength);
            }

            compacted.Add(clone);
        }

        payload["messages"] = compacted;
    }

    private static int MessageContentLength(JsonObject message)
    {
        string content = FirstString(message, "content");
        if (!string.IsNullOrEmpty(content))
            return content.Length;
        return MessageContentText(message["content"]).Length;
    }

    private static JsonArray BuildCompactVisualStudioToolTurnMessages(JsonArray messages)
    {
        var systemParts = new List<string>();
        string fileContext = "";
        string ideContext = "";
        string latestUserTask = "";

        foreach (JsonNode? node in messages)
        {
            if (node is not JsonObject message)
                continue;

            string role = FirstString(message, "role");
            string content = FirstString(message, "content");
            if (string.IsNullOrWhiteSpace(content))
                content = MessageContentText(message["content"]);

            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(content))
                    systemParts.Add(content);
                continue;
            }

            if (!role.Equals("user", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(content))
                continue;

            if (content.Contains("# FILE CONTEXT", StringComparison.OrdinalIgnoreCase))
                fileContext = content;
            else if (content.Contains("# IDESTATE CONTEXT", StringComparison.OrdinalIgnoreCase))
                ideContext = content;
            else if (!content.Contains("not yet marked the task as complete", StringComparison.OrdinalIgnoreCase))
                latestUserTask = content;
        }

        var compacted = new JsonArray
        {
            BuildMessage(
                "system",
                "Visual Studio local tool turn. Use exactly one valid function tool call unless the task is complete. Do not write hidden reasoning, markdown tool JSON, or prose before a tool call.\n\n" +
                TruncateMiddle(string.Join("\n\n", systemParts), 700)),
            BuildMessage(
                "user",
                "Task: " + TruncateMiddle(latestUserTask, 300) +
                "\n\nIDE context:\n" + TruncateMiddle(ideContext, 700) +
                "\n\nPlan/file context:\n" + TruncateMiddle(fileContext, 1800))
        };

        int start = Math.Max(0, messages.Count - 12);
        for (int i = start; i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject original)
                continue;

            string role = FirstString(original, "role");
            if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
                continue;

            string content = FirstString(original, "content");
            bool hasToolCalls = original["tool_calls"] is JsonArray toolCalls && toolCalls.Count > 0;
            bool keep =
                role.Equals("assistant", StringComparison.OrdinalIgnoreCase) && (hasToolCalls || content.Contains("malformed JSON", StringComparison.OrdinalIgnoreCase) || content.Contains("did not produce a response", StringComparison.OrdinalIgnoreCase)) ||
                role.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
                role.Equals("function", StringComparison.OrdinalIgnoreCase) ||
                role.Equals("user", StringComparison.OrdinalIgnoreCase) && content.Contains("not yet marked the task as complete", StringComparison.OrdinalIgnoreCase);
            if (!keep)
                continue;

            var clone = CloneObject(original);
            string cloneContent = FirstString(clone, "content");
            if (!string.IsNullOrWhiteSpace(cloneContent))
                clone["content"] = TruncateMiddle(cloneContent, 500);
            compacted.Add(clone);
        }

        return compacted;
    }

    private static bool IsImportantVisualStudioContextMessage(string content)
    {
        string text = content ?? "";
        return text.Contains("# FILE CONTEXT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("# IDESTATE CONTEXT", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("implementation plan", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("workspace root path", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("solution file", StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateMiddle(string value, int maxLength)
    {
        string text = value ?? "";
        if (text.Length <= maxLength)
            return text;

        int head = Math.Max(0, maxLength / 2);
        int tail = Math.Max(0, maxLength - head - 42);
        return text[..head].TrimEnd() +
            "\n\n[...compacted by SocketJack bridge...]\n\n" +
            text[^tail..].TrimStart();
    }

    private static JsonArray BuildChatMessages(JsonObject openAiRequest)
    {
        if (openAiRequest["messages"] is JsonArray messages && messages.Count > 0)
            return CloneArray(messages);

        var result = new JsonArray();
        AddMessagesFromResponsesInput(openAiRequest["input"], result);
        if (result.Count == 0)
        {
            string prompt = FirstString(openAiRequest, "prompt", "query");
            if (!string.IsNullOrWhiteSpace(prompt))
                result.Add(BuildMessage("user", prompt));
        }

        return result;
    }

    private static void AddMessagesFromResponsesInput(JsonNode? input, JsonArray messages)
    {
        if (input == null)
            return;

        if (input is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (input is JsonArray array)
        {
            foreach (JsonNode? item in array)
                AddResponsesInputItem(item, messages);
            return;
        }

        AddResponsesInputItem(input, messages);
    }

    private static void AddResponsesInputItem(JsonNode? item, JsonArray messages)
    {
        if (item == null)
            return;

        if (item is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (item is JsonArray array)
        {
            string text = MessageContentText(array);
            if (!string.IsNullOrWhiteSpace(text))
                messages.Add(BuildMessage("user", text));
            return;
        }

        if (item is not JsonObject obj)
            return;

        string type = FirstString(obj, "type");
        string role = NormalizeChatRole(FirstString(obj, "role"));
        if (string.IsNullOrWhiteSpace(role))
            role = type.Equals("message", StringComparison.OrdinalIgnoreCase) ? "user" : "user";

        string content = MessageContentText(obj["content"]);
        if (string.IsNullOrWhiteSpace(content))
            content = FirstChatTextValue(obj, "text", "input_text", "output_text", "summary", "content", "body", "value");
        if (string.IsNullOrWhiteSpace(content))
            return;

        messages.Add(BuildMessage(role, content));
    }

    private static JsonObject BuildMessage(string role, string content)
    {
        return new JsonObject
        {
            ["role"] = NormalizeChatRole(role),
            ["content"] = content
        };
    }

    private static string NormalizeChatRole(string role)
    {
        string value = (role ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            "assistant" => "assistant",
            "system" => "system",
            "developer" => "system",
            "tool" => "tool",
            "function" => "tool",
            _ => "user"
        };
    }

    private static void AddVisualStudioFeatureMessages(JsonObject openAiRequest, JsonArray messages)
    {
        string instructions = FirstNonEmpty(
            FirstString(openAiRequest, "instructions"),
            FirstString(openAiRequest, "developer"),
            FirstString(openAiRequest, "system"));
        if (!string.IsNullOrWhiteSpace(instructions))
            messages.Insert(0, BuildMessage("system", instructions));

        string featureInstruction = BuildVisualStudioFeatureInstruction(openAiRequest);
        if (!string.IsNullOrWhiteSpace(featureInstruction))
            messages.Insert(0, BuildMessage("system", featureInstruction));
    }

    private static void AddVisualStudioReferenceFileMessage(JsonObject openAiRequest, JsonArray messages)
    {
        JsonArray files = ExtractVisualStudioReferenceFiles(openAiRequest);
        if (files.Count == 0)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("Visual Studio reference files:");
        foreach (JsonNode? node in files)
        {
            if (node is not JsonObject file)
                continue;

            string name = FirstNonEmpty(FirstString(file, "relativePath"), FirstString(file, "name"), "reference");
            string text = FirstString(file, "text", "content");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            builder.AppendLine();
            builder.AppendLine("File: " + name);
            builder.AppendLine("```");
            builder.AppendLine(text);
            builder.AppendLine("```");
        }

        string message = builder.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(message))
            messages.Add(BuildMessage("system", message));
    }

    private sealed record VisualStudioToolCallRecord(string Id, string Name, string ArgumentsJson);

    private static void AddVisualStudioToolRecoveryMessage(JsonObject openAiRequest, JsonArray messages)
    {
        List<string> hints = BuildVisualStudioToolRecoveryHints(openAiRequest);
        if (hints.Count == 0)
            return;

        var builder = new StringBuilder();
        builder.AppendLine("Recent Visual Studio tool recovery guidance:");
        foreach (string hint in hints.Take(5))
            builder.AppendLine("- " + hint);
        messages.Add(BuildMessage("system", builder.ToString().Trim()));
    }

    private static List<string> BuildVisualStudioToolRecoveryHints(JsonObject openAiRequest)
    {
        var toolCallsById = new Dictionary<string, VisualStudioToolCallRecord>(StringComparer.OrdinalIgnoreCase);
        var hints = new List<string>();
        var seenHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var successfulGetFileReads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string lastProjectPath = "";
        bool preferXamlCodeBehindForVbFiles = ShouldPreferVisualStudioXamlCodeBehindForVbFiles(openAiRequest);

        foreach (JsonObject message in EnumerateVisualStudioMessages(openAiRequest["messages"]).Concat(EnumerateVisualStudioMessages(openAiRequest["input"])))
        {
            string role = FirstString(message, "role");
            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            {
                string assistantContent = MessageContentText(message["content"]);
                if (assistantContent.Contains("malformed JSON", StringComparison.OrdinalIgnoreCase))
                {
                    const string malformedHint = "The previous tool call was malformed. Retry with a strict function tool call only; do not include prose, markdown, or partial JSON.";
                    if (seenHints.Add(malformedHint))
                        hints.Add(malformedHint);
                }

                foreach (VisualStudioToolCallRecord toolCall in EnumerateVisualStudioToolCalls(message["tool_calls"]))
                {
                    if (!string.IsNullOrWhiteSpace(toolCall.Id))
                        toolCallsById[toolCall.Id] = toolCall;
                }

                continue;
            }

            if (!role.Equals("tool", StringComparison.OrdinalIgnoreCase) && !role.Equals("function", StringComparison.OrdinalIgnoreCase))
                continue;

            string content = MessageContentText(message["content"]);
            string toolCallId = FirstString(message, "tool_call_id", "toolCallId", "id");
            toolCallsById.TryGetValue(toolCallId, out VisualStudioToolCallRecord? toolCallRecord);
            string toolName = FirstNonEmpty(toolCallRecord?.Name ?? "", FirstString(message, "name"));
            string filePath = FirstNonEmpty(
                TryReadStringArgument(toolCallRecord?.ArgumentsJson ?? "", "filePath", "filename", "path", "projectPath"),
                TryExtractExistingFilePath(content));
            bool repeatedSuccessfulGetFileRead = false;
            if (toolName.Equals("get_file", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(content) &&
                !IsFailedVisualStudioToolResult(content))
            {
                string normalizedReadPath = NormalizeVisualStudioPathForComparison(filePath);
                if (!string.IsNullOrWhiteSpace(normalizedReadPath))
                {
                    successfulGetFileReads.TryGetValue(normalizedReadPath, out int readCount);
                    successfulGetFileReads[normalizedReadPath] = readCount + 1;
                    repeatedSuccessfulGetFileRead = readCount + 1 >= 2;
                }
            }

            if (toolName.Equals("get_projects_in_solution", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(content))
            {
                lastProjectPath = FirstNonEmpty(FirstNonEmptyLine(content), lastProjectPath);
            }

            string? hint = null;
            if (toolName.Equals("create_file", StringComparison.OrdinalIgnoreCase) &&
                content.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                hint = "create_file for " + FormatToolPath(filePath) + " failed because the file already exists. Do not call create_file for that path again; call get_file first, then replace_string_in_file or multi_replace_string_in_file if an edit is needed. Do not call task_complete until a real edit, build, or plan progress update succeeds.";
            }
            else if ((toolName.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase)) &&
                IsFailedVisualStudioToolResult(content))
            {
                hint = toolName + " for " + FormatToolPath(filePath) + " failed. Do not call task_complete after a failed edit. Call get_file for the exact target path, use the returned text as the exact oldString, then retry replace_string_in_file or multi_replace_string_in_file.";
            }
            else if ((toolName.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase)) &&
                TryReadNoOpVisualStudioReplacePath(toolCallRecord?.ArgumentsJson ?? "", out _))
            {
                hint = toolName + " for " + FormatToolPath(filePath) + " was a no-op because the replacement text was empty or unchanged. Do not count that as a file edit; call get_file for the exact target path and retry with a non-empty oldString and changed newString.";
            }
            else if (toolName.Equals("get_file", StringComparison.OrdinalIgnoreCase) &&
                content.Contains("startLine must be >= 1", StringComparison.OrdinalIgnoreCase))
            {
                hint = "get_file line numbers are 1-based. Retry get_file with startLine 1 or greater.";
            }
            else if (toolName.Equals("get_file", StringComparison.OrdinalIgnoreCase) &&
                IsEmptyVisualStudioGetFileResult(content))
            {
                hint = string.IsNullOrWhiteSpace(lastProjectPath)
                    ? "get_file returned no content for " + FormatToolPath(filePath) + ". Use file_search/get_files_in_project to find the exact project-relative path before creating or editing files."
                    : "get_file returned no content for " + FormatToolPath(filePath) + ". Use the exact project-relative path under " + FormatToolPath(VisualStudioDirectoryName(lastProjectPath)) + " before creating or editing files.";
            }
            else if (repeatedSuccessfulGetFileRead)
            {
                hint = "get_file already returned content for " + FormatToolPath(filePath) + ". Do not read that same path again; either edit it with replace_string_in_file/multi_replace_string_in_file, or search/read a different target file. For implementation-plan work, do not build or update plan progress until a real file edit succeeds.";
            }
            else if (toolName.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(content))
            {
                hint = string.IsNullOrWhiteSpace(lastProjectPath)
                    ? "get_files_in_project returned no files for " + FormatToolPath(filePath) + ". Use the exact project path returned by get_projects_in_solution, not a shortened display name."
                    : "get_files_in_project returned no files for " + FormatToolPath(filePath) + ". Retry with the exact project path returned by get_projects_in_solution: " + FormatToolPath(lastProjectPath) + ".";
            }
            else if ((toolName.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
                string.IsNullOrWhiteSpace(content))
            {
                string extensionHint = ToolSearchArgumentsMentionVisualBasicMacroFiles(toolCallRecord?.ArgumentsJson ?? "")
                    ? " The search used macro-style file names; this is a Visual Studio VB/WPF project, so retry with .xaml and .xaml.vb names and/or list the exact project path first."
                    : "";
                hint = toolName + " returned no results." + extensionHint + " Do not edit or complete the task until a read tool returns the actual target file paths.";
            }
            else if (LooksLikeImplementationPlanRequest(openAiRequest) &&
                (toolName.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(content) &&
                TryExtractImplementationPlanPath(content, out string planPath))
            {
                hint = "The previous result includes an implementation plan file. Next call get_file for " + FormatToolPath(planPath) + " before reading arbitrary project files or editing code.";
            }
            else if (preferXamlCodeBehindForVbFiles &&
                (toolName.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
                    toolName.Equals("grep_search", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(content) &&
                TryExtractPreferredVisualStudioXamlPath(content, out string preferredPath))
            {
                hint = "The previous result includes WPF XAML/code-behind paths. Next call get_file for " + FormatToolPath(preferredPath) + " or another .xaml/.xaml.vb result; ignore standalone .vb duplicates unless the plan explicitly names a non-XAML class.";
            }
            else if (content.Contains("malformed JSON", StringComparison.OrdinalIgnoreCase))
            {
                hint = "The previous tool call was malformed. Retry with a strict function tool call only; do not include prose, markdown, or partial JSON.";
            }

            if (!string.IsNullOrWhiteSpace(hint) && seenHints.Add(hint))
                hints.Add(hint);
        }

        return hints;
    }

    private static bool TryExtractImplementationPlanPath(string content, out string planPath)
    {
        planPath = "";
        if (string.IsNullOrWhiteSpace(content))
            return false;

        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim().Trim('`');
            if (trimmed.Length == 0)
                continue;
            if (!trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                continue;
            if (trimmed.Contains("implementation", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("plan", StringComparison.OrdinalIgnoreCase))
            {
                planPath = trimmed;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractPreferredVisualStudioXamlPath(string content, out string preferredPath)
    {
        preferredPath = "";
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var candidates = new List<string>();
        foreach (string line in content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim().Trim('`');
            if (trimmed.Length == 0)
                continue;
            if (trimmed.Contains(".xaml.vb", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add(trimmed);
            }
        }

        preferredPath = candidates.FirstOrDefault(candidate => candidate.Contains(".xaml.vb", StringComparison.OrdinalIgnoreCase)) ??
            candidates.FirstOrDefault() ??
            "";
        return !string.IsNullOrWhiteSpace(preferredPath);
    }

    private static IEnumerable<JsonObject> EnumerateVisualStudioMessages(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                foreach (JsonObject message in EnumerateVisualStudioMessages(item))
                    yield return message;
            }

            yield break;
        }

        if (node is JsonObject obj)
            yield return obj;
    }

    private static IEnumerable<VisualStudioToolCallRecord> EnumerateVisualStudioToolCalls(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                foreach (VisualStudioToolCallRecord toolCall in EnumerateVisualStudioToolCalls(item))
                    yield return toolCall;
            }

            yield break;
        }

        if (node is not JsonObject obj)
            yield break;

        JsonObject? function = obj["function"] as JsonObject;
        string name = FirstNonEmpty(
            function == null ? "" : FirstString(function, "name"),
            FirstString(obj, "name"),
            FirstString(obj, "tool"),
            FirstString(obj, "id"));
        if (string.IsNullOrWhiteSpace(name))
            yield break;

        string arguments = FirstNonEmpty(
            function == null ? "" : FirstString(function, "arguments"),
            FirstString(obj, "arguments"),
            FirstString(obj, "args"));
        yield return new VisualStudioToolCallRecord(FirstString(obj, "id"), name.Trim(), arguments);
    }

    private static string TryReadStringArgument(string argumentsJson, params string[] names)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "";

        try
        {
            if (JsonNode.Parse(argumentsJson) is not JsonObject obj)
                return "";

            foreach (string name in names)
            {
                string value = FirstString(obj, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }
        catch (JsonException)
        {
        }

        return "";
    }

    private static bool ToolSearchArgumentsMentionVisualBasicMacroFiles(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        return argumentsJson.Contains(".vba", StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstNonEmptyLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        foreach (string line in value.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
                return trimmed;
        }

        return "";
    }

    private static string TryExtractExistingFilePath(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        Match match = Regex.Match(content, @"\bat\s+(?<path>.+?)\.\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["path"].Value.Trim() : "";
    }

    private static string FormatToolPath(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? "the requested path" : "'" + path.Trim() + "'";
    }

    private static string BuildVisualStudioFeatureInstruction(JsonObject openAiRequest)
    {
        var parts = new List<string>();
        string mode = DetectVisualStudioAiMode(openAiRequest);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            string behavior = mode switch
            {
                "Interactive" => "Ask before risky or user-visible write actions and keep the user in the loop.",
                "Bypass" => "Proceed without extra confirmation prompts when existing SocketJack and Visual Studio permission gates already allow the action.",
                "AutoPilot" => "Continue autonomously through safe read, edit, build, and verification steps until the requested task is handled. Batch independent context reads in one tool-call turn when possible, and after tool results return, continue from those results instead of restarting the plan.",
                _ => ""
            };
            parts.Add("Visual Studio AI mode: " + mode + ". " + behavior);
        }

        HashSet<string> availableTools = ReadAvailableToolNames(openAiRequest);
        if (availableTools.Count > 0)
        {
            parts.Add("When Visual Studio tools are available, call them through the provided tool-call protocol. Do not print raw tool JSON, XML, or fenced tool-call snippets as assistant text.");
            parts.Add("For tool-enabled Visual Studio turns, do not write hidden reasoning, analysis, or <think> tags. Choose the next useful tool call directly, or answer briefly only when no tool call is needed.");
            parts.Add("Use exact file and project paths returned by Visual Studio tools. If a tool result says a file already exists or the call failed, do not retry the same tool with unchanged arguments; inspect the existing file and use an edit tool such as replace_string_in_file or multi_replace_string_in_file when changes are still needed.");
            parts.Add("For get_files_in_project, pass the exact project path returned by get_projects_in_solution, including folders and the project file extension.");
            parts.Add("After get_files_in_project, file_search, or grep_search returns results, call get_file for the exact target file before creating or editing files.");
            if (!LooksLikeUserRequestedDotNetModernization(openAiRequest) && availableTools.Any(IsDotNetModernizationToolName))
            {
                parts.Add("This is a general Visual Studio coding request, not a .NET modernization workflow. Ignore modernization workflow prompts and use the normal code, file, plan, build, and test tools unless the user explicitly asks to modernize, migrate, or upgrade the project.");
            }
            if (LooksLikeImplementationPlanRequest(openAiRequest))
            {
                parts.Add("For an 'Implement the plan' request, do not ask the user what the plan is while workspace tools are available. If the active document is not clearly the plan, locate the implementation plan with get_files_in_project, file_search, grep_search, or get_file, then implement the concrete steps from that file.");
                parts.Add("If a project file list or search result includes a markdown file whose path contains 'plan' or 'implementation', read that plan file next before inspecting arbitrary project files.");
                parts.Add("For implementation-plan work, treat the supplied FILE CONTEXT implementation plan as authoritative when present. Read the actual project files named by that plan, then use edit tools for existing files. Do not create a file after Visual Studio says that file already exists.");
                parts.Add("For Visual Basic WPF/XAML projects, plan text that says VBA, Xaml.Vb, or WindowName.Vb usually means the Visual Basic code-behind file such as WindowName.xaml.vb. Keep WPF controls as classes/partial classes that match their XAML, not WinForms modules.");
            }
            if (availableTools.Contains("update_plan_progress"))
            {
                parts.Add("When implementing a plan and Visual Studio provides a plan-progress tool, record genuine progress after each successful create_file, replace_string_in_file, multi_replace_string_in_file, or remove_file step before continuing. Build/test progress only counts after a real file mutation has already succeeded. If the requested work is already present, update the plan progress to reflect only the steps you actually verified before calling task_complete.");
            }
            if (availableTools.Contains("task_complete"))
            {
                parts.Add("Never call task_complete immediately after a failed tool result, failed edit, failed build, or unmatched replacement. First fix the failed step or verify successful progress with Visual Studio tools.");
                parts.Add("For implementation-plan requests, do not call task_complete after only update_plan_progress, run_build, or empty search results. Complete only after a successful file create, edit, or removal, or after reading the actual files and verifying that no file change is required.");
            }
        }

        string summary = FirstNonEmpty(
            FirstString(openAiRequest, "summary"),
            FirstString(openAiRequest, "conversation_summary"),
            FirstString(openAiRequest, "context_summary"),
            FirstString(openAiRequest, "workspace_summary"),
            FirstStringFromChild(openAiRequest, "metadata", "summary", "conversation_summary", "context_summary"),
            FirstStringFromChild(openAiRequest, "context", "summary", "conversation_summary", "workspace_summary"));
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add("Visual Studio supplied context summary: " + summary);

        return string.Join("\n\n", parts);
    }

    private static string DetectVisualStudioAiMode(JsonObject openAiRequest)
    {
        foreach (JsonObject candidate in CandidateVisualStudioFeatureObjects(openAiRequest))
        {
            string direct = FirstNonEmpty(
                FirstString(candidate, "mode"),
                FirstString(candidate, "chatMode"),
                FirstString(candidate, "chat_mode"),
                FirstString(candidate, "interactionMode"),
                FirstString(candidate, "interaction_mode"),
                FirstString(candidate, "agentMode"),
                FirstString(candidate, "agent_mode"),
                FirstString(candidate, "approvalMode"),
                FirstString(candidate, "approval_mode"),
                FirstString(candidate, "automationMode"),
                FirstString(candidate, "automation_mode"),
                FirstString(candidate, "copilotMode"),
                FirstString(candidate, "copilot_mode"),
                FirstString(candidate, "visualStudioMode"),
                FirstString(candidate, "visual_studio_mode"));
            string mode = CanonicalVisualStudioAiMode(direct);
            if (!string.IsNullOrWhiteSpace(mode))
                return mode;
        }

        return "";
    }

    private static IEnumerable<JsonObject> CandidateVisualStudioFeatureObjects(JsonObject openAiRequest)
    {
        yield return openAiRequest;

        foreach (string name in new[] { "metadata", "context", "options", "settings", "copilot", "visualStudio", "visual_studio", "vs" })
        {
            if (openAiRequest[name] is JsonObject obj)
                yield return obj;
        }
    }

    private static string CanonicalVisualStudioAiMode(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";
        if (Regex.IsMatch(text, @"\binteractive\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Interactive";
        if (Regex.IsMatch(text, @"\bbypass\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Bypass";
        if (Regex.IsMatch(text, @"\bauto\s*pilot\b|\bautopilot\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "AutoPilot";
        return "";
    }

    public static bool WantsStreaming(JsonObject openAiRequest)
    {
        return openAiRequest["stream"] is JsonValue value &&
            value.TryGetValue(out bool stream) &&
            stream;
    }

    public static string ExtractAssistantText(string chatStreamEvents)
    {
        if (string.IsNullOrWhiteSpace(chatStreamEvents))
            return "";

        var builder = new StringBuilder();
        var assistantText = new SocketJackAssistantTextAccumulator();
        string[] lines = chatStreamEvents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                line = line.Substring(5).Trim();
            if (line.Length == 0 || line.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                string content = ExtractAssistantDeltaText(line);
                content = assistantText.AcceptDeltaOrSnapshot(content);
                if (!string.IsNullOrEmpty(content))
                    builder.Append(content);
            }
            catch (JsonException)
            {
                if (!IsInternalProgressLikeText(line) && !LooksLikeRawStructuredPayload(line))
                {
                    string content = assistantText.AcceptDeltaOrSnapshot(line);
                    if (!string.IsNullOrEmpty(content))
                        builder.AppendLine(content);
                }
            }
        }

        return NormalizeVisibleAssistantText(builder.ToString());
    }

    public static string ExtractAssistantDeltaText(string chatStreamEvent)
    {
        if (string.IsNullOrEmpty(chatStreamEvent))
            return "";

        string line = chatStreamEvent;
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return "";

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int dataIndex = line.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
            line = dataIndex >= 0 ? line.Substring(dataIndex + 5) : trimmed.Substring(5);
            if (line.StartsWith(" ", StringComparison.Ordinal))
                line = line.Substring(1);
            trimmed = line.Trim();
        }

        if (trimmed.Length == 0 || trimmed.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return "";

        try
        {
            var builder = new StringBuilder();
            AppendChatAssistantText(JsonNode.Parse(trimmed), builder, preserveTokenWhitespace: true);
            return StripHiddenAssistantMarkup(builder.ToString());
        }
        catch (JsonException)
        {
            if (!IsInternalProgressLikeText(line) && !LooksLikeRawStructuredPayload(line) && !LooksLikeOrphanStructuredTailFragment(line))
                return line;
        }

        return "";
    }

    public static string ExtractAssistantRawDeltaText(string chatStreamEvent)
    {
        if (string.IsNullOrEmpty(chatStreamEvent))
            return "";

        string line = chatStreamEvent;
        string trimmed = line.Trim();
        if (trimmed.Length == 0)
            return "";

        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int dataIndex = line.IndexOf("data:", StringComparison.OrdinalIgnoreCase);
            line = dataIndex >= 0 ? line.Substring(dataIndex + 5) : trimmed.Substring(5);
            if (line.StartsWith(" ", StringComparison.Ordinal))
                line = line.Substring(1);
            trimmed = line.Trim();
        }

        if (trimmed.Length == 0 || trimmed.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return "";

        try
        {
            JsonNode? node = JsonNode.Parse(trimmed);
            return FirstRawAssistantDeltaText(node);
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string FirstRawAssistantDeltaText(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value)
            return value.TryGetValue(out string? text) ? text ?? "" : "";

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                string text = FirstRawAssistantDeltaText(item);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }

            return "";
        }

        if (node is not JsonObject obj)
            return "";

        if (obj["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choice in choices)
            {
                string text = FirstRawAssistantDeltaText(choice);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        foreach (string childName in new[] { "delta", "message", "response", "data", "result" })
        {
            if (obj[childName] != null)
            {
                string text = FirstRawAssistantDeltaText(obj[childName]);
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }

        return FirstChatTextValue(obj, true, "content", "text", "body", "value", "output_text", "outputText");
    }

    public static string BuildVisibleChatStreamUpdate(JsonObject evt)
    {
        if (evt == null)
            return "";

        string type = FirstString(evt, "type");
        if (type.Equals("usage", StringComparison.OrdinalIgnoreCase))
            return "";

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        if (type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        return "";
    }

    public static string ToSafeMarkdownStatus(string value)
    {
        return FirstStatusText(new JsonObject { ["value"] = value }, "value");
    }

    public static bool IsNoAssistantTextError(string value)
    {
        string text = value ?? "";
        return text.Contains("completed without returning assistant text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("returned no assistant text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("no assistant content", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNoVisibleAssistantFallbackText(string value)
    {
        string text = value ?? "";
        return (text.Contains("model finished without a visible reply", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("enabled chat model", StringComparison.OrdinalIgnoreCase)) ||
            text.Contains("SocketJack bridge did not receive visible assistant text", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("used the response budget without producing visible assistant text", StringComparison.OrdinalIgnoreCase);
    }

    public static ProxyResponse BuildChatResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateCompletionId();
        var root = new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = assistantText ?? ""
                    },
                    ["finish_reason"] = "stop"
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = 0,
                ["completion_tokens"] = 0,
                ["total_tokens"] = 0
            }
        };

        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildStreamingChatResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateCompletionId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var builder = new StringBuilder();
        builder.Append(BuildStreamingDeltaSse(id, created, model, null, true, null));
        if (!string.IsNullOrEmpty(assistantText))
            builder.Append(BuildStreamingDeltaSse(id, created, model, assistantText, false, null));
        builder.Append(BuildStreamingDeltaSse(id, created, model, null, false, "stop"));
        builder.Append(DoneSse);

        return new ProxyResponse(
            StatusCodes.Status200OK,
            "OK",
            "text/event-stream",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-cache",
                ["X-Accel-Buffering"] = "no"
            },
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static ProxyResponse BuildResponseResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateResponseId();
        JsonObject root = BuildResponseObject(id, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model, "completed", assistantText ?? "");
        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildStreamingResponseResponse(JsonObject openAiRequest, string assistantText, string selectedModelId)
    {
        string model = GetResponseModel(openAiRequest, selectedModelId);
        string id = CreateResponseId();
        long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var builder = new StringBuilder();
        builder.Append(BuildResponseStreamStartSse(id, created, model));
        if (!string.IsNullOrEmpty(assistantText))
            builder.Append(BuildResponseTextDeltaSse(id, assistantText));
        builder.Append(BuildResponseStreamFinishSse(id, created, model, assistantText ?? ""));
        builder.Append(DoneSse);

        return new ProxyResponse(
            StatusCodes.Status200OK,
            "OK",
            "text/event-stream",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Cache-Control"] = "no-cache",
                ["X-Accel-Buffering"] = "no"
            },
            Encoding.UTF8.GetBytes(builder.ToString()));
    }

    public static string BuildResponseStreamStartSse(string id, long created, string model)
    {
        var builder = new StringBuilder();
        builder.Append(BuildResponseLifecycleStartSse(id, created, model));
        builder.Append(BuildResponseMessageStartSse(id));
        return builder.ToString();
    }

    public static string BuildResponseLifecycleStartSse(string id, long created, string model)
    {
        var builder = new StringBuilder();
        AppendEventSse(builder, "response.created", new JsonObject
        {
            ["type"] = "response.created",
            ["response"] = BuildResponseObject(id, created, model, "in_progress", "")
        });
        AppendEventSse(builder, "response.in_progress", new JsonObject
        {
            ["type"] = "response.in_progress",
            ["response"] = BuildResponseObject(id, created, model, "in_progress", "")
        });
        return builder.ToString();
    }

    public static string BuildResponseMessageStartSse(string id)
    {
        string messageId = ResponseMessageId(id);
        var builder = new StringBuilder();
        AppendEventSse(builder, "response.output_item.added", new JsonObject
        {
            ["type"] = "response.output_item.added",
            ["output_index"] = 0,
            ["item"] = new JsonObject
            {
                ["id"] = messageId,
                ["type"] = "message",
                ["status"] = "in_progress",
                ["role"] = "assistant",
                ["content"] = new JsonArray()
            }
        });
        AppendEventSse(builder, "response.content_part.added", new JsonObject
        {
            ["type"] = "response.content_part.added",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = new JsonObject
            {
                ["type"] = "output_text",
                ["text"] = ""
            }
        });
        return builder.ToString();
    }

    public static string BuildResponseTextDeltaSse(string id, string? content)
    {
        var builder = new StringBuilder();
        AppendEventSse(builder, "response.output_text.delta", new JsonObject
        {
            ["type"] = "response.output_text.delta",
            ["item_id"] = ResponseMessageId(id),
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["delta"] = content ?? ""
        });
        return builder.ToString();
    }

    public static string BuildResponseStreamFinishSse(string id, long created, string model, string assistantText)
    {
        string text = assistantText ?? "";
        string messageId = ResponseMessageId(id);
        var contentPart = new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text
        };
        var message = new JsonObject
        {
            ["id"] = messageId,
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray { CloneNode(contentPart) }
        };

        var builder = new StringBuilder();
        AppendEventSse(builder, "response.output_text.done", new JsonObject
        {
            ["type"] = "response.output_text.done",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["text"] = text
        });
        AppendEventSse(builder, "response.content_part.done", new JsonObject
        {
            ["type"] = "response.content_part.done",
            ["item_id"] = messageId,
            ["output_index"] = 0,
            ["content_index"] = 0,
            ["part"] = CloneNode(contentPart)
        });
        AppendEventSse(builder, "response.output_item.done", new JsonObject
        {
            ["type"] = "response.output_item.done",
            ["output_index"] = 0,
            ["item"] = CloneNode(message)
        });
        AppendEventSse(builder, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = BuildResponseObject(id, created, model, "completed", text)
        });
        return builder.ToString();
    }

    public static string BuildStreamingDeltaSse(string id, long created, string model, string? content, bool includeRole, string? finishReason)
    {
        var delta = new JsonObject();
        if (includeRole)
            delta["role"] = "assistant";
        if (content != null)
            delta["content"] = content;
        var builder = new StringBuilder();
        AppendSse(builder, BuildChunk(id, created, model, delta, finishReason));
        return builder.ToString();
    }

    public static IReadOnlyList<SocketJackOpenAiToolCall> NormalizeVisualStudioToolCalls(IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        return NormalizeVisualStudioToolCalls(toolCalls, "");
    }

    public static IReadOnlyList<SocketJackOpenAiToolCall> NormalizeVisualStudioToolCalls(IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, string visualStudioDefaultProjectPath)
    {
        return NormalizeVisualStudioToolCalls(toolCalls, visualStudioDefaultProjectPath, false);
    }

    public static IReadOnlyList<SocketJackOpenAiToolCall> NormalizeVisualStudioToolCalls(IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        List<SocketJackOpenAiToolCall>? normalized = null;
        for (int i = 0; i < toolCalls.Count; i++)
        {
            SocketJackOpenAiToolCall toolCall = toolCalls[i];
            string toolName = toolCall.Name;
            string argumentsJson = NormalizeVisualStudioToolArguments(toolName, toolCall.ArgumentsJson, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
            if (TryConvertNoOpVisualStudioReplaceToolCall(toolName, argumentsJson, out string convertedToolName, out string convertedArgumentsJson))
            {
                toolName = convertedToolName;
                argumentsJson = convertedArgumentsJson;
            }

            if (normalized == null &&
                (!string.Equals(toolName, toolCall.Name, StringComparison.Ordinal) ||
                    !string.Equals(argumentsJson, toolCall.ArgumentsJson, StringComparison.Ordinal)))
            {
                normalized = new List<SocketJackOpenAiToolCall>(toolCalls.Count);
                for (int j = 0; j < i; j++)
                    normalized.Add(toolCalls[j]);
            }

            normalized?.Add(normalized == null
                ? toolCall
                : new SocketJackOpenAiToolCall
                {
                    Id = toolCall.Id,
                    Name = toolName,
                    ArgumentsJson = argumentsJson
                });
        }

        return normalized ?? toolCalls;
    }

    private static string NormalizeVisualStudioToolArguments(string toolName, string argumentsJson, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return "{}";

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return argumentsJson;
        }

        if (node == null)
            return argumentsJson;

        bool changed = NormalizeVisualStudioToolArgumentNode(toolName, node, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
        return changed ? node.ToJsonString() : argumentsJson;
    }

    private static bool NormalizeVisualStudioToolArgumentNode(string toolName, JsonNode node, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        bool changed = false;
        if (node is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> property in obj.ToList())
            {
                JsonNode? value = property.Value;
                if (value == null)
                    continue;

                if (IsVisualStudioGetFileTool(toolName) &&
                    IsVisualStudioStartLineProperty(property.Key) &&
                    TryReadInteger(value, out long startLine) &&
                    startLine < 1)
                {
                    obj[property.Key] = 1;
                    changed = true;
                    value = obj[property.Key];
                }

                if (IsVisualStudioGetFilesInProjectTool(toolName) &&
                    IsVisualStudioProjectPathProperty(property.Key) &&
                    value is JsonValue projectPathValue &&
                    projectPathValue.TryGetValue(out string? projectPath) &&
                    ShouldReplaceVisualStudioProjectPath(projectPath ?? "", visualStudioDefaultProjectPath))
                {
                    obj[property.Key] = visualStudioDefaultProjectPath.Trim();
                    changed = true;
                    value = obj[property.Key];
                }

                if (IsVisualStudioFileMutationOrReadTool(toolName) &&
                    IsVisualStudioFilePathProperty(property.Key) &&
                    value is JsonValue filePathValue &&
                    filePathValue.TryGetValue(out string? filePath))
                {
                    string normalizedFilePath = NormalizeVisualStudioFilePath(filePath ?? "", visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
                    if (!string.Equals(normalizedFilePath, filePath, StringComparison.Ordinal))
                    {
                        obj[property.Key] = normalizedFilePath;
                        changed = true;
                        value = obj[property.Key];
                    }
                }

                if (IsVisualStudioUpdatePlanProgressTool(toolName) &&
                    property.Key.Equals("status", StringComparison.OrdinalIgnoreCase) &&
                    value is JsonValue statusValue &&
                    statusValue.TryGetValue(out string? status))
                {
                    string normalizedStatus = NormalizeVisualStudioPlanProgressStatus(status ?? "");
                    if (!string.Equals(normalizedStatus, status, StringComparison.Ordinal))
                    {
                        obj[property.Key] = normalizedStatus;
                        changed = true;
                        value = obj[property.Key];
                    }
                }

                if (value is JsonValue jsonValue &&
                    jsonValue.TryGetValue(out string? stringValue) &&
                    ShouldSanitizeVisualStudioToolArgumentText(property.Key))
                {
                    string sanitized = StripLeakedHiddenReasoningSuffix(stringValue ?? "");
                    if (!string.Equals(sanitized, stringValue, StringComparison.Ordinal))
                    {
                        obj[property.Key] = sanitized;
                        changed = true;
                        value = obj[property.Key];
                    }
                }

                if (IsVisualStudioFileSearchTool(toolName) &&
                    IsVisualStudioSearchQueriesProperty(property.Key) &&
                    value is JsonArray queries)
                {
                    changed |= NormalizeVisualStudioFileSearchQueries(queries, preferVisualStudioXamlCodeBehindForVbFiles);
                }

                if (IsVisualStudioGrepSearchTool(toolName) &&
                    IsVisualStudioIncludePatternProperty(property.Key) &&
                    value is JsonArray)
                {
                    obj[property.Key] = null;
                    changed = true;
                    value = obj[property.Key];
                }

                if (value != null)
                    changed |= NormalizeVisualStudioToolArgumentNode(toolName, value, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item != null)
                    changed |= NormalizeVisualStudioToolArgumentNode(toolName, item, visualStudioDefaultProjectPath, preferVisualStudioXamlCodeBehindForVbFiles);
            }
        }

        return changed;
    }

    private static bool IsVisualStudioGetFileTool(string toolName)
    {
        return toolName.Equals("get_file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioGetFilesInProjectTool(string toolName)
    {
        return toolName.Equals("get_files_in_project", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioFileSearchTool(string toolName)
    {
        return toolName.Equals("file_search", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("grep_search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioGrepSearchTool(string toolName)
    {
        return toolName.Equals("grep_search", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioFileMutationOrReadTool(string toolName)
    {
        return toolName.Equals("get_file", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("create_file", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("remove_file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioUpdatePlanProgressTool(string toolName)
    {
        return toolName.Equals("update_plan_progress", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertNoOpVisualStudioReplaceToolCall(string toolName, string argumentsJson, out string convertedToolName, out string convertedArgumentsJson)
    {
        convertedToolName = toolName;
        convertedArgumentsJson = argumentsJson;
        if (!IsVisualStudioReplaceTool(toolName) || !TryReadNoOpVisualStudioReplacePath(argumentsJson, out string filePath))
            return false;

        convertedToolName = "get_file";
        convertedArgumentsJson = new JsonObject
        {
            ["filename"] = filePath,
            ["startLine"] = 1,
            ["endLine"] = 120,
            ["includeLineNumbers"] = true
        }.ToJsonString();
        return true;
    }

    private static bool IsVisualStudioReplaceTool(string toolName)
    {
        return toolName.Equals("replace_string_in_file", StringComparison.OrdinalIgnoreCase) ||
            toolName.Equals("multi_replace_string_in_file", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadNoOpVisualStudioReplacePath(string argumentsJson, out string filePath)
    {
        filePath = "";
        if (string.IsNullOrWhiteSpace(argumentsJson))
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        if (node is not JsonObject obj)
            return false;

        filePath = FirstString(obj, "filePath", "file_path", "filename", "path");
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        if (IsNoOpVisualStudioReplacePair(
                FirstString(obj, "oldString", "old_string", "oldText", "old_text", "search", "text"),
                FirstString(obj, "newString", "new_string", "newText", "new_text", "replacement")))
        {
            return true;
        }

        if (obj["replacements"] is JsonArray replacements)
        {
            bool sawReplacement = false;
            foreach (JsonNode? item in replacements)
            {
                if (item is not JsonObject replacement)
                    continue;

                sawReplacement = true;
                if (!IsNoOpVisualStudioReplacePair(
                        FirstString(replacement, "oldString", "old_string", "oldText", "old_text", "search", "text"),
                        FirstString(replacement, "newString", "new_string", "newText", "new_text", "replacement")))
                {
                    return false;
                }
            }

            return sawReplacement;
        }

        return false;
    }

    private static bool IsNoOpVisualStudioReplacePair(string oldText, string newText)
    {
        return string.IsNullOrWhiteSpace(oldText) ||
            string.Equals(oldText, newText, StringComparison.Ordinal);
    }

    private static bool IsVisualStudioStartLineProperty(string propertyName)
    {
        return propertyName.Equals("startLine", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("start_line", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioProjectPathProperty(string propertyName)
    {
        return propertyName.Equals("projectPath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("project_path", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioFilePathProperty(string propertyName)
    {
        return propertyName.Equals("filePath", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("file_path", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("filename", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("path", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioSearchQueriesProperty(string propertyName)
    {
        return propertyName.Equals("queries", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVisualStudioIncludePatternProperty(string propertyName)
    {
        return propertyName.Equals("includePattern", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("include_pattern", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReplaceVisualStudioProjectPath(string candidate, string visualStudioDefaultProjectPath)
    {
        if (string.IsNullOrWhiteSpace(visualStudioDefaultProjectPath))
            return false;

        if (string.IsNullOrWhiteSpace(candidate))
            return true;

        string normalizedCandidate = NormalizeVisualStudioPathForComparison(candidate);
        string normalizedProjectPath = NormalizeVisualStudioPathForComparison(visualStudioDefaultProjectPath);
        if (normalizedCandidate.Equals(normalizedProjectPath, StringComparison.OrdinalIgnoreCase))
            return false;

        if (ContainsVisualStudioParentPathSegment(normalizedCandidate) ||
            !LooksLikeVisualStudioProjectFilePath(normalizedCandidate))
        {
            return true;
        }

        string leaf = LastVisualStudioPathSegment(normalizedProjectPath);
        string stem = Path.GetFileNameWithoutExtension(leaf);
        string directory = VisualStudioDirectoryName(normalizedProjectPath);
        string directoryAndStem = string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(stem)
            ? ""
            : NormalizeVisualStudioPathForComparison(Path.Combine(directory, stem));
        if (Path.IsPathRooted(normalizedCandidate))
        {
            string candidateLeaf = LastVisualStudioPathSegment(normalizedCandidate);
            return candidateLeaf.Equals(leaf, StringComparison.OrdinalIgnoreCase) ||
                candidateLeaf.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
                candidateLeaf.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.EndsWith("\\" + directory, StringComparison.OrdinalIgnoreCase) ||
                normalizedCandidate.EndsWith("\\" + directoryAndStem, StringComparison.OrdinalIgnoreCase);
        }

        return normalizedCandidate.Equals(leaf, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Equals(stem, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Equals(directoryAndStem, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.Equals(".", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsVisualStudioParentPathSegment(string path)
    {
        string normalized = NormalizeVisualStudioPathForComparison(path).Trim('\\');
        return normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("..\\", StringComparison.Ordinal) ||
            normalized.EndsWith("\\..", StringComparison.Ordinal) ||
            normalized.Contains("\\..\\", StringComparison.Ordinal);
    }

    private static bool LooksLikeVisualStudioProjectFilePath(string path)
    {
        string leaf = LastVisualStudioPathSegment(path);
        int dot = leaf.LastIndexOf('.');
        return dot >= 0 && leaf.Substring(dot).EndsWith("proj", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeVisualStudioPathForComparison(string path)
    {
        return (path ?? "").Trim().Replace('/', '\\');
    }

    private static string LastVisualStudioPathSegment(string path)
    {
        string normalized = NormalizeVisualStudioPathForComparison(path);
        int slash = normalized.LastIndexOf('\\');
        return slash < 0 ? normalized : normalized.Substring(slash + 1);
    }

    private static string VisualStudioDirectoryName(string path)
    {
        string normalized = NormalizeVisualStudioPathForComparison(path);
        int slash = normalized.LastIndexOf('\\');
        return slash < 0 ? "" : normalized.Substring(0, slash).TrimEnd('\\');
    }

    private static bool NormalizeVisualStudioFileSearchQueries(JsonArray queries, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replacementQueries = new JsonArray();
        bool changed = false;
        foreach (JsonNode? item in queries)
        {
            if (item is JsonValue jsonValue && jsonValue.TryGetValue(out string? query) && !string.IsNullOrWhiteSpace(query))
            {
                string trimmed = query.Trim();
                if (LooksLikeVisualStudioMacroFileName(trimmed, preferVisualStudioXamlCodeBehindForVbFiles) || LooksLikeVisualStudioWildcardFileSearch(trimmed))
                {
                    changed = true;
                    continue;
                }

                if (existing.Add(trimmed))
                    replacementQueries.Add(trimmed);
            }
        }

        var additions = new List<string>();
        foreach (JsonNode? item in queries)
        {
            if (item is not JsonValue jsonValue || !jsonValue.TryGetValue(out string? query) || string.IsNullOrWhiteSpace(query))
                continue;

            foreach (string variant in BuildVisualBasicMacroFileSearchVariants(query, preferVisualStudioXamlCodeBehindForVbFiles))
            {
                if (existing.Add(variant))
                    additions.Add(variant);
            }

            foreach (string variant in BuildVisualStudioWildcardFileSearchVariants(query))
            {
                if (existing.Add(variant))
                    additions.Add(variant);
            }
        }

        foreach (string addition in additions)
            replacementQueries.Add(addition);

        if (!changed && additions.Count == 0)
            return false;

        queries.Clear();
        foreach (JsonNode? item in replacementQueries)
            queries.Add(item?.DeepClone());

        return true;
    }

    private static IEnumerable<string> BuildVisualStudioWildcardFileSearchVariants(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || !LooksLikeVisualStudioWildcardFileSearch(query))
            yield break;

        string trimmed = query.Trim();
        string withoutWildcards = Regex.Replace(trimmed, @"[*?]+", "", RegexOptions.CultureInvariant).Trim();
        withoutWildcards = withoutWildcards.TrimEnd('.', '\\', '/');
        if (withoutWildcards.Length == 0)
            yield break;

        yield return withoutWildcards;

        string noLeadingDot = withoutWildcards.TrimStart('.');
        if (noLeadingDot.Length > 0 && !noLeadingDot.Equals(withoutWildcards, StringComparison.Ordinal))
            yield return noLeadingDot;
    }

    private static IEnumerable<string> BuildVisualBasicMacroFileSearchVariants(string query, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        if (string.IsNullOrWhiteSpace(query) || !LooksLikeVisualStudioMacroFileName(query, preferVisualStudioXamlCodeBehindForVbFiles))
            yield break;

        string trimmed = query.Trim();
        if (trimmed.Contains(".vba", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string extension in new[] { ".xaml.vb", ".xaml" })
            {
                string variant = Regex.Replace(trimmed, @"\.vba\b", extension, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                if (!variant.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                    yield return variant;
            }
        }

        if (ContainsVisualStudioUppercaseVbExtension(trimmed) ||
            (preferVisualStudioXamlCodeBehindForVbFiles && ContainsVisualStudioVbExtension(trimmed)))
        {
            RegexOptions options = preferVisualStudioXamlCodeBehindForVbFiles
                ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant;
            string vbPattern = preferVisualStudioXamlCodeBehindForVbFiles
                ? @"(?<!\.xaml)\.vb\b"
                : @"(?<!\.xaml)\.Vb\b";
            string xamlVbPattern = preferVisualStudioXamlCodeBehindForVbFiles
                ? @"\.xaml\.vb\b"
                : @"\.xaml\.Vb\b";
            string xamlCodeBehind = Regex.Replace(trimmed, vbPattern, ".xaml.vb", options);
            xamlCodeBehind = Regex.Replace(xamlCodeBehind, xamlVbPattern, ".xaml.vb", options);
            if (!xamlCodeBehind.Equals(trimmed, StringComparison.Ordinal))
                yield return xamlCodeBehind;

            string xaml = Regex.Replace(trimmed, vbPattern, ".xaml", options);
            if (!xaml.Equals(trimmed, StringComparison.Ordinal) &&
                !xaml.Equals(xamlCodeBehind, StringComparison.OrdinalIgnoreCase))
            {
                yield return xaml;
            }
        }
    }

    private static string NormalizeVisualStudioFilePath(string path, string visualStudioDefaultProjectPath, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        string normalized = NormalizeVisualStudioMacroFilePath(path, preferVisualStudioXamlCodeBehindForVbFiles);
        return ResolveVisualStudioProjectRelativeFilePath(normalized, visualStudioDefaultProjectPath);
    }

    private static string ResolveVisualStudioProjectRelativeFilePath(string path, string visualStudioDefaultProjectPath)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(visualStudioDefaultProjectPath))
            return path;

        string normalizedPath = NormalizeVisualStudioPathForComparison(path);
        string projectDirectory = VisualStudioDirectoryName(visualStudioDefaultProjectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
            return path;

        if (Path.IsPathRooted(normalizedPath))
        {
            string rootedProjectMarker = "\\" + projectDirectory.Trim('\\') + "\\";
            int projectMarkerIndex = normalizedPath.LastIndexOf(rootedProjectMarker, StringComparison.OrdinalIgnoreCase);
            if (projectMarkerIndex >= 0)
            {
                string tail = normalizedPath.Substring(projectMarkerIndex + rootedProjectMarker.Length).TrimStart('\\');
                if (string.IsNullOrWhiteSpace(tail))
                    return path;
                if (tail.StartsWith(projectDirectory + "\\", StringComparison.OrdinalIgnoreCase))
                    return tail;
                return projectDirectory + "\\" + tail;
            }

            return path;
        }

        string projectDirectoryPrefix = projectDirectory + "\\";
        string duplicatedProjectDirectoryPrefix = projectDirectory + "\\" + projectDirectory + "\\";
        if (normalizedPath.StartsWith(duplicatedProjectDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            return projectDirectoryPrefix + normalizedPath.Substring(duplicatedProjectDirectoryPrefix.Length);

        if (normalizedPath.StartsWith(projectDirectoryPrefix, StringComparison.OrdinalIgnoreCase))
            return normalizedPath;

        if (normalizedPath.StartsWith(".\\", StringComparison.Ordinal))
            normalizedPath = normalizedPath.Substring(2);

        if (normalizedPath.Contains('\\', StringComparison.Ordinal))
            return normalizedPath;

        return projectDirectory + "\\" + normalizedPath;
    }

    private static string NormalizeVisualStudioMacroFilePath(string path, bool preferVisualStudioXamlCodeBehindForVbFiles)
    {
        if (string.IsNullOrWhiteSpace(path) || !LooksLikeVisualStudioMacroFileName(path, preferVisualStudioXamlCodeBehindForVbFiles))
            return path;

        string normalized = Regex.Replace(path, @"\.vba\b", ".xaml.vb", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        RegexOptions vbOptions = preferVisualStudioXamlCodeBehindForVbFiles
            ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
            : RegexOptions.CultureInvariant;
        string vbPattern = preferVisualStudioXamlCodeBehindForVbFiles
            ? @"(?<!\.xaml)\.vb\b"
            : @"(?<!\.xaml)\.Vb\b";
        string xamlVbPattern = preferVisualStudioXamlCodeBehindForVbFiles
            ? @"\.xaml\.vb\b"
            : @"\.xaml\.Vb\b";
        normalized = Regex.Replace(normalized, vbPattern, ".xaml.vb", vbOptions);
        normalized = Regex.Replace(normalized, xamlVbPattern, ".xaml.vb", vbOptions);
        return normalized;
    }

    private static bool LooksLikeVisualStudioMacroFileName(string text, bool preferVisualStudioXamlCodeBehindForVbFiles = false)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            (text.Contains(".vba", StringComparison.OrdinalIgnoreCase) ||
                ContainsVisualStudioUppercaseVbExtension(text) ||
                (preferVisualStudioXamlCodeBehindForVbFiles && ContainsVisualStudioVbExtension(text)));
    }

    private static bool LooksLikeVisualStudioWildcardFileSearch(string text)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            (!text.Contains('*', StringComparison.Ordinal) && !text.Contains('?', StringComparison.Ordinal)))
        {
            return false;
        }

        string withoutWildcards = Regex.Replace(text.Trim(), @"[*?]+", "", RegexOptions.CultureInvariant)
            .Trim()
            .TrimEnd('.', '\\', '/');
        return withoutWildcards.Any(char.IsLetterOrDigit) ||
            (withoutWildcards.StartsWith(".", StringComparison.Ordinal) && withoutWildcards.Length > 1);
    }

    private static bool ContainsVisualStudioUppercaseVbExtension(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            Regex.IsMatch(text, @"\.Vb\b", RegexOptions.CultureInvariant);
    }

    private static bool ContainsVisualStudioVbExtension(string text)
    {
        return !string.IsNullOrWhiteSpace(text) &&
            Regex.IsMatch(text, @"\.vb\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string NormalizeVisualStudioPlanProgressStatus(string status)
    {
        string trimmed = (status ?? "").Trim();
        if (trimmed.Equals("in progress", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("in_progress", StringComparison.OrdinalIgnoreCase))
        {
            return "in-progress";
        }

        return trimmed;
    }

    private static bool TryReadInteger(JsonNode node, out long value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
            return false;

        if (jsonValue.TryGetValue(out long longValue))
        {
            value = longValue;
            return true;
        }

        if (jsonValue.TryGetValue(out int intValue))
        {
            value = intValue;
            return true;
        }

        if (jsonValue.TryGetValue(out string? stringValue) &&
            long.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    private static bool ShouldSanitizeVisualStudioToolArgumentText(string propertyName)
    {
        return propertyName.Equals("content", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("newString", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("new_string", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("newText", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("new_text", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("replacement", StringComparison.OrdinalIgnoreCase) ||
            propertyName.Equals("text", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripLeakedHiddenReasoningSuffix(string value)
    {
        int marker = value.IndexOf("<think", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return value;

        string suffix = value.Substring(marker);
        if (!LooksLikeLeakedHiddenReasoningSuffix(suffix))
            return value;

        return value.Substring(0, marker).TrimEnd();
    }

    private static bool LooksLikeLeakedHiddenReasoningSuffix(string suffix)
    {
        return suffix.Contains("</think>", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("Assistant requested tool", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("tool_call", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("previous response", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("previous tool call", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("failed because", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("malformed", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("strict function", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("I need to ", StringComparison.OrdinalIgnoreCase) ||
            suffix.Contains("Let me ", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<SocketJackOpenAiToolCall> AssignUniqueVisualStudioToolCallIds(IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, string scope)
    {
        if (toolCalls.Count == 0)
            return toolCalls;

        string safeScope = Regex.Replace(scope ?? "", @"[^A-Za-z0-9]", "", RegexOptions.CultureInvariant);
        if (safeScope.Length > 24)
            safeScope = safeScope[^24..];
        if (string.IsNullOrWhiteSpace(safeScope))
            safeScope = Guid.NewGuid().ToString("N")[..12];

        var result = new List<SocketJackOpenAiToolCall>(toolCalls.Count);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            SocketJackOpenAiToolCall toolCall = toolCalls[i];
            result.Add(new SocketJackOpenAiToolCall
            {
                Id = "call_sj_" + safeScope + "_" + i.ToString(CultureInfo.InvariantCulture),
                Name = toolCall.Name,
                ArgumentsJson = toolCall.ArgumentsJson
            });
        }

        return result;
    }

    public static string BuildStreamingToolCallsSse(string id, long created, string model, IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < toolCalls.Count; i++)
        {
            SocketJackOpenAiToolCall toolCall = toolCalls[i];
            var delta = new JsonObject
            {
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = i,
                        ["id"] = string.IsNullOrWhiteSpace(toolCall.Id) ? CreateToolCallId() : toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson
                        }
                    }
                }
            };
            AppendSse(builder, BuildChunk(id, created, model, delta, null));
        }

        AppendSse(builder, BuildChunk(id, created, model, new JsonObject(), "tool_calls"));
        builder.Append(DoneSse);
        return builder.ToString();
    }

    public static string BuildResponseToolCallsSse(string id, long created, string model, IReadOnlyList<SocketJackOpenAiToolCall> toolCalls, bool includeLifecycle = true)
    {
        var builder = new StringBuilder();
        if (includeLifecycle)
        {
            AppendEventSse(builder, "response.created", new JsonObject
            {
                ["type"] = "response.created",
                ["response"] = BuildResponseObject(id, created, model, "in_progress", "", new JsonArray())
            });
            AppendEventSse(builder, "response.in_progress", new JsonObject
            {
                ["type"] = "response.in_progress",
                ["response"] = BuildResponseObject(id, created, model, "in_progress", "", new JsonArray())
            });
        }

        var output = new JsonArray();
        for (int i = 0; i < toolCalls.Count; i++)
        {
            SocketJackOpenAiToolCall toolCall = toolCalls[i];
            string callId = string.IsNullOrWhiteSpace(toolCall.Id) ? CreateToolCallId() : toolCall.Id;
            string itemId = ResponseFunctionCallItemId(callId);
            string arguments = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson;
            var addedItem = new JsonObject
            {
                ["id"] = itemId,
                ["type"] = "function_call",
                ["status"] = "in_progress",
                ["call_id"] = callId,
                ["name"] = toolCall.Name,
                ["arguments"] = ""
            };
            var doneItem = new JsonObject
            {
                ["id"] = itemId,
                ["type"] = "function_call",
                ["status"] = "completed",
                ["call_id"] = callId,
                ["name"] = toolCall.Name,
                ["arguments"] = arguments
            };

            output.Add(CloneNode(doneItem));
            AppendEventSse(builder, "response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added",
                ["response_id"] = id,
                ["output_index"] = i,
                ["item"] = CloneNode(addedItem)
            });
            AppendEventSse(builder, "response.function_call_arguments.delta", new JsonObject
            {
                ["type"] = "response.function_call_arguments.delta",
                ["response_id"] = id,
                ["item_id"] = itemId,
                ["output_index"] = i,
                ["delta"] = arguments
            });
            AppendEventSse(builder, "response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done",
                ["response_id"] = id,
                ["item_id"] = itemId,
                ["output_index"] = i,
                ["arguments"] = arguments
            });
            AppendEventSse(builder, "response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done",
                ["response_id"] = id,
                ["output_index"] = i,
                ["item"] = CloneNode(doneItem)
            });
        }

        AppendEventSse(builder, "response.completed", new JsonObject
        {
            ["type"] = "response.completed",
            ["response"] = BuildResponseObject(id, created, model, "completed", "", output)
        });
        builder.Append(DoneSse);
        return builder.ToString();
    }

    private static string CreateToolCallId()
    {
        return "call_socketjack_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
    }

    private static string ResponseFunctionCallItemId(string callId)
    {
        string value = Regex.Replace(callId ?? "", @"[^A-Za-z0-9_]", "_", RegexOptions.CultureInvariant);
        return string.IsNullOrWhiteSpace(value) ? "fc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) : "fc_" + value;
    }

    public static ProxyResponse BuildModelsResponse(string selectedModelId)
    {
        string model = string.IsNullOrWhiteSpace(selectedModelId) ? "socketjack-model" : selectedModelId.Trim();
        var root = new JsonObject
        {
            ["object"] = "list",
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = model,
                    ["object"] = "model",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["owned_by"] = "socketjack"
                }
            }
        };

        return JsonResponse(StatusCodes.Status200OK, root);
    }

    public static ProxyResponse BuildErrorResponse(int statusCode, string message)
    {
        var root = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["message"] = string.IsNullOrWhiteSpace(message) ? "SocketJack bridge error." : message,
                ["type"] = "socketjack_bridge_error",
                ["code"] = "socketjack_bridge_error"
            }
        };

        return JsonResponse(statusCode, root);
    }

    public static string BuildServerOfflineAssistantText(string serverName)
    {
        string displayName = FormatServerDisplayName(serverName);
        return displayName + " server is offline, please choose a different server." + Environment.NewLine + Environment.NewLine +
            "I tried to open the SocketJack Copilot Servers window. You can also open it from `Extensions > SocketJack > Copilot Servers`.";
    }

    public static string BuildNoVisibleAssistantTextFallback()
    {
        return "SocketJack bridge did not receive visible assistant text before this stream closed. The upstream model may still be running; check the selected SocketJack server or retry after it finishes.";
    }

    private static string FormatServerDisplayName(string serverName)
    {
        string displayName = (serverName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return "The selected SocketJack server";
        }

        return displayName.Length > 1 && displayName.All(ch => !char.IsLetter(ch) || char.IsLower(ch))
            ? char.ToUpperInvariant(displayName[0]) + displayName.Substring(1)
            : displayName;
    }

    public static bool LooksLikeOfflineServer(int statusCode, string detail)
    {
        string text = detail ?? "";
        if (statusCode == StatusCodes.Status503ServiceUnavailable ||
            statusCode == StatusCodes.Status502BadGateway ||
            statusCode == StatusCodes.Status504GatewayTimeout)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (text.Contains("offline", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not connected", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("not responding", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("reverse agent", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("target machine actively refused", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("HTTP 504", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Service Unavailable", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Gateway Timeout", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return text.Contains("heirowLLM has not connected", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("server is offline", StringComparison.OrdinalIgnoreCase);
    }

    private static string MessageContentText(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? stringValue))
                return stringValue ?? "";
            return node.ToString();
        }

        if (node is JsonArray array)
        {
            var parts = new List<string>();
            foreach (JsonNode? item in array)
            {
                string text = MessageContentText(item);
                if (!string.IsNullOrWhiteSpace(text))
                    parts.Add(text);
            }

            return string.Join("\n\n", parts);
        }

        if (node is JsonObject obj)
        {
            string text = FirstChatTextValue(obj, "text", "content", "input_text", "body", "value");
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }

        return "";
    }

    private static void AppendChatAssistantText(JsonNode? node, StringBuilder builder)
    {
        AppendChatAssistantText(node, builder, preserveTokenWhitespace: false);
    }

    private static void AppendChatAssistantText(JsonNode? node, StringBuilder builder, bool preserveTokenWhitespace)
    {
        if (node == null)
            return;

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? text) && ShouldAppendAssistantText(text ?? "", preserveTokenWhitespace))
                builder.Append(text);
            return;
        }

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                AppendChatAssistantText(item, builder, preserveTokenWhitespace);
            return;
        }

        if (node is not JsonObject obj)
            return;

        string type = FirstString(obj, "type", "event");
        string status = FirstString(obj, "status");
        if (type.Equals("error", StringComparison.OrdinalIgnoreCase) ||
            status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
            JsonBoolIsFalse(obj["ok"]) ||
            JsonBoolIsFalse(obj["success"]))
        {
            string errorText = FirstNonEmpty(FirstChatTextValue(obj, "error", "message", "detail", "content", "body"), "AI request failed.");
            if (IsNoAssistantTextError(errorText))
                return;

            throw new InvalidOperationException(errorText);
        }

        if (obj["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choice in choices)
                AppendChatAssistantText(choice, builder, preserveTokenWhitespace);
            return;
        }

        if (type.Equals("progress", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("usage", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("fileChanges", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("file-change", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("toolCall", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("tool-call", StringComparison.OrdinalIgnoreCase) ||
            type.Equals("serviceAccess", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int beforeNested = builder.Length;
        foreach (string name in new[] { "message", "delta", "result", "response", "data" })
        {
            if (obj[name] is JsonObject child)
                AppendChatAssistantText(child, builder, preserveTokenWhitespace);
        }

        foreach (string name in new[] { "events", "items", "output" })
        {
            if (obj[name] is JsonArray children)
                AppendChatAssistantText(children, builder, preserveTokenWhitespace);
        }

        string content = FirstChatTextValue(
            obj,
            preserveTokenWhitespace,
            "final",
            "finalAnswer",
            "final_answer",
            "answer",
            "output_text",
            "outputText",
            "completion",
            "content",
            "body",
            "text",
            "response",
            "message");
        if (preserveTokenWhitespace ? content.Length == 0 : string.IsNullOrWhiteSpace(content))
            return;

        string nestedText = builder.Length > beforeNested ? builder.ToString(beforeNested, builder.Length - beforeNested) : "";
        if (!string.IsNullOrEmpty(nestedText) &&
            (string.Equals(nestedText, content, StringComparison.Ordinal) ||
            (content.Length >= 16 && nestedText.Contains(content, StringComparison.Ordinal)) ||
            (nestedText.Length >= 16 && content.Contains(nestedText, StringComparison.Ordinal))))
        {
            return;
        }

        if (LooksLikeJsonContainer(content.Trim()))
        {
            try
            {
                int before = builder.Length;
                AppendChatAssistantText(JsonNode.Parse(content), builder, preserveTokenWhitespace);
                if (builder.Length > before)
                    return;
            }
            catch (JsonException)
            {
            }
        }

        if (ShouldAppendAssistantText(content, preserveTokenWhitespace))
            builder.Append(content);
    }

    private static string FirstChatTextValue(JsonObject obj, params string[] names)
    {
        return FirstChatTextValue(obj, false, names);
    }

    private static bool ShouldAppendAssistantText(string text, bool preserveTokenWhitespace)
    {
        if (preserveTokenWhitespace)
        {
            if (text.Length == 0)
                return false;
            if (string.IsNullOrWhiteSpace(text))
                return true;
        }
        else if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return !IsInternalProgressLikeText(text) &&
            !LooksLikeRawStructuredPayload(text) &&
            !LooksLikeOrphanStructuredTailFragment(text);
    }

    private static string FirstChatTextValue(JsonObject obj, bool preserveTokenWhitespace, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            if (node is JsonValue value)
            {
                if (value.TryGetValue(out string? stringValue) &&
                    (preserveTokenWhitespace ? !string.IsNullOrEmpty(stringValue) : !string.IsNullOrWhiteSpace(stringValue)))
                {
                    return stringValue;
                }
                if (value.TryGetValue(out long longValue))
                    return longValue.ToString(CultureInfo.InvariantCulture);
                if (value.TryGetValue(out double doubleValue))
                    return doubleValue.ToString(CultureInfo.InvariantCulture);
            }
            else if (node is JsonArray array)
            {
                string text = MessageContentText(array);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return "";
    }

    private static bool JsonBoolIsFalse(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool boolValue) && !boolValue;

    private static bool LooksLikeJsonContainer(string value)
    {
        string trimmed = (value ?? "").Trim();
        return (trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
            (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal));
    }

    private static string NormalizeVisibleAssistantText(string value)
    {
        string text = StripHiddenAssistantMarkup(value ?? "");
        var finals = Regex.Matches(text, @"<\s*auto_final\b[^>]*>([\s\S]*?)<\s*/\s*auto_final\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            .Cast<Match>()
            .Select(match => match.Groups[1].Value.Trim())
            .Where(item => item.Length > 0)
            .ToArray();
        if (finals.Length > 0)
            text = string.Join("\n\n", finals);

        text = Regex.Replace(text, @"<\s*/?\s*auto_final\b[^>]*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = StripHiddenAssistantMarkup(text);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var output = new List<string>();
        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                if (output.Count > 0 && output[^1].Length != 0)
                    output.Add("");
                continue;
            }

            if (trimmed.StartsWith("[SocketJack]", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("[Socketjack]", StringComparison.OrdinalIgnoreCase) ||
                IsInternalProgressLikeText(trimmed) ||
                LooksLikeRawStructuredPayload(trimmed) ||
                LooksLikeOrphanStructuredTailFragment(trimmed))
            {
                continue;
            }

            output.Add(line);
        }

        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);
        return string.Join("\n", output).Trim();
    }

    public static string NormalizeVisibleAssistantDeltaText(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return StripHiddenAssistantMarkup(value);
    }

    private static string StripHiddenAssistantMarkup(string value)
    {
        string text = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        string previous = "";
        for (int i = 0; i < 8 && !string.Equals(previous, text, StringComparison.Ordinal); i++)
        {
            previous = text;
            text = Regex.Replace(text, @"<\s*(think|thinking|thought|analysis)\b[^>]*>[\s\S]*?<\s*/\s*\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        text = Regex.Replace(text, @"<\s*(?:think|thinking|thought|analysis)\b[^>]*>[\s\S]*$", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"<\s*/?\s*(?:think|thinking|thought|analysis)\b[^>]*>?", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"<\s*/?\s*markdown\s*>", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }

    private static bool IsSseControlLine(string value)
    {
        string trimmed = (value ?? "").Trim();
        return trimmed.Length == 0 ||
            trimmed.StartsWith(":", StringComparison.Ordinal) ||
            trimmed.StartsWith("event:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("id:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("retry:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInternalProgressLikeText(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return true;
        if (IsSseControlLine(text))
            return true;
        if (LooksLikeOrphanStructuredTailFragment(text))
            return true;
        if (Regex.IsMatch(text, @"\bstill connected after\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(text, @"^(checking llmruntime model|llmruntime model .* is loaded|opening chat stream|processing prompt|selected server|routing |connected to auto server|waiting for |server started|server is working|server completed|tool event:|tool summary:|tool complete:|tool failed:)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        if (Regex.IsMatch(text, @"^\s*(?:data:\s*)?[\[{].*(tool|function_call|tool_call|browser_|internet_search|search_query|vs_|shell|powershell)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return true;
        return false;
    }

    private static JsonObject BuildResponseObject(string id, long created, string model, string status, string assistantText, JsonArray? outputOverride = null)
    {
        string text = assistantText ?? "";
        var message = new JsonObject
        {
            ["id"] = ResponseMessageId(id),
            ["type"] = "message",
            ["status"] = string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ? "completed" : "in_progress",
            ["role"] = "assistant",
            ["content"] = string.IsNullOrEmpty(text)
                ? new JsonArray()
                : new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "output_text",
                        ["text"] = text
                    }
                }
        };
        JsonArray output = outputOverride ?? (string.IsNullOrEmpty(text) && !string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
            ? new JsonArray()
            : new JsonArray { message });

        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = created,
            ["model"] = model,
            ["status"] = status,
            ["output"] = output,
            ["output_text"] = text,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = 0,
                ["output_tokens"] = 0,
                ["total_tokens"] = 0
            }
        };
    }

    private static string ResponseMessageId(string responseId)
    {
        return "msg_" + Regex.Replace(responseId ?? "", @"[^A-Za-z0-9_]", "_", RegexOptions.CultureInvariant);
    }

    private static JsonObject BuildChunk(string id, long created, string model, JsonObject delta, string? finishReason)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "chat.completion.chunk",
            ["created"] = created,
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["delta"] = delta,
                    ["finish_reason"] = finishReason == null ? null : JsonValue.Create(finishReason)
                }
            }
        };
    }

    private static void AppendSse(StringBuilder builder, JsonObject payload)
    {
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

    private static void AppendEventSse(StringBuilder builder, string eventName, JsonObject payload)
    {
        builder.Append("event: ").Append(eventName).Append('\n');
        builder.Append("data: ").Append(payload.ToJsonString()).Append("\n\n");
    }

    private static ProxyResponse JsonResponse(int statusCode, JsonObject root)
    {
        return new ProxyResponse(
            statusCode,
            statusCode >= 200 && statusCode < 300 ? "OK" : "Error",
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    private static void CopyMaxTokens(JsonObject source, JsonObject target)
    {
        JsonNode? requested = FirstMaxTokensNode(source);
        if (requested == null)
        {
            if (IsVisualStudioAutopilotRequest(source))
                target["max_tokens"] = SocketJackModelProxyForwarder.VisualStudioAutopilotMinimumMaxTokens;
            return;
        }

        int? requestedInt = ReadPositiveInt(requested);
        if (IsVisualStudioAutopilotRequest(source) &&
            (!requestedInt.HasValue || requestedInt.Value < SocketJackModelProxyForwarder.VisualStudioAutopilotMinimumMaxTokens))
        {
            target["max_tokens"] = SocketJackModelProxyForwarder.VisualStudioAutopilotMinimumMaxTokens;
            return;
        }

        target["max_tokens"] = CloneNode(requested);
    }

    private static bool IsVisualStudioAutopilotRequest(JsonObject source)
    {
        return string.Equals(DetectVisualStudioAiMode(source), "AutoPilot", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonNode? FirstMaxTokensNode(JsonObject source)
    {
        return source["max_tokens"] ??
            source["max_completion_tokens"] ??
            source["max_output_tokens"];
    }

    private static int? ReadPositiveInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;

        if (value.TryGetValue(out int intValue) && intValue > 0)
            return intValue;
        if (value.TryGetValue(out long longValue) && longValue > 0 && longValue <= int.MaxValue)
            return (int)longValue;
        if (value.TryGetValue(out double doubleValue) && doubleValue > 0 && doubleValue <= int.MaxValue)
            return (int)Math.Round(doubleValue);
        if (value.TryGetValue(out string? stringValue) &&
            int.TryParse(stringValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
            parsed > 0)
        {
            return parsed;
        }

        return null;
    }

    private static void CopyIfPresent(JsonObject source, JsonObject target, string name)
    {
        if (source[name] != null)
            target[name] = CloneNode(source[name]);
    }

    private static JsonArray CloneArray(JsonArray? source)
    {
        var clone = new JsonArray();
        if (source == null)
            return clone;

        foreach (JsonNode? node in source)
            clone.Add(CloneNode(node));
        return clone;
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString()) as JsonObject ?? new JsonObject();
    }

    private static JsonNode? CloneNode(JsonNode? node)
    {
        return node == null ? null : JsonNode.Parse(node.ToJsonString());
    }

    public static JsonArray ExtractVisualStudioReferenceFiles(JsonObject openAiRequest)
    {
        var files = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddReferenceFilesFromNode(openAiRequest["files"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["attachments"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["references"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["documents"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["input_files"], files, seen);
        AddReferenceFilesFromNode(openAiRequest["input"], files, seen);

        if (openAiRequest["context"] is JsonObject context)
        {
            AddReferenceFilesFromNode(context["files"], files, seen);
            AddReferenceFilesFromNode(context["attachments"], files, seen);
            AddReferenceFilesFromNode(context["references"], files, seen);
            AddReferenceFilesFromNode(context["documents"], files, seen);
            AddReferenceFilesFromNode(context["snippets"], files, seen);
        }

        if (openAiRequest["messages"] is JsonArray messages)
        {
            foreach (JsonNode? messageNode in messages)
            {
                if (messageNode is not JsonObject message)
                    continue;

                AddReferenceFilesFromNode(message["files"], files, seen);
                AddReferenceFilesFromNode(message["attachments"], files, seen);
                AddReferenceFilesFromNode(message["references"], files, seen);
                AddReferenceFilesFromNode(message["snippets"], files, seen);

                JsonNode? content = message["content"];
                if (content is JsonArray parts)
                {
                    foreach (JsonNode? part in parts)
                    {
                        if (part is JsonObject partObject)
                            TryAddVisualStudioReferenceFile(partObject, files, seen);
                    }
                }
                else
                {
                    TryAddVisualStudioReferenceText(ReferenceNodeString(content), files, seen);
                }
            }
        }

        return files;
    }

    public static string FirstReferenceString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            string value = ReferenceNodeString(node);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static void AddReferenceFilesFromNode(JsonNode? node, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32 || node == null)
            return;

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                    TryAddVisualStudioReferenceFile(obj, files, seen);
                else
                    TryAddVisualStudioReferenceText(ReferenceNodeString(item), files, seen);

                if (files.Count >= 32)
                    return;
            }

            return;
        }

        if (node is JsonObject single)
            TryAddVisualStudioReferenceFile(single, files, seen);
        else
            TryAddVisualStudioReferenceText(ReferenceNodeString(node), files, seen);
    }

    private static void TryAddVisualStudioReferenceFile(JsonObject source, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32)
            return;

        string dataUrl = FirstReferenceString(source, "dataUrl", "dataURL");
        string text = FirstReferenceString(source, "text", "content", "contents", "source", "body", "value", "snippet", "code");
        string path = FirstReferenceString(source, "path", "filePath", "fullPath", "uri", "url", "relativePath");
        string name = FirstReferenceString(source, "name", "fileName", "filename", "title", "label");
        string type = FirstNonEmpty(FirstReferenceString(source, "type", "mimeType", "contentType"), GuessMimeType(name, path));

        if (string.IsNullOrWhiteSpace(name))
            name = FileNameFromReferencePath(path);

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(dataUrl) &&
            TryReadLocalVisualStudioReference(path, out string localText, out string localType, out string localName))
        {
            text = localText;
            if (string.IsNullOrWhiteSpace(name))
                name = localName;
            if (string.IsNullOrWhiteSpace(type))
                type = localType;
        }

        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(dataUrl))
            return;

        if (string.IsNullOrWhiteSpace(name))
            name = "visual-studio-reference-" + (files.Count + 1).ToString(CultureInfo.InvariantCulture) + ".txt";

        name = SanitizeReferenceFileName(name);
        string relativePath = BuildVisualStudioReferenceRelativePath(source, name, path);
        string dedupe = ComputeReferenceHash(name + "|" + relativePath + "|" + text + "|" + dataUrl);
        if (!seen.Add(dedupe))
            return;

        var file = new JsonObject
        {
            ["id"] = "vsref_" + dedupe,
            ["name"] = name,
            ["type"] = string.IsNullOrWhiteSpace(type) ? "text/plain" : type,
            ["relativePath"] = relativePath,
            ["source"] = "visual-studio",
            ["asFile"] = true,
            ["uploadedUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        CopyReferenceMetadata(source, file, "startLine", "endLine", "range", "language", "projectName", "documentId");

        if (!string.IsNullOrWhiteSpace(path))
            file["originalPath"] = path;
        if (!string.IsNullOrWhiteSpace(dataUrl))
        {
            file["dataUrl"] = dataUrl;
            file["size"] = EstimateDataUrlBytes(dataUrl);
        }
        else
        {
            string limited = LimitReferenceText(text);
            file["text"] = limited;
            file["content"] = limited;
            file["size"] = Encoding.UTF8.GetByteCount(limited);
        }

        files.Add(file);
    }

    private static void TryAddVisualStudioReferenceText(string text, JsonArray files, HashSet<string> seen)
    {
        if (files.Count >= 32 || !LooksLikeInlineVisualStudioReference(text))
            return;

        string name = ExtractReferenceFileNameFromText(text);
        if (string.IsNullOrWhiteSpace(name))
            name = "visual-studio-snippet-" + (files.Count + 1).ToString(CultureInfo.InvariantCulture) + ".md";

        var source = new JsonObject
        {
            ["name"] = name,
            ["type"] = GuessMimeType(name, ""),
            ["text"] = text,
            ["relativePath"] = "VisualStudio/" + name
        };
        TryAddVisualStudioReferenceFile(source, files, seen);
    }

    private static bool LooksLikeInlineVisualStudioReference(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 512)
            return false;

        return text.Contains("```", StringComparison.Ordinal) ||
            text.Contains("Active Document", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("references", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\b(class|interface|struct|enum|namespace|using|public|private|protected|internal)\b", RegexOptions.CultureInvariant);
    }

    private static bool TryReadLocalVisualStudioReference(string path, out string text, out string type, out string name)
    {
        text = "";
        type = "";
        name = "";
        string fullPath = NormalizeReferencePath(path);
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            return false;

        var info = new FileInfo(fullPath);
        if (info.Length > 10L * 1024L * 1024L)
            return false;

        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            if (LooksBinary(bytes))
                return false;

            text = Encoding.UTF8.GetString(bytes);
            name = info.Name;
            type = GuessMimeType(name, fullPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeReferencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        string value = path.Trim().Trim('"', '\'');
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            value = uri.LocalPath;

        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksBinary(byte[] bytes)
    {
        int sample = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < sample; i++)
        {
            if (bytes[i] == 0)
                return true;
        }

        return false;
    }

    private static string BuildVisualStudioReferenceRelativePath(JsonObject source, string name, string path)
    {
        string explicitRelative = FirstReferenceString(source, "relativePath", "workspaceRelativePath", "projectRelativePath");
        if (!string.IsNullOrWhiteSpace(explicitRelative) && !Path.IsPathRooted(explicitRelative))
            return NormalizeReferenceRelativePath("VisualStudio/" + explicitRelative);

        string pathName = FileNameFromReferencePath(path);
        return NormalizeReferenceRelativePath("VisualStudio/" + (string.IsNullOrWhiteSpace(pathName) ? name : pathName));
    }

    private static string NormalizeReferenceRelativePath(string value)
    {
        var parts = new List<string>();
        foreach (string raw in (value ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = SanitizeReferenceFileName(raw.Trim());
            if (part.Length == 0 || part == "." || part == "..")
                continue;
            parts.Add(part);
        }

        return parts.Count == 0 ? "VisualStudio/reference.txt" : string.Join("/", parts);
    }

    private static string ExtractReferenceFileNameFromText(string text)
    {
        Match match = Regex.Match(text ?? "", @"[A-Za-z]:\\[^\r\n""'<>|]+\.[A-Za-z0-9]{1,12}", RegexOptions.CultureInvariant);
        if (!match.Success)
            match = Regex.Match(text ?? "", @"(?:^|[\s`""'])([A-Za-z0-9_. -]+\.[A-Za-z0-9]{1,12})(?:\s|$)", RegexOptions.CultureInvariant);

        if (!match.Success)
            return "";

        string value = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1].Value : match.Value;
        return FileNameFromReferencePath(value.Trim());
    }

    private static string FileNameFromReferencePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            string value = path.Trim().Trim('"', '\'');
            if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                value = uri.LocalPath;
            return Path.GetFileName(value.Replace('/', Path.DirectorySeparatorChar));
        }
        catch
        {
            return "";
        }
    }

    private static string SanitizeReferenceFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return "";

        foreach (char invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        return fileName.Trim();
    }

    private static string LimitReferenceText(string text)
    {
        text ??= "";
        const int maxChars = 10 * 1024 * 1024;
        return Encoding.UTF8.GetByteCount(text) <= maxChars ? text : text.Substring(0, Math.Min(text.Length, maxChars));
    }

    private static int EstimateDataUrlBytes(string dataUrl)
    {
        string value = dataUrl ?? "";
        int comma = value.IndexOf(',');
        string encoded = comma >= 0 ? value.Substring(comma + 1) : value;
        return (int)Math.Min(int.MaxValue, Math.Max(0, encoded.Length * 3L / 4L));
    }

    private static string ComputeReferenceHash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
        return BitConverter.ToString(hash, 0, 8).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string GuessMimeType(string name, string path)
    {
        string extension = Path.GetExtension(FirstNonEmpty(name, path)).ToLowerInvariant();
        return extension switch
        {
            ".cs" => "text/x-csharp",
            ".vb" => "text/x-vb",
            ".fs" => "text/x-fsharp",
            ".ts" => "text/typescript",
            ".tsx" => "text/tsx",
            ".js" => "text/javascript",
            ".jsx" => "text/jsx",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".xaml" => "application/xaml+xml",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".py" => "text/x-python",
            ".cpp" or ".cc" or ".cxx" or ".c" or ".h" or ".hpp" => "text/x-c",
            _ => "text/plain"
        };
    }

    private static string ReferenceNodeString(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out string? stringValue))
                return stringValue ?? "";
            if (value.TryGetValue(out bool boolValue))
                return boolValue.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue(out long longValue))
                return longValue.ToString(CultureInfo.InvariantCulture);
            if (value.TryGetValue(out double doubleValue))
                return doubleValue.ToString(CultureInfo.InvariantCulture);
        }

        return node.ToString();
    }

    private static void CopyReferenceMetadata(JsonObject source, JsonObject target, params string[] names)
    {
        foreach (string name in names)
        {
            if (source[name] != null)
                target[name] = CloneNode(source[name]);
        }
    }

    private static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;
            string value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static string FirstStringFromChild(JsonObject obj, string childName, params string[] names)
    {
        return obj[childName] is JsonObject child ? FirstString(child, names) : "";
    }

    private static string FirstTextSegment(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
                continue;

            string value;
            if (node is JsonValue jsonValue && jsonValue.TryGetValue(out string? stringValue))
                value = stringValue ?? "";
            else
                value = node.ToString();

            if (value.Length > 0)
                return value;
        }

        return "";
    }

    private static string FirstStatusText(JsonObject obj, params string[] names)
    {
        string value = FirstTextSegment(obj, names);
        if (string.IsNullOrWhiteSpace(value))
            return "";

        if (LooksLikeRawStructuredPayload(value))
            return "";

        value = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var builder = new StringBuilder(value.Length);
        bool lastWasWhite = false;
        foreach (char ch in value)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasWhite)
                    builder.Append(' ');
                lastWasWhite = true;
                continue;
            }

            builder.Append(ch);
            lastWasWhite = false;
        }

        string result = builder.ToString().Trim();
        if (result.Length > 220)
            result = result.Substring(0, 217) + "...";
        return result;
    }

    private static bool LooksLikeRawStructuredPayload(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        if ((trimmed.StartsWith("{", StringComparison.Ordinal) && trimmed.EndsWith("}", StringComparison.Ordinal)) ||
            (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal)))
        {
            return true;
        }

        return trimmed.IndexOf("\"type\"", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("\"tool", StringComparison.OrdinalIgnoreCase) >= 0 ||
            trimmed.IndexOf("```json", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool LooksLikeOrphanStructuredTailFragment(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0 || text.Length > 64)
            return false;

        bool hasCloser = false;
        foreach (char ch in text)
        {
            if (ch == '}' || ch == ']' || ch == ')' || ch == '"' || ch == '\'' || ch == ',' || ch == ':' || char.IsWhiteSpace(ch))
            {
                hasCloser = hasCloser || ch == '}' || ch == ']' || ch == ')';
                continue;
            }

            return false;
        }

        return hasCloser;
    }

    public static bool LooksLikeToolCallTextStart(string value)
    {
        string text = (value ?? "").TrimStart();
        if (text.Length == 0)
            return false;

        return text.StartsWith("{", StringComparison.Ordinal) ||
            text.StartsWith("[", StringComparison.Ordinal) ||
            text.StartsWith("<tool_call", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("Assistant requested tool call", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("tool_call ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("```", StringComparison.Ordinal) ||
            text.Contains("Assistant requested tool call", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\btool_call\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool MightStillBeToolCallText(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return true;

        if (TryParseAssistantToolCalls(text, null, out _))
            return false;

        if (LooksLikeBalancedJsonContainer(text))
        {
            try
            {
                JsonNode.Parse(StripMarkdownJsonFence(text));
                return false;
            }
            catch (JsonException)
            {
            }
        }

        if (text.Contains("<tool_call", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("</tool_call>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (text.Contains("Assistant requested tool call", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text, @"\btool_call\s+\S", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return !LooksLikeBalancedJsonContainer(text);
    }

    public static bool TryParseAssistantToolCalls(string content, IReadOnlyCollection<string>? availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        var result = new List<SocketJackOpenAiToolCall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string text = content ?? "";
        foreach (string candidate in EnumerateToolCallJsonCandidates(text))
        {
            try
            {
                JsonNode? node = JsonNode.Parse(candidate);
                TryParseToolCallNode(node, availableTools, result, seen);
            }
            catch (JsonException)
            {
            }
        }

        TryParsePlainToolCallTranscript(text, availableTools, result, seen);

        toolCalls = result;
        return result.Count > 0;
    }

    private static void TryParsePlainToolCallTranscript(string content, IReadOnlyCollection<string>? availableTools, List<SocketJackOpenAiToolCall> result, HashSet<string> seen)
    {
        string text = content ?? "";
        if (!text.Contains("tool_call", StringComparison.OrdinalIgnoreCase))
            return;

        int searchIndex = 0;
        while (searchIndex < text.Length)
        {
            Match match = Regex.Match(
                text[searchIndex..],
                @"\btool_call\b(?<header>[^\r\n]*?)\barguments\s*=",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                break;

            int headerStart = searchIndex + match.Index;
            int argsStart = searchIndex + match.Index + match.Length;
            string header = match.Groups["header"].Value;
            string name = ReadPlainToolCallAttribute(header, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                searchIndex = headerStart + Math.Max(1, match.Length);
                continue;
            }

            name = name.Trim();
            if (availableTools != null && availableTools.Count > 0 && !availableTools.Contains(name))
            {
                searchIndex = argsStart;
                continue;
            }

            if (!TryReadBalancedJsonSegmentAt(text, argsStart, out string argumentsJson, out int endIndex))
            {
                searchIndex = argsStart;
                continue;
            }

            try
            {
                argumentsJson = JsonNode.Parse(argumentsJson)?.ToJsonString() ?? "{}";
            }
            catch (JsonException)
            {
                searchIndex = endIndex;
                continue;
            }

            string key = name + "\n" + argumentsJson;
            if (seen.Add(key))
            {
                string id = ReadPlainToolCallAttribute(header, "id");
                result.Add(new SocketJackOpenAiToolCall
                {
                    Id = string.IsNullOrWhiteSpace(id) ? CreateToolCallId() : id.Trim(),
                    Name = name,
                    ArgumentsJson = argumentsJson
                });
            }

            searchIndex = endIndex;
        }
    }

    private static string ReadPlainToolCallAttribute(string header, string name)
    {
        Match match = Regex.Match(
            header ?? "",
            @"(?:^|\s)" + Regex.Escape(name) + @"\s*=\s*(?:""(?<quoted>[^""]+)""|'(?<single>[^']+)'|(?<bare>[^\s]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return "";

        return FirstNonEmpty(
            match.Groups["quoted"].Value,
            match.Groups["single"].Value,
            match.Groups["bare"].Value);
    }

    private static bool TryReadBalancedJsonSegmentAt(string text, int startIndex, out string json, out int endIndex)
    {
        json = "";
        endIndex = startIndex;
        if (string.IsNullOrEmpty(text) || startIndex < 0 || startIndex >= text.Length)
            return false;

        int i = startIndex;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        if (i >= text.Length || (text[i] != '{' && text[i] != '['))
            return false;

        int start = i;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{' || ch == '[')
            {
                depth++;
                continue;
            }

            if (ch == '}' || ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    endIndex = i + 1;
                    json = text.Substring(start, endIndex - start).Trim();
                    return true;
                }
            }
        }

        return false;
    }

    public static bool TryParseOpenAiStreamingToolCalls(string text, IReadOnlyCollection<string>? availableTools, out IReadOnlyList<SocketJackOpenAiToolCall> toolCalls)
    {
        toolCalls = Array.Empty<SocketJackOpenAiToolCall>();
        string payload = StripSseDataPrefix(text);
        if (string.IsNullOrWhiteSpace(payload) || payload.Equals("[DONE]", StringComparison.OrdinalIgnoreCase))
            return false;

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            return false;
        }

        var result = new List<SocketJackOpenAiToolCall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ExtractOpenAiStreamingToolCalls(node, availableTools, result, seen);
        toolCalls = result;
        return result.Count > 0;
    }

    private static void ExtractOpenAiStreamingToolCalls(JsonNode? node, IReadOnlyCollection<string>? availableTools, List<SocketJackOpenAiToolCall> result, HashSet<string> seen)
    {
        if (node == null)
            return;

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                ExtractOpenAiStreamingToolCalls(item, availableTools, result, seen);
            return;
        }

        if (node is not JsonObject obj)
            return;

        if (obj["choices"] is JsonArray choices)
        {
            foreach (JsonNode? choice in choices)
                ExtractOpenAiStreamingToolCalls(choice, availableTools, result, seen);
            return;
        }

        foreach (string childName in new[] { "delta", "message" })
        {
            if (obj[childName] is JsonObject child)
                ExtractOpenAiStreamingToolCalls(child, availableTools, result, seen);
        }

        if (obj["tool_calls"] is JsonArray toolCalls)
        {
            foreach (JsonNode? toolCall in toolCalls)
                TryParseToolCallNode(toolCall, availableTools, result, seen);
        }

        if (obj["function_call"] is JsonObject functionCall)
            TryParseToolCallObject(functionCall, availableTools, result, seen, obj["id"]?.ToString() ?? "");
    }

    private static string StripSseDataPrefix(string text)
    {
        string value = (text ?? "").Trim();
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return value;

        value = value.Substring(5);
        if (value.StartsWith(" ", StringComparison.Ordinal))
            value = value.Substring(1);
        return value.Trim();
    }

    private static IEnumerable<string> EnumerateToolCallJsonCandidates(string content)
    {
        string text = StripMarkdownJsonFence(content ?? "");
        foreach (Match match in Regex.Matches(text, @"<\s*tool_call\b[^>]*>([\s\S]*?)<\s*/\s*tool_call\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string inner = match.Groups[1].Value.Trim();
            if (inner.Length > 0)
                yield return inner;
        }

        foreach (string segment in EnumerateBalancedJsonSegments(text))
            yield return segment;
    }

    private static IEnumerable<string> EnumerateBalancedJsonSegments(string text)
    {
        int start = -1;
        int depth = 0;
        bool inString = false;
        bool escape = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (start < 0)
            {
                if (ch == '{' || ch == '[')
                {
                    start = i;
                    depth = 1;
                }
                continue;
            }

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escape = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{' || ch == '[')
            {
                depth++;
                continue;
            }

            if (ch == '}' || ch == ']')
            {
                depth--;
                if (depth == 0)
                {
                    yield return text.Substring(start, i - start + 1).Trim();
                    start = -1;
                }
            }
        }
    }

    private static void TryParseToolCallNode(JsonNode? node, IReadOnlyCollection<string>? availableTools, List<SocketJackOpenAiToolCall> result, HashSet<string> seen)
    {
        if (node == null)
            return;

        if (node is JsonArray array)
        {
            foreach (JsonNode? item in array)
                TryParseToolCallNode(item, availableTools, result, seen);
            return;
        }

        if (node is not JsonObject obj)
            return;

        if (obj["tool_calls"] is JsonArray toolCalls)
        {
            foreach (JsonNode? item in toolCalls)
                TryParseToolCallNode(item, availableTools, result, seen);
            return;
        }

        if (obj["function_call"] is JsonObject functionCall)
            TryParseToolCallObject(functionCall, availableTools, result, seen, obj["id"]?.ToString() ?? "");

        TryParseToolCallObject(obj, availableTools, result, seen, obj["id"]?.ToString() ?? "");
    }

    private static void TryParseToolCallObject(JsonObject obj, IReadOnlyCollection<string>? availableTools, List<SocketJackOpenAiToolCall> result, HashSet<string> seen, string id)
    {
        JsonObject? function = obj["function"] as JsonObject;
        string name = FirstNonEmpty(
            function == null ? "" : FirstString(function, "name"),
            FirstString(obj, "name"),
            FirstString(obj, "tool"),
            FirstString(obj, "toolName"),
            FirstString(obj, "tool_name"));
        if (string.IsNullOrWhiteSpace(name))
            return;

        name = name.Trim();
        if (availableTools != null && availableTools.Count > 0 && !availableTools.Contains(name))
            return;

        string argumentsJson = BuildToolArgumentsJson(obj, function);
        string key = name + "\n" + argumentsJson;
        if (!seen.Add(key))
            return;

        result.Add(new SocketJackOpenAiToolCall
        {
            Id = string.IsNullOrWhiteSpace(id) ? CreateToolCallId() : id,
            Name = name,
            ArgumentsJson = argumentsJson
        });
    }

    private static string BuildToolArgumentsJson(JsonObject obj, JsonObject? function)
    {
        JsonNode? arguments = function?["arguments"] ??
            obj["arguments"] ??
            obj["args"] ??
            obj["input"] ??
            obj["parameters"];
        string fromArguments = NormalizeToolArgumentsJson(arguments);
        if (!string.IsNullOrWhiteSpace(fromArguments))
            return fromArguments;

        var args = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (property.Value == null || IsToolCallMetadataProperty(property.Key))
                continue;

            args[property.Key] = CloneNode(property.Value);
        }

        return args.Count == 0 ? "{}" : args.ToJsonString();
    }

    private static string NormalizeToolArgumentsJson(JsonNode? node)
    {
        if (node == null)
            return "";

        if (node is JsonValue value && value.TryGetValue(out string? stringValue))
        {
            string text = (stringValue ?? "").Trim();
            if (text.Length == 0)
                return "{}";
            if (LooksLikeJsonContainer(text))
            {
                try
                {
                    return JsonNode.Parse(text)?.ToJsonString() ?? "{}";
                }
                catch (JsonException)
                {
                }
            }

            return new JsonObject { ["value"] = text }.ToJsonString();
        }

        return node.ToJsonString();
    }

    private static bool IsToolCallMetadataProperty(string name)
    {
        return name.Equals("id", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("type", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("name", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tool", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("toolName", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("tool_name", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("function", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("arguments", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("args", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("input", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("parameters", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripMarkdownJsonFence(string value)
    {
        string text = (value ?? "").Trim();
        Match match = Regex.Match(text, @"^```\w*\s*([\s\S]*?)\s*```$", RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value.Trim() : text;
    }

    private static bool LooksLikeBalancedJsonContainer(string value)
    {
        string text = StripMarkdownJsonFence(value);
        if (text.Length == 0)
            return false;

        string last = text[^1].ToString();
        return (text.StartsWith("{", StringComparison.Ordinal) && last == "}") ||
            (text.StartsWith("[", StringComparison.Ordinal) && last == "]");
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}

public static class SocketJackSecretRedactor
{
    private static readonly string[] SecretNames = { "token", "api_key", "apikey", "authorization", "secret", "password" };

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        string redacted = value!;
        foreach (string secretName in SecretNames)
            redacted = RedactName(redacted, secretName);
        return redacted;
    }

    private static string RedactName(string value, string name)
    {
        string redacted = value;
        int searchStart = 0;
        while (searchStart < redacted.Length)
        {
            int index = redacted.IndexOf(name, searchStart, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;

            int separator = redacted.IndexOfAny(new[] { ':', '=' }, index + name.Length);
            if (separator < 0)
            {
                searchStart = index + name.Length;
                continue;
            }

            int valueStart = separator + 1;
            while (valueStart < redacted.Length && char.IsWhiteSpace(redacted[valueStart]))
                valueStart++;

            int valueEnd = valueStart;
            while (valueEnd < redacted.Length && redacted[valueEnd] != ',' && redacted[valueEnd] != '&' && redacted[valueEnd] != '"' && redacted[valueEnd] != '\'' && redacted[valueEnd] != '\r' && redacted[valueEnd] != '\n')
                valueEnd++;

            if (valueEnd > valueStart)
                redacted = redacted.Substring(0, valueStart) + "[redacted]" + redacted.Substring(valueEnd);
            searchStart = valueStart + "[redacted]".Length;
        }

        return redacted;
    }
}
