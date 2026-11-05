#if UNITY_WEBGL && !UNITY_EDITOR
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Scripting;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Browser-native text WebSockets for the optional account/chat API. Gameplay uses its separate bridge.</summary>
    public sealed class SocketJackBrowserApiSocket : IWebApiSocket
    {
        private readonly ConcurrentQueue<string> received = new ConcurrentQueue<string>();
        private readonly object receiveGate = new object();
        private TaskCompletionSource<bool> available;
        private readonly TaskCompletionSource<bool> connected = new TaskCompletionSource<bool>();
        private readonly int id;
        private bool disposed, closed;
        private int queued;
        public SocketJackBrowserApiSocket() { id = SocketJackWebApiDispatcher.Register(this); }
        public async Task ConnectAsync(Uri endpoint, CancellationToken token)
        {
            using (token.Register(() => { connected.TrySetCanceled(); Dispose(); }))
            {
                SocketJackWebApiDispatcher.Schedule(() => SJWebApi_Open(id, endpoint.AbsoluteUri, SocketJackWebApiDispatcher.TargetName));
                await connected.Task;
            }
        }
        public Task SendAsync(string json, CancellationToken token)
        {
            if (disposed || closed || token.IsCancellationRequested) return Task.FromCanceled(token.IsCancellationRequested ? token : new CancellationToken(true));
            if (json == null || json.Length > 16384) throw new ArgumentException("API frame exceeds its bound.");
            var completion = new TaskCompletionSource<bool>();
            SocketJackWebApiDispatcher.Schedule(() => { if (disposed || SJWebApi_Send(id, json) == 0) completion.TrySetException(new InvalidOperationException("Browser WebSocket is unavailable or congested.")); else completion.TrySetResult(true); });
            return completion.Task;
        }
        public async Task<string> ReceiveAsync(CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested(); TaskCompletionSource<bool> signal;
                lock (receiveGate)
                {
                    if (received.TryDequeue(out string value)) { Interlocked.Decrement(ref queued); return value; }
                    if (closed || disposed) return null;
                    if (available != null) throw new InvalidOperationException("Only one API receive may wait at a time.");
                    signal = available = new TaskCompletionSource<bool>();
                }
                using (token.Register(() => signal.TrySetCanceled()))
                    try { await signal.Task; }
                    finally { lock (receiveGate) if (ReferenceEquals(available, signal)) available = null; }
            }
        }
        internal void Opened() => connected.TrySetResult(true);
        internal void Message(string text)
        {
            if (disposed || text == null) return;
            if (text.Length > 128 * 1024 || Interlocked.Increment(ref queued) > 128) { Interlocked.Decrement(ref queued); Failed(); Dispose(); return; }
            received.Enqueue(text); WakeReceiver();
        }
        private void WakeReceiver() { TaskCompletionSource<bool> signal; lock (receiveGate) { signal = available; available = null; } signal?.TrySetResult(true); }
        internal void Failed() { closed = true; connected.TrySetException(new InvalidOperationException("Browser WebSocket connection failed.")); WakeReceiver(); }
        internal void Closed() { closed = true; connected.TrySetException(new InvalidOperationException("Browser WebSocket closed before connecting.")); WakeReceiver(); }
        public void Dispose()
        {
            if (disposed) return; disposed = true; closed = true; connected.TrySetCanceled(); WakeReceiver();
            SocketJackWebApiDispatcher.Schedule(() => { SJWebApi_Close(id); SocketJackWebApiDispatcher.Unregister(id); });
        }
        [DllImport("__Internal")] private static extern void SJWebApi_Open(int id, string uri, string target);
        [DllImport("__Internal")] private static extern int SJWebApi_Send(int id, string message);
        [DllImport("__Internal")] private static extern void SJWebApi_Close(int id);
    }
    [Preserve]
    public sealed class SocketJackWebApiDispatcher : MonoBehaviour
    {
        internal const string TargetName = "SocketJackWebApiBridge";
        private static readonly Dictionary<int, SocketJackBrowserApiSocket> sockets = new Dictionary<int, SocketJackBrowserApiSocket>();
        private static readonly ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
        private static SocketJackWebApiDispatcher instance;
        private static int nextId;
        internal static int Register(SocketJackBrowserApiSocket socket)
        {
            if (instance == null) { var root = new GameObject(TargetName); DontDestroyOnLoad(root); instance = root.AddComponent<SocketJackWebApiDispatcher>(); }
            int id = ++nextId; sockets.Add(id, socket); return id;
        }
        internal static void Unregister(int id) => sockets.Remove(id);
        internal static void Schedule(Action action) => actions.Enqueue(action);
        private void Update() { for (int i = 0; i < 256 && actions.TryDequeue(out var action); i++) action(); }
        [Preserve] public void OnSocketOpen(string value) { if (int.TryParse(value, out int id) && sockets.TryGetValue(id, out var socket)) socket.Opened(); }
        [Preserve] public void OnSocketError(string value) { if (int.TryParse(value, out int id) && sockets.TryGetValue(id, out var socket)) socket.Failed(); }
        [Preserve] public void OnSocketClose(string value) { if (int.TryParse(value, out int id) && sockets.TryGetValue(id, out var socket)) socket.Closed(); }
        [Preserve] public void OnSocketMessage(string value)
        {
            int separator = value.IndexOf('|'); if (separator < 1 || !int.TryParse(value.Substring(0, separator), out int id) || !sockets.TryGetValue(id, out var socket)) return;
            socket.Message(value.Substring(separator + 1));
        }
    }
}
#endif
