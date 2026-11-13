using System;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    public sealed partial class ApplicationWebSocket {
        /// <summary>Registers a fixed HTTPS JSON operation. The host supplies the per-user policy and credentials.</summary>
        public void RegisterRestOperation<TInput, TOutput>(string name, Uri endpoint, HttpMethod method,
            Func<ApplicationCaller, TInput, CancellationToken, ValueTask<bool>> authorize,
            Func<ApplicationCaller, TInput, CancellationToken, ValueTask<string>> bearer = null) {
            ValidateConnector(endpoint, "https");
            if (method == null || authorize == null) throw new ArgumentNullException(method == null ? nameof(method) : nameof(authorize));
            Register<TInput, TOutput>(name, async (caller, input, cancellation) => {
                if (!await authorize(caller, input, cancellation).ConfigureAwait(false))
                    throw new ApplicationError("forbidden", "Access denied.");
                using var request = new HttpRequestMessage(method, endpoint);
                if (method != HttpMethod.Get && method != HttpMethod.Head)
                    request.Content = new StringContent(JsonSerializer.Serialize(input, Json), System.Text.Encoding.UTF8, "application/json");
                if (bearer != null) {
                    string token = await bearer(caller, input, cancellation).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(token))
                        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }
                using var response = await ConnectorHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw new ApplicationError("upstreamFailed", "The connected service could not complete the request.");
                if (response.Content.Headers.ContentLength > MaximumMessageBytes)
                    throw new ApplicationError("tooLarge", "The connected service response exceeds the limit.");
                using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                return JsonSerializer.Deserialize<TOutput>(await ReadBoundedAsync(stream, cancellation).ConfigureAwait(false), Json);
            });
        }

        /// <summary>Registers a named WSS JSON request/reply operation at a fixed upstream address.</summary>
        public void RegisterWebSocketOperation<TInput, TOutput>(string name, Uri endpoint,
            Func<ApplicationCaller, TInput, CancellationToken, ValueTask<bool>> authorize) {
            ValidateConnector(endpoint, "wss");
            if (authorize == null) throw new ArgumentNullException(nameof(authorize));
            Register<TInput, TOutput>(name, async (caller, input, cancellation) => {
                if (!await authorize(caller, input, cancellation).ConfigureAwait(false))
                    throw new ApplicationError("forbidden", "Access denied.");
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(15));
                await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
                var request = JsonSerializer.SerializeToUtf8Bytes(input, Json);
                if (request.Length > MaximumMessageBytes) throw new ApplicationError("tooLarge", "The request exceeds the limit.");
                await socket.SendAsync(new ArraySegment<byte>(request), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                using var response = new MemoryStream();
                var buffer = new byte[16 * 1024];
                WebSocketReceiveResult received;
                do {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token).ConfigureAwait(false);
                    if (received.MessageType != WebSocketMessageType.Text || response.Length + received.Count > MaximumMessageBytes)
                        throw new ApplicationError("upstreamFailed", "The connected service sent an invalid response.");
                    response.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                return JsonSerializer.Deserialize<TOutput>(response.ToArray(), Json);
            });
        }

        private static readonly System.Net.Http.HttpClient ConnectorHttp = new System.Net.Http.HttpClient(new HttpClientHandler {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false
        }) { Timeout = TimeSpan.FromSeconds(15) };

        private static void ValidateConnector(Uri endpoint, string scheme) {
            if (endpoint == null || !endpoint.IsAbsoluteUri || endpoint.Scheme != scheme ||
                endpoint.UserInfo.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("A fixed secure endpoint is required.", nameof(endpoint));
        }

        private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellation) {
            using var result = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int count;
            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0) {
                if (result.Length + count > MaximumMessageBytes)
                    throw new ApplicationError("tooLarge", "The connected service response exceeds the limit.");
                result.Write(buffer, 0, count);
            }
            return result.ToArray();
        }
    }
}
