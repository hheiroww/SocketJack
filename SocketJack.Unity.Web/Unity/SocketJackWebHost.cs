using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using UnityEngine;

namespace SocketJack.Multiplayer.Web.Components
{
    /// <summary>Optional embedded website/accounts host. Only a native listening server opens the website.</summary>
    [DefaultExecutionOrder(-8000), DisallowMultipleComponent, RequireComponent(typeof(NetworkManager))]
    public sealed class SocketJackWebHost : MonoBehaviour
    {
        [SerializeField] private NetworkManager network;
        [SerializeField] private string serverName = "SocketJack Multiplayer";
        [SerializeField] private string nodeId = "local";
        [SerializeField] private string worldId = "";
        [SerializeField] private string bindAddress = "127.0.0.1";
        [SerializeField, Range(1, 65535)] private int webPort = 7778;
        [SerializeField, Tooltip("Empty permits same-origin browsers and native clients without Origin. Add exact HTTPS origins for a separately hosted browser game; wildcards are not accepted.")]
        private string[] allowedOrigins = Array.Empty<string>();
        [SerializeField, Tooltip("Empty uses Application.persistentDataPath/SocketJack. Never place account data under website/source assets.")]
        private string dataDirectory = "";
        [SerializeField, Tooltip("PFX certificate path for network-facing HTTPS/WSS. Keep certificates and passwords outside game assets.")]
        private string certificatePath = "";
        [SerializeField] private string certificatePasswordEnvironment = "SOCKETJACK_WEB_CERTIFICATE_PASSWORD";
        [SerializeField, Tooltip("Empty hosts a canonical backend. Set the canonical HTTPS hub origin to link this server.")]
        private string hubAddress = "";
        [SerializeField] private string enrollmentTokenEnvironment = "SOCKETJACK_NODE_ENROLLMENT_TOKEN";
        [SerializeField] private bool requireGameAccount;
        [SerializeField] private bool enableRelay = true;
        [SerializeField] private int datagramRelayPort = -1;
        [SerializeField] private string publicRelayHost = "";
        private SqliteAccountStore store;
        private X509Certificate2 certificate;
        private readonly Dictionary<ulong, WebAccount> authenticatedPeers = new Dictionary<ulong, WebAccount>();
        private readonly ConcurrentQueue<string> revoked = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<string> errors = new ConcurrentQueue<string>();
        private readonly HashSet<string> lastPublished = new HashSet<string>(StringComparer.Ordinal);
        private Func<IPEndPoint, byte[], bool> previousValidator;
        private Func<IPEndPoint, byte[], bool> installedValidator;
        private double nextSnapshot;
        private bool starting, destroyed;
        private Task stopping = Task.CompletedTask;
        public SocketJackWebServer Server { get; private set; }
        public string LastError { get; private set; } = "";
        public string WebsiteAddress { get; private set; } = "";
        public string BootstrapCode => Server?.BootstrapCode;
        public bool RequireGameAccount { get => requireGameAccount; set => requireGameAccount = value; }
        /// <summary>Optional trusted main-thread selector for games using their own player spawner.</summary>
        public Func<ulong, NetworkIdentity> PresenceObjectResolver { get; set; }
        public event Action<SocketJackWebServer> Started;
        public event Action<string> Error;
        public bool TryGetAuthenticatedAccount(ulong peerId, out WebAccount account) => authenticatedPeers.TryGetValue(peerId, out account);
        public void Configure(string name, string id, int port, string directory, string bind = "127.0.0.1", string hub = "")
        {
            if (Server != null || starting) throw new InvalidOperationException("Configure the web host before starting.");
            serverName = name; nodeId = id; webPort = port; dataDirectory = directory; bindAddress = bind; hubAddress = hub;
        }
        private void Awake()
        {
            if (network == null) network = GetComponent<NetworkManager>();
            previousValidator = network.ConnectionValidator;
            installedValidator = (endpoint, data) =>
            {
                if (previousValidator != null && !previousValidator(endpoint, data)) return false;
                bool hasTicket = data != null && data.Length >= 6 && System.Text.Encoding.ASCII.GetString(data, 0, 6) == "SJAT1:";
                return !requireGameAccount && !hasTicket || Server != null && Server.ValidateGameConnection(endpoint, data);
            };
            network.ConnectionValidator = installedValidator;
            network.ListenerStarted += OnListenerStarted; network.SessionStopped += OnSessionStopped;
            network.PeerConnected += OnPeerConnected; network.PeerDisconnected += OnPeerDisconnected;
        }
        private void OnListenerStarted() { _ = StartWebsiteAsync(); }
        public async Task StartWebsiteAsync()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException("Browser clients cannot host an HTTP/WebSocket listener.");
#else
            if (starting || Server != null || destroyed) return;
            starting = true;
            try
            {
                await stopping;
                string directory = string.IsNullOrWhiteSpace(dataDirectory) ? Path.Combine(Application.persistentDataPath, "SocketJack", nodeId) : Path.GetFullPath(dataDirectory);
                if (directory.StartsWith(Application.dataPath, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Store account data outside the Unity Assets directory.");
                string certificateFile = certificatePath;
                string certificatePassword = string.IsNullOrEmpty(certificatePasswordEnvironment) ? null : Environment.GetEnvironmentVariable(certificatePasswordEnvironment);
                var configuration = new SocketJackWebOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Parse(bindAddress), webPort), ServerName = serverName, NodeId = nodeId,
                    AllowedOrigins = allowedOrigins == null ? Array.Empty<string>() : (string[])allowedOrigins.Clone(),
                    HubEndPoint = string.IsNullOrWhiteSpace(hubAddress) ? null : new Uri(hubAddress),
                    EnrollmentToken = string.IsNullOrWhiteSpace(enrollmentTokenEnvironment) ? null : Environment.GetEnvironmentVariable(enrollmentTokenEnvironment)
                    , EnableRelay = enableRelay, DatagramRelayPort = datagramRelayPort, PublicRelayHost = publicRelayHost
                };
                // Disk, SQLite schema setup and certificate loading never block Unity's render/update loop.
                var resources = await Task.Run(() =>
                {
                    X509Certificate2 loaded = null; SqliteAccountStore database = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(certificateFile)) loaded = new X509Certificate2(certificateFile, certificatePassword);
                        configuration.Certificate = loaded; database = new SqliteAccountStore(Path.Combine(directory, "accounts.sqlite"));
                        return (Database: database, Certificate: loaded, Host: new SocketJackWebServer(database, database, configuration));
                    }
                    catch { database?.Dispose(); loaded?.Dispose(); throw; }
                });
                if (destroyed || !network.IsServer) { await resources.Host.DisposeAsync(); resources.Database.Dispose(); resources.Certificate?.Dispose(); return; }
                store = resources.Database; certificate = resources.Certificate; Server = resources.Host;
                Server.ServerError += error => { if (errors.Count < 32) errors.Enqueue("The web service could not complete an operation. Check its configuration and diagnostics."); };
                Server.AccountRevoked += id => revoked.Enqueue(id);
                WebsiteAddress = (certificate == null ? "http://" : "https://") + (bindAddress == "0.0.0.0" || bindAddress == "::" ? "localhost" : bindAddress) + ":" + webPort;
                var current = Server; await Task.Run(() => current.StartAsync());
                if (destroyed) return;
                LastError = ""; Started?.Invoke(Server);
            }
            catch (Exception error)
            {
                if (!destroyed) { LastError = error.Message; Error?.Invoke(LastError); Debug.LogError("SocketJack website: " + LastError, this); }
                await StopWebsiteAsync();
            }
            finally { starting = false; }
