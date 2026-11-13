using System.Reflection;
using System.Text;
using SocketJack.Net;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
var origin = builder.Configuration["ApplicationOrigin"] ?? "http://localhost:5091";
var storage = builder.Configuration["ApplicationStorage"] ?? Path.Combine(Path.GetTempPath(), "socketjack-websocket-demo");
using var backend = new ApplicationWebSocket(new ApplicationWebSocketOptions {
    StorageDirectory = storage,
    AllowedOrigins = new[] { origin },
    Authenticate = (user, password, _) => ValueTask.FromResult<string?>(
        (user is "alice" or "bob") && password == "demo" ? user : null)!
});
backend.RegisterCollection<Note>("notes");
backend.Register<Note, Note>("demo.echo", (_, note, _) => ValueTask.FromResult(note));
app.UseWebSockets();

app.Map("/app/socket", async context => {
    string requestOrigin = context.Request.Headers.Origin.ToString();
    if (!context.WebSockets.IsWebSocketRequest || !backend.CanUpgrade(requestOrigin, context.Request.IsHttps) ||
        !context.WebSockets.WebSocketRequestedProtocols.Contains(ApplicationWebSocket.Subprotocol)) {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    using var socket = await context.WebSockets.AcceptWebSocketAsync(ApplicationWebSocket.Subprotocol);
    await backend.RunAsync(socket, requestOrigin, context.Request.IsHttps, context.RequestAborted);
});
app.MapGet("/application.mjs", () => Results.Text(ApplicationWebSocket.GetBrowserClientModule(), "text/javascript"));
app.MapGet("/application.d.ts", () => Results.Text(backend.GenerateClientDeclarations(), "text/plain"));
app.MapGet("/manifest.json", () => Results.Text(backend.GenerateManifest(), "application/json"));

string clientDemoHtml = LoadEmbeddedHtml("WebSocketClientDemo.html");

app.MapGet("/", context => {
    context.Response.ContentType = "text/html";
    return context.Response.WriteAsync(clientDemoHtml);
});
app.Run();

static string LoadEmbeddedHtml(string fileName)
{
    string resourceName = "WebSocketTestApplication.Html." + fileName;
    using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
    if (stream == null)
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>WebSocket Test</title></head><body><h1>WebSocket test page missing</h1></body></html>";

    using var reader = new StreamReader(stream, Encoding.UTF8, true);
    return reader.ReadToEnd();
}

public sealed record Note(string Text);
