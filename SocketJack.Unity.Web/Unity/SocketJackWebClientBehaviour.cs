using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_WEBGL && !UNITY_EDITOR
using UnityEngine.Networking;
#endif
using SocketJack.Multiplayer.Components;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Unity main-thread adapter for the optional account/chat client.</summary>
    [DisallowMultipleComponent]
    public sealed class SocketJackWebClientBehaviour : MonoBehaviour
    {
        [SerializeField] private string serverAddress = "http://127.0.0.1:7778";
        private readonly ConcurrentQueue<Action> callbacks = new ConcurrentQueue<Action>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private int queued;
        private int accountEpoch;
        private bool reconnecting;
        private bool authenticating;
        private double nextReconnect;
        public SocketJackWebClient Client { get; private set; }
        public WebAccount User => Client?.User;
        public bool IsSignedIn => User != null;
        public bool IsLive => Client != null && Client.IsLive;
        public string LastError { get; private set; } = "";
        public event Action<WebChatMessage> MessageReceived;
        public event Action<string> StatusChanged;
        public event Action<WebAccount> SignedIn;
        public event Action SignedOut;
        private void Awake() => CreateClient();
        public void ConfigureEndpoint(string endpoint)
        {
            if (IsSignedIn) throw new InvalidOperationException("Sign out before changing the account server.");
            Interlocked.Increment(ref accountEpoch); Client?.Dispose(); serverAddress = endpoint; CreateClient();
        }
        private void CreateClient()
        {
            if (Client != null) Client.Dispose(); Client = new SocketJackWebClient(new Uri(serverAddress));
            var subscribedClient = Client;
            Client.ChatMessageReceived += message => { int epoch = Volatile.Read(ref accountEpoch); Post(() => { if (ReferenceEquals(subscribedClient, Client) && epoch == Volatile.Read(ref accountEpoch) && message.DeliveryEpoch == subscribedClient.SessionEpoch) MessageReceived?.Invoke(message); }); };
            Client.ConnectionChanged += status => Post(() => StatusChanged?.Invoke(status));
#if UNITY_WEBGL && !UNITY_EDITOR
            Client.PreserveSynchronizationContext = true;
            Client.HttpRequestOverride = HttpRequestAsync;
            Client.SocketFactory = () => new SocketJackBrowserApiSocket();
#endif
        }
        private void Post(Action action)
        {
            if (lifetime.IsCancellationRequested) return;
            if (Interlocked.Increment(ref queued) > 1024) { Interlocked.Decrement(ref queued); return; }
            callbacks.Enqueue(action);
        }
        private void Update()
        {
            for (int i = 0; i < 128 && callbacks.TryDequeue(out var action); i++) { Interlocked.Decrement(ref queued); try { action(); } catch (Exception error) { Debug.LogException(error, this); } }
            if (IsSignedIn && !IsLive && !authenticating && !reconnecting && Time.unscaledTimeAsDouble >= nextReconnect) { nextReconnect = Time.unscaledTimeAsDouble + 5; _ = ReconnectAsync(); }
        }
        private async Task ReconnectAsync()
        {
            if (reconnecting) return;
            reconnecting = true;
            try { await Client.StartRealtimeAsync(lifetime.Token); LastError = ""; }
            catch (OperationCanceledException) { }
            catch (Exception error) { LastError = error.Message; StatusChanged?.Invoke("Reconnecting"); }
            finally { reconnecting = false; }
        }
        public async Task<WebAccount> LoginAsync(string username, string password)
        {
            authenticating = true; Interlocked.Increment(ref accountEpoch); Client.StopRealtime();
            try { var user = await Client.LoginAsync(username, password, lifetime.Token); SignedIn?.Invoke(user); await ReconnectAsync(); return user; }
            finally { authenticating = false; }
        }
        public async Task<WebAccount> RegisterAsync(string username, string password, string displayName)
        {
            authenticating = true; Interlocked.Increment(ref accountEpoch); Client.StopRealtime();
            try { var user = await Client.RegisterAsync(username, password, displayName, lifetime.Token); SignedIn?.Invoke(user); await ReconnectAsync(); return user; }
            finally { authenticating = false; }
        }
        public async Task LogoutAsync() { Interlocked.Increment(ref accountEpoch); Client.StopRealtime(); await Client.LogoutAsync(lifetime.Token); SignedOut?.Invoke(); }
        public Task<WebChatMessage> SendAsync(string channel, string text, string scopeId = "", string targetUsername = "", string targetId = "")
            => Client.SendChatAsync(channel, text, scopeId, targetUsername, targetId, lifetime.Token);
        public Task<IReadOnlyList<WebChatMessage>> GetHistoryAsync(string channel, string scopeId = "", int limit = 100)
            => Client.GetHistoryAsync(channel, scopeId, limit, "", lifetime.Token);
        public async Task JoinGameAsync(NetworkManager manager, string serverId = "")
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            byte[] connectionData = await Client.GetGameConnectionDataAsync(serverId, lifetime.Token);
            if (manager.Topology == NetworkTopology.HostPeer) manager.StartPeer(connectionData); else manager.StartClient(connectionData);
        }
        public async Task ConnectLocalHostAsync(NetworkManager manager, string serverId = "")
        {
            if (manager == null || !manager.IsServer || manager.Topology != NetworkTopology.HostPeer) throw new InvalidOperationException("StartHostListener must be running before authenticating its local player.");
            byte[] connectionData = await Client.GetGameConnectionDataAsync(serverId, lifetime.Token); manager.ConnectLocalHost(connectionData);
        }
