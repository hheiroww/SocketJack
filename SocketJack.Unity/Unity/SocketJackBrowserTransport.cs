using System;
using SocketJack.Multiplayer.Transport;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
#endif

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Browser-native WebSocket connector. Add beside NetworkManager and configure an
    /// explicit ws/wss endpoint before joining. HTTPS pages require a wss endpoint. Browsers join
    /// through outbound connections; they cannot listen as a game server. Native players use the
    /// same endpoint through ClientWebSocket. Use a public TLS certificate for Internet hosts.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkManager)), DefaultExecutionOrder(-500)]
    public sealed class SocketJackBrowserTransport : MonoBehaviour
    {
        [SerializeField] private string endpoint = "ws://127.0.0.1:7777/ws/game";
        private NetworkManager manager;
        private Func<bool, IGameTransport> previousFactory;
        private string relayTicket;
        /// <summary>Optional short-lived account-issued relay ticket. Set while stopped after
        /// login; it is sent in a binary Join rather than a URL or serialized scene asset.</summary>
        public string RelayTicket
        {
            get => relayTicket;
            set { if (manager != null && (manager.Server != null || manager.Client != null)) throw new InvalidOperationException("Configure relay access while stopped."); relayTicket = value; }
        }
        public string Endpoint
        {
            get => endpoint;
            set { if (manager != null && (manager.Server != null || manager.Client != null)) throw new InvalidOperationException("Configure the WebSocket endpoint while stopped."); Validate(value); endpoint = value; }
        }
        private static Uri Validate(string text)
        {
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != "ws" && uri.Scheme != "wss") || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
                throw new ArgumentException("Supply an absolute ws:// or wss:// endpoint without credentials or fragment.");
            return uri;
        }
        private void Awake() => Configure(GetComponent<NetworkManager>());
        public void Configure(NetworkManager target)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.Server != null || target.Client != null) throw new InvalidOperationException("Configure the browser provider before networking starts.");
            if (manager == target && target.TransportFactory == CreateTransport) return;
            manager = target; previousFactory = target.TransportFactory; target.Transport = TransportType.WebSocket; target.TransportFactory = CreateTransport;
        }
        public IGameTransport CreateTransport(bool server)
        {
            if (server)
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                throw new PlatformNotSupportedException("A browser can join a WebSocket game or relay, but cannot host a listening server.");
#else
                return previousFactory == null ? new WebSocketGameTransport() : previousFactory(true);
#endif
            }
            var transport = new WebSocketGameTransport(clientUri: Validate(endpoint));
#if UNITY_WEBGL && !UNITY_EDITOR
            transport.ClientConnector = string.IsNullOrEmpty(relayTicket) ? BrowserSocket.Connect : WebSocketRelayService.CreateClientConnector(relayTicket, BrowserSocket.Connect);
#else
            if (!string.IsNullOrEmpty(relayTicket)) transport.ClientConnector = WebSocketRelayService.CreateClientConnector(relayTicket);