#endif
        }
        private void OnPeerConnected(ulong peerId)
        {
            var current = Server;
            if (current == null || network.Server == null) return;
            var account = current.BindAcceptedGameConnection(network.Server.GetConnectionData(peerId));
            if (account != null) authenticatedPeers[peerId] = account;
        }
        private void OnPeerDisconnected(ulong peerId, string reason)
        {
            if (authenticatedPeers.TryGetValue(peerId, out var account)) Server?.Presence.RemoveGamePresence(account.Id);
            authenticatedPeers.Remove(peerId);
        }
        private void Update()
        {
            for (int i = 0; i < 16 && errors.TryDequeue(out string error); i++) { LastError = error; Error?.Invoke(error); }
            for (int i = 0; i < 128 && revoked.TryDequeue(out string id); i++)
            {
                var peers = new List<ulong>(); foreach (var pair in authenticatedPeers) if (pair.Value.Id == id) peers.Add(pair.Key);
                foreach (var peer in peers) network.Server?.Transport.Disconnect(peer, "Account session revoked");
            }
            var current = Server; if (current == null || !network.IsServer || Time.unscaledTimeAsDouble < nextSnapshot) return;
            nextSnapshot = Time.unscaledTimeAsDouble + 0.5;
            var snapshots = new List<FederationPresence>(); var present = new HashSet<string>(StringComparer.Ordinal);
            var spawner = network.GetComponent<NetworkPlayerSpawner>();
            foreach (var pair in authenticatedPeers)
            {
                NetworkIdentity identity = PresenceObjectResolver == null ? null : PresenceObjectResolver(pair.Key);
                if (identity == null && spawner != null) spawner.TryGetPlayer(pair.Key, out identity);
                if (identity == null || identity.OwnerId != pair.Key) continue;
                var account = pair.Value; if (!present.Add(account.Id)) continue;
                var p = identity.transform.position; string world = string.IsNullOrWhiteSpace(worldId) ? nodeId : worldId;
                var position = new System.Numerics.Vector3(p.x, p.y, p.z);
                current.Presence.UpdateGamePresence(account, world, identity.RoomId, position);
                snapshots.Add(new FederationPresence { AccountId = account.Id, WorldId = world, RoomId = identity.RoomId, X = p.x, Y = p.y, Z = p.z });
            }
            var removed = new List<string>(); foreach (var account in lastPublished) if (!present.Contains(account)) { removed.Add(account); current.Presence.RemoveGamePresence(account); }
            lastPublished.Clear(); foreach (var account in present) lastPublished.Add(account);
            current.PublishNodePresence(new FederationPresenceUpdate { Players = snapshots, RemovedAccountIds = removed.ToArray() });
            current.UpdateStatus(new WebServerStatus { Name = serverName, Version = typeof(NetworkServer).Assembly.GetName().Version.ToString(), Players = network.Server.Peers.Count, Objects = network.ObjectCount, TickRate = network.SimulationTickRate, Transport = network.Transport.ToString() });
            var metrics = TransportMetrics.Source(network.Server.Transport)?.GetTransportMetrics(64);
            current.UpdateDiagnostics(new { transport = metrics, objects = network.ObjectCount, players = network.Server.Peers.Count, tick = network.Server.Tick });
        }
        private void OnSessionStopped() { authenticatedPeers.Clear(); lastPublished.Clear(); stopping = StopWebsiteAsync(); }
        public async Task StopWebsiteAsync()
        {
            var current = Server; Server = null; var database = store; store = null; var cert = certificate; certificate = null;
            WebsiteAddress = "";
            try { if (current != null) await current.DisposeAsync(); }
            finally { database?.Dispose(); cert?.Dispose(); }
        }
        private void OnDestroy()
        {
            destroyed = true;
            if (network != null)
            {
                network.ListenerStarted -= OnListenerStarted; network.SessionStopped -= OnSessionStopped;
                network.PeerConnected -= OnPeerConnected; network.PeerDisconnected -= OnPeerDisconnected;
                if (network.ConnectionValidator == installedValidator) network.ConnectionValidator = previousValidator;
            }
            _ = StopWebsiteAsync();
        }
    }
}