#if UNITY_WEBGL && !UNITY_EDITOR
        private async Task<string> HttpRequestAsync(Uri endpoint, string method, string json, string bearer, CancellationToken token)
        {
            var completion = new TaskCompletionSource<string>();
            using (token.Register(() => completion.TrySetCanceled()))
            {
                Post(() => StartCoroutine(SendHttp(endpoint, method, json, bearer, token, completion)));
                return await completion.Task;
            }
        }
        private IEnumerator SendHttp(Uri endpoint, string method, string json, string bearer, CancellationToken token, TaskCompletionSource<string> completion)
        {
            using (var request = new UnityWebRequest(endpoint, method))
            {
                request.timeout = 15; request.redirectLimit = 0; request.downloadHandler = new BoundedDownloadHandler(2 * 1024 * 1024);
                if (json != null) { request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json)); request.SetRequestHeader("Content-Type", "application/json"); }
                if (bearer != null) request.SetRequestHeader("Authorization", "Bearer " + bearer);
                var sending = request.SendWebRequest();
                while (!sending.isDone) { if (token.IsCancellationRequested || lifetime.IsCancellationRequested) { request.Abort(); completion.TrySetCanceled(); yield break; } yield return null; }
                if (request.result == UnityWebRequest.Result.ConnectionError || request.result == UnityWebRequest.Result.DataProcessingError) completion.TrySetException(new IOException("The account/chat server could not be reached."));
                else completion.TrySetResult(request.downloadHandler.text);
            }
        }
        private sealed class BoundedDownloadHandler : DownloadHandlerScript
        {
            private readonly MemoryStream collected = new MemoryStream(); private readonly int maximum;
            internal BoundedDownloadHandler(int maximum) : base(new byte[8192]) { this.maximum = maximum; }
            protected override bool ReceiveData(byte[] bytes, int count) { if (bytes == null || count < 0 || collected.Length + count > maximum) return false; collected.Write(bytes, 0, count); return true; }
            protected override byte[] GetData() => collected.ToArray();
            protected override string GetText() => Encoding.UTF8.GetString(collected.ToArray());
        }
#endif
        private void OnDestroy() { lifetime.Cancel(); Client?.Dispose(); lifetime.Dispose(); }
    }
}