#endif
            return transport;
        }
        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            BrowserSocket.PumpAll();
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        private sealed class BrowserSocket : WebSocket
        {
            private const int MaximumMessageBytes = 2048, MaximumQueuedBytes = 1024 * 1024, MaximumQueuedMessages = 256;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateCallback(int id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void MessageCallback(int id, IntPtr data, int length);
            private static readonly StateCallback OpenedCallback = OnOpen, ClosedCallback = OnClose, ErrorCallback = OnError;
            private static readonly MessageCallback ReceivedCallback = OnMessage;
            private static readonly Dictionary<int, BrowserSocket> Sockets = new Dictionary<int, BrowserSocket>();
            private static readonly List<BrowserSocket> Pump = new List<BrowserSocket>();
            private static int nextId;
            [DllImport("__Internal")] private static extern void SJWS_Open(int id, string url, StateCallback opened, StateCallback closed, StateCallback failed, MessageCallback message, int maxBytes);
            [DllImport("__Internal")] private static extern int SJWS_Send(int id, byte[] data, int offset, int length, int maxBufferedBytes);
            [DllImport("__Internal")] private static extern void SJWS_Close(int id);
            private readonly int id;
            private readonly Queue<byte[]> messages = new Queue<byte[]>();
            // Default continuations run from the browser event/main thread; no managed worker thread is required.
            private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<bool> closed = new TaskCompletionSource<bool>();
            private TaskCompletionSource<WebSocketReceiveResult> receiving;
            private ArraySegment<byte> receiveBuffer;
            private byte[] current, pendingSend;
            private int currentOffset, queuedBytes;
            private TaskCompletionSource<bool> sending;
            private CancellationTokenRegistration receiveCancellation, sendCancellation, connectCancellation;
            private WebSocketState state = WebSocketState.Connecting;
            private BrowserSocket(Uri uri, CancellationToken token)
            {
                id = ++nextId; Sockets.Add(id, this); connectCancellation = token.Register(Abort);
                if (state != WebSocketState.Connecting) return;
                try { SJWS_Open(id, uri.AbsoluteUri, OpenedCallback, ClosedCallback, ErrorCallback, ReceivedCallback, MaximumMessageBytes); }
                catch { Fail(new WebSocketException("Browser WebSocket could not be created.")); }
            }
            internal static async Task<WebSocket> Connect(Uri uri, CancellationToken token)
            { var socket = new BrowserSocket(uri, token); await socket.connected.Task; socket.connectCancellation.Dispose(); return socket; }
            internal static void PumpAll()
            {
                Pump.Clear(); foreach (var socket in Sockets.Values) Pump.Add(socket);
                foreach (var socket in Pump) socket.TrySend();
            }
            public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : (WebSocketCloseStatus?)null;
            public override string CloseStatusDescription => state == WebSocketState.Closed ? "Browser WebSocket closed" : null;
            public override WebSocketState State => state;
            public override string SubProtocol => null;
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool endOfMessage, CancellationToken token)
            {
                if (state != WebSocketState.Open || type != WebSocketMessageType.Binary || !endOfMessage || buffer.Count > MaximumMessageBytes || sending != null)
                    throw new WebSocketException("Invalid concurrent, fragmented or oversized browser send.");
                if (token.IsCancellationRequested) return Task.FromCanceled(token);
                var task = new TaskCompletionSource<bool>(); sending = task; pendingSend = new byte[buffer.Count];
                if (buffer.Count > 0) Buffer.BlockCopy(buffer.Array, buffer.Offset, pendingSend, 0, buffer.Count);
                sendCancellation = token.Register(Abort); TrySend(); return task.Task;
            }
            private void TrySend()
            {
                if (sending == null || state != WebSocketState.Open) return;
                int result = SJWS_Send(id, pendingSend, 0, pendingSend.Length, MaximumQueuedBytes);
                if (result == 1) return; // Buffered browser output is full: retry from the next Unity Update.
                if (result != 0) { Fail(new WebSocketException("Browser WebSocket send failed.")); return; }
                var completion = sending; sending = null; pendingSend = null; sendCancellation.Dispose(); completion.TrySetResult(true);
            }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                if (receiving != null) throw new InvalidOperationException("Only one browser receive may be pending.");
                if (token.IsCancellationRequested) return Task.FromCanceled<WebSocketReceiveResult>(token);
                if (state != WebSocketState.Open) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                var completion = new TaskCompletionSource<WebSocketReceiveResult>(); receiving = completion; receiveBuffer = buffer; receiveCancellation = token.Register(Abort); Deliver(); return completion.Task;
            }
            private void Deliver()
            {
                if (receiving == null) return;
                if (current == null && messages.Count > 0) { current = messages.Dequeue(); currentOffset = 0; queuedBytes -= current.Length; }
                if (current == null) return;
                int count = Math.Min(receiveBuffer.Count, current.Length - currentOffset);
                Buffer.BlockCopy(current, currentOffset, receiveBuffer.Array, receiveBuffer.Offset, count); currentOffset += count;
                bool end = currentOffset == current.Length; if (end) current = null;
                var completion = receiving; receiving = null; receiveCancellation.Dispose(); completion.TrySetResult(new WebSocketReceiveResult(count, WebSocketMessageType.Binary, end));
            }
            private void Fail(Exception error)
            {
                if (state == WebSocketState.Aborted || state == WebSocketState.Closed) return;
                state = WebSocketState.Aborted; Sockets.Remove(id); SJWS_Close(id); messages.Clear(); current = pendingSend = null; queuedBytes = 0;
                var receive = receiving; receiving = null; var send = sending; sending = null;
                connected.TrySetException(error); receive?.TrySetException(error); send?.TrySetException(error); closed.TrySetResult(true);
            }
            public override void Abort() => Fail(new OperationCanceledException("Browser WebSocket was cancelled."));
            public override void Dispose() { Abort(); connectCancellation.Dispose(); receiveCancellation.Dispose(); sendCancellation.Dispose(); }
            public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return closed.Task; }
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void OnOpen(int id)
            { if (Sockets.TryGetValue(id, out var socket)) { socket.state = WebSocketState.Open; socket.connected.TrySetResult(true); } }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void OnClose(int id)
            {
                if (!Sockets.TryGetValue(id, out var socket)) return;
                socket.Fail(new WebSocketException("Browser WebSocket closed.")); socket.state = WebSocketState.Closed;
            }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void OnError(int id)
            { if (Sockets.TryGetValue(id, out var socket)) socket.Fail(new WebSocketException("Browser WebSocket connection failed.")); }
            [AOT.MonoPInvokeCallback(typeof(MessageCallback))] private static void OnMessage(int id, IntPtr data, int length)
            {
                if (!Sockets.TryGetValue(id, out var socket)) return;
                if (length < 0 || length > MaximumMessageBytes || socket.messages.Count >= MaximumQueuedMessages || length > MaximumQueuedBytes - socket.queuedBytes)
                { socket.Fail(new WebSocketException("Browser incoming queue exceeded its limit.")); return; }
                var bytes = new byte[length]; if (length > 0) Marshal.Copy(data, bytes, 0, length);
                socket.messages.Enqueue(bytes); socket.queuedBytes += length; socket.Deliver();
            }
        }
#endif
    }
}
