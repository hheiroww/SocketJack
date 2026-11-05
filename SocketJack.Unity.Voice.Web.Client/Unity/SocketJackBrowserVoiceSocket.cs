using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
#endif

namespace SocketJack.Multiplayer.Voice.Web.Components
{
    /// <summary>Browser-native text-authentication/binary-audio WebSocket connector. Add beside
    /// SocketJackWebVoiceClient before signing in. Tickets remain in the first text message, never
    /// in URLs. HTTPS pages require WSS with a publicly trusted certificate. Native players retain
    /// the ordinary ClientWebSocket implementation.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(SocketJackWebVoiceClient)), DefaultExecutionOrder(-300)]
    public sealed class SocketJackBrowserVoiceSocket : MonoBehaviour
    {
        private SocketJackWebVoiceClient voice;
#if UNITY_WEBGL && !UNITY_EDITOR
        private readonly HashSet<BrowserSocket> sockets = new HashSet<BrowserSocket>();
        private readonly List<BrowserSocket> schedule = new List<BrowserSocket>();
#endif
        private void Awake()
        {
            voice = GetComponent<SocketJackWebVoiceClient>();
#if UNITY_WEBGL && !UNITY_EDITOR
            voice.SocketConnector = ConnectAsync;
#endif
        }
        public async Task<WebSocket> ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
        {
            if (!isActiveAndEnabled) throw new InvalidOperationException("Enable the browser voice connector before connecting.");
            if (endpoint == null || !endpoint.IsAbsoluteUri || (endpoint.Scheme != "ws" && endpoint.Scheme != "wss") || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Use a ws/wss voice endpoint without credentials, query or fragment.");
#if UNITY_WEBGL && !UNITY_EDITOR
            var socket = new BrowserSocket(this, endpoint, cancellationToken);
            try { await socket.Opened; return socket; } catch { socket.Dispose(); throw; }
#else
            var socket = new ClientWebSocket();
            try { await socket.ConnectAsync(endpoint, cancellationToken); return socket; } catch { socket.Dispose(); throw; }
#endif
        }
        private void Update()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            schedule.Clear(); foreach (var socket in sockets) schedule.Add(socket);
            foreach (var socket in schedule) socket.Pump();
#endif
        }
        private void OnDisable()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            voice?.Disconnect(); schedule.Clear(); foreach (var socket in sockets) schedule.Add(socket);
            foreach (var socket in schedule) socket.Dispose(); schedule.Clear();
#endif
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        private sealed class BrowserSocket : WebSocket
        {
            private const int MessageLimit = 1024, QueueLimit = 32, ByteLimit = 32 * 1024;
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StateCallback(int id);
            [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DataCallback(int id, IntPtr bytes, int length, int text);
            private static readonly StateCallback OpenCallback = Open, CloseCallback = Closed, ErrorCallback = Failed;
            private static readonly DataCallback MessageCallback = Message;
            private static readonly Dictionary<int, BrowserSocket> All = new Dictionary<int, BrowserSocket>();
            private static int nextId;
            [DllImport("__Internal")] private static extern void SJVoiceSocket_Open(int id, string url, StateCallback open, StateCallback close, StateCallback error, DataCallback message, int limit);
            [DllImport("__Internal")] private static extern int SJVoiceSocket_Send(int id, byte[] bytes, int length, int text, int bufferedLimit);
            [DllImport("__Internal")] private static extern void SJVoiceSocket_Close(int id);
            private sealed class Incoming { internal byte[] Bytes; internal WebSocketMessageType Type; }
            private readonly SocketJackBrowserVoiceSocket owner;
            private readonly int id;
            private readonly Queue<Incoming> incoming = new Queue<Incoming>();
            private readonly TaskCompletionSource<bool> opened = new TaskCompletionSource<bool>();
            private readonly long started = Stopwatch.GetTimestamp();
            private CancellationTokenRegistration connectCancellation, receiveCancellation, sendCancellation;
            private TaskCompletionSource<WebSocketReceiveResult> receiving;
            private TaskCompletionSource<bool> sending;
            private ArraySegment<byte> receiveBuffer;
            private Incoming current;
            private int offset, queuedBytes;
            private byte[] pendingSend;
            private WebSocketMessageType pendingType;
            private long sendAt;
            private bool receivedFirst;
            private WebSocketState state = WebSocketState.Connecting;
            internal Task Opened => opened.Task;
            internal BrowserSocket(SocketJackBrowserVoiceSocket owner, Uri endpoint, CancellationToken token)
            {
                this.owner = owner; id = ++nextId; All.Add(id, this); owner.sockets.Add(this);
                connectCancellation = token.Register(Abort);
                if (state != WebSocketState.Connecting) return;
                try { SJVoiceSocket_Open(id, endpoint.AbsoluteUri, OpenCallback, CloseCallback, ErrorCallback, MessageCallback, MessageLimit); }
                catch { Fail(new WebSocketException("Browser voice socket could not open.")); }
            }
            internal void Pump()
            {
                double elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                if ((state == WebSocketState.Connecting || (state == WebSocketState.Open && !receivedFirst)) && elapsed > 10) { Fail(new WebSocketException("Browser voice connection/authentication timed out.")); return; }
                if (sending == null || state != WebSocketState.Open) return;
                if ((Stopwatch.GetTimestamp() - sendAt) / (double)Stopwatch.Frequency > 1) { Fail(new WebSocketException("Browser voice send was congested.")); return; }
                int result = SJVoiceSocket_Send(id, pendingSend, pendingSend.Length, pendingType == WebSocketMessageType.Text ? 1 : 0, 2048);
                if (result == 1) return;
                if (result != 0) { Fail(new WebSocketException("Browser voice send failed.")); return; }
                var completion = sending; sending = null; pendingSend = null; sendCancellation.Dispose(); completion.TrySetResult(true);
            }
            public override WebSocketState State => state;
            public override WebSocketCloseStatus? CloseStatus => state == WebSocketState.Closed ? WebSocketCloseStatus.NormalClosure : (WebSocketCloseStatus?)null;
            public override string CloseStatusDescription => null;
            public override string SubProtocol => null;
            public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken token)
            {
                if (token.IsCancellationRequested) return Task.FromCanceled(token);
                if (state != WebSocketState.Open || sending != null || !end || buffer.Count > MessageLimit || (type != WebSocketMessageType.Text && type != WebSocketMessageType.Binary))
                    throw new WebSocketException("Browser voice requires one bounded complete text/binary message per send.");
                var completion = new TaskCompletionSource<bool>(); sending = completion; pendingSend = new byte[buffer.Count]; pendingType = type; sendAt = Stopwatch.GetTimestamp();
                if (buffer.Count != 0) Buffer.BlockCopy(buffer.Array, buffer.Offset, pendingSend, 0, buffer.Count);
                sendCancellation = token.Register(Abort); Pump(); return completion.Task;
            }
            public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
            {
                if (token.IsCancellationRequested) return Task.FromCanceled<WebSocketReceiveResult>(token);
                if (receiving != null) throw new InvalidOperationException("Only one browser voice receive may be pending.");
                if (state != WebSocketState.Open) return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
                var completion = new TaskCompletionSource<WebSocketReceiveResult>(); receiving = completion; receiveBuffer = buffer;
                receiveCancellation = token.Register(Abort); Deliver(); return completion.Task;
            }
            private void Deliver()
            {
                if (receiving == null) return;
                if (current == null && incoming.Count > 0) { current = incoming.Dequeue(); offset = 0; }
                if (current == null) return;
                var type = current.Type; int count = Math.Min(receiveBuffer.Count, current.Bytes.Length - offset);
                if (count != 0) Buffer.BlockCopy(current.Bytes, offset, receiveBuffer.Array, receiveBuffer.Offset, count); offset += count;
                bool end = offset == current.Bytes.Length;
                if (end) { queuedBytes -= current.Bytes.Length; current = null; offset = 0; }
                var completion = receiving; receiving = null; receiveCancellation.Dispose(); completion.TrySetResult(new WebSocketReceiveResult(count, type, end));
            }
            private void Fail(Exception error)
            {
                if (state == WebSocketState.Aborted || state == WebSocketState.Closed) return;
                state = WebSocketState.Aborted; All.Remove(id); owner.sockets.Remove(this); SJVoiceSocket_Close(id);
                incoming.Clear(); current = null; pendingSend = null; queuedBytes = 0;
                var receiver = receiving; receiving = null; var sender = sending; sending = null;
                opened.TrySetException(error); receiver?.TrySetException(error); sender?.TrySetException(error);
            }
            public override void Abort() => Fail(new OperationCanceledException("Browser voice socket was cancelled."));
            public override void Dispose() { Abort(); connectCancellation.Dispose(); receiveCancellation.Dispose(); sendCancellation.Dispose(); }
            public override Task CloseAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
            public override Task CloseOutputAsync(WebSocketCloseStatus status, string description, CancellationToken token) { Abort(); return Task.CompletedTask; }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void Open(int id)
            { if (All.TryGetValue(id, out var socket)) { socket.state = WebSocketState.Open; socket.connectCancellation.Dispose(); socket.opened.TrySetResult(true); } }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void Closed(int id)
            { if (All.TryGetValue(id, out var socket)) { socket.Fail(new WebSocketException("Browser voice socket closed.")); socket.state = WebSocketState.Closed; } }
            [AOT.MonoPInvokeCallback(typeof(StateCallback))] private static void Failed(int id)
            { if (All.TryGetValue(id, out var socket)) socket.Fail(new WebSocketException("Browser voice socket failed.")); }
            [AOT.MonoPInvokeCallback(typeof(DataCallback))] private static void Message(int id, IntPtr data, int length, int text)
            {
                if (!All.TryGetValue(id, out var socket)) return;
                if (length < 0 || length > MessageLimit || socket.incoming.Count >= QueueLimit || length > ByteLimit - socket.queuedBytes || (text != 0 && text != 1))
                { socket.Fail(new WebSocketException("Browser voice incoming queue exceeded its bound.")); return; }
                var bytes = new byte[length]; if (length != 0) Marshal.Copy(data, bytes, 0, length);
                socket.receivedFirst = true; socket.queuedBytes += length;
                socket.incoming.Enqueue(new Incoming { Bytes = bytes, Type = text == 1 ? WebSocketMessageType.Text : WebSocketMessageType.Binary }); socket.Deliver();
            }
        }
#endif
    }
}
