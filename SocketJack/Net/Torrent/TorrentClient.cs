using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Net.Database;

namespace SocketJack.Net.Torrent {

    /// <summary>
    /// A BitTorrent-style client built on SocketJack's <see cref="TcpClient"/> and <see cref="TcpServer"/>.
    /// <para>
    /// Connects to a <see cref="TorrentTracker"/> to discover peers, then downloads/uploads
    /// file pieces using a simplified peer wire protocol. Includes support for searching
    /// torrents on the tracker.
    /// </para>
    /// <para>
    /// Can operate in <b>standalone mode</b> (own <see cref="TcpServer"/> for peer connections)
    /// or be <b>registered</b> on a <see cref="MutableTcpServer"/> to share a port with
    /// HTTP, WebSocket, and other SocketJack services.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Example — standalone seeding:</b>
    /// <code>
    /// var metadata = TorrentMetadata.FromFile("myfile.zip", new List&lt;string&gt; { "localhost:6969" });
    /// var client = new TorrentClient(metadata, listenPort: 6881, downloadPath: "./downloads");
    /// client.Seed("myfile.zip");
    /// await client.StartAsync();
    /// </code>
    ///
    /// <b>Example — registered on MutableTcpServer:</b>
    /// <code>
    /// var server = new MutableTcpServer(8080, "MultiServer");
    /// server.EnabledProtocols.UnionWith(new[] { MutableTcpProtocols.Http, MutableTcpProtocols.SocketJack });
    /// var client = new TorrentClient(metadata, downloadPath: "./downloads");
    /// client.Register(server);
    /// client.Seed("myfile.zip");
    /// server.Listen();
    /// await client.StartAsync();
    /// </code>
    ///
    /// <b>Example — searching for torrents:</b>
    /// <code>
    /// var client = new TorrentClient(metadata, listenPort: 6883, downloadPath: "./downloads");
    /// var results = await client.SearchAsync("linux iso", category: "software");
    /// </code>
    /// </remarks>
    public class TorrentClient : IDisposable {

        #region Static Database

        private static readonly object _dbInitLock = new object();
        private static DataServer _torrentsDb;

        /// <summary>
        /// Shared in-memory database that persists the state of all torrent transfers.
        /// <para>
        /// Uses <see cref="DataServer"/> with <see cref="DataServer.AutoSave"/> enabled
        /// so every mutation is flushed to <c>torrents.json</c> on disk.
        /// </para>
        /// </summary>
        public static DataServer Torrents {
            get {
                if (_torrentsDb == null) {
                    lock (_dbInitLock) {
                        if (_torrentsDb == null) {
                            _torrentsDb = new DataServer(0, "Torrents");
                            _torrentsDb.DataPath = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "SocketJack", "torrents.json");
                            _torrentsDb.AutoSave = true;
                            _torrentsDb.Load();
                            EnsureTorrentsSchema();
                        }
                    }
                }
                return _torrentsDb;
            }
        }

        /// <summary>
        /// Ensures the Torrents database has the required schema.
        /// </summary>
        private static void EnsureTorrentsSchema() {
            var db = _torrentsDb.Databases.GetOrAdd("Torrents", _ => new Database.Database("Torrents"));

            if (!db.Tables.ContainsKey("Transfers")) {
                db.Tables["Transfers"] = new Table("Transfers") {
                    Columns = new List<Column> {
                        new Column("InfoHash",     typeof(string), 64),
                        new Column("Name",         typeof(string), 512),
                        new Column("TotalSize",    typeof(long)),
                        new Column("PieceCount",   typeof(int)),
                        new Column("PieceSize",    typeof(int)),
                        new Column("Progress",     typeof(double)),
                        new Column("BytesDownloaded", typeof(long)),
                        new Column("Status",       typeof(string), 32),
                        new Column("Trackers",     typeof(string), 2048),
                        new Column("DownloadPath", typeof(string), 1024),
                        new Column("MagnetUri",    typeof(string), 2048),
                        new Column("AddedUtc",     typeof(string), 32),
                        new Column("CompletedUtc", typeof(string), 32)
                    }
                };
            }
        }

        /// <summary>
        /// Persists the current state of this torrent client to the Torrents database.
        /// </summary>
        public void SaveState(string status = null, string magnetUri = null) {
            try {
                if (Metadata == null) return;
                Database.Database db;
                if (!Torrents.Databases.TryGetValue("Torrents", out db)) return;
                Table table;
                if (!db.Tables.TryGetValue("Transfers", out table)) return;

            string infoHash = Metadata.InfoHash;
            string currentStatus = status;
            if (currentStatus == null) {
                currentStatus = IsComplete ? "Complete" : "Downloading";
            }
            string trackers = string.Join(";", Metadata.Trackers);
            string completedUtc = IsComplete ? DateTime.UtcNow.ToString("o") : "";

            // Find existing row by InfoHash
            int existingIndex = -1;
            for (int i = 0; i < table.Rows.Count; i++) {
                if (table.Rows[i].Length > 0 && string.Equals(table.Rows[i][0]?.ToString(), infoHash, StringComparison.OrdinalIgnoreCase)) {
                    existingIndex = i;
                    break;
                }
            }

            var row = new object[] {
                infoHash,
                Metadata.Name,
                Metadata.TotalSize,
                Metadata.PieceCount,
                Metadata.PieceSize,
                Progress,
                BytesDownloaded,
                currentStatus,
                trackers,
                DownloadPath,
                magnetUri != null ? magnetUri : "",
                existingIndex >= 0 ? (table.Rows[existingIndex].Length > 11 ? table.Rows[existingIndex][11]?.ToString() : "") : DateTime.UtcNow.ToString("o"),
                completedUtc
            };

            if (existingIndex >= 0) {
                table.Rows[existingIndex] = row;
            } else {
                table.Rows.Add(row);
            }

            Torrents.ScheduleSave();
            } catch (Exception) {
                // Swallow state-save failures so they never crash the torrent client
            }
        }

        /// <summary>
        /// Removes this torrent's state from the Torrents database.
        /// </summary>
        public void RemoveState() {
            try {
                if (Metadata == null) return;
                Database.Database db;
                if (!Torrents.Databases.TryGetValue("Torrents", out db)) return;
                Table table;
                if (!db.Tables.TryGetValue("Transfers", out table)) return;

                for (int i = table.Rows.Count - 1; i >= 0; i--) {
                    if (table.Rows[i].Length > 0 && string.Equals(table.Rows[i][0]?.ToString(), Metadata.InfoHash, StringComparison.OrdinalIgnoreCase)) {
                        table.Rows.RemoveAt(i);
                        break;
                    }
                }

                Torrents.ScheduleSave();
            } catch (Exception) {
                // Swallow state-removal failures so they never crash the torrent client
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Torrent metadata (piece hashes, tracker list, etc.).
        /// </summary>
        public TorrentMetadata Metadata { get; }

        /// <summary>
        /// Unique identifier for this client instance.
        /// </summary>
        public string PeerId { get; }

        /// <summary>
        /// Port this client listens on for incoming peer connections.
        /// In registered mode this is the <see cref="MutableTcpServer"/>'s port.
        /// </summary>
        public int ListenPort { get; private set; }

        /// <summary>
        /// Directory where downloaded files are saved.
        /// </summary>
        public string DownloadPath { get; }

        /// <summary>
        /// Total bytes downloaded so far.
        /// </summary>
        public long BytesDownloaded { get; private set; }

        /// <summary>
        /// Total bytes remaining.
        /// </summary>
        public long BytesRemaining {
            get {
                long remaining = Metadata.TotalSize - BytesDownloaded;
                return remaining > 0 ? remaining : 0;
            }
        }

        /// <summary>
        /// True when all pieces have been downloaded and verified.
        /// </summary>
        public bool IsComplete {
            get {
                for (int i = 0; i < _bitfield.Length; i++) {
                    if (!_bitfield[i]) return false;
                }
                return _bitfield.Length > 0;
            }
        }

        /// <summary>
        /// Download progress as a percentage (0–100).
        /// </summary>
        public double Progress {
            get {
                if (Metadata.PieceCount == 0) return 0;
                int have = 0;
                for (int i = 0; i < _bitfield.Length; i++) {
                    if (_bitfield[i]) have++;
                }
                return (double)have / Metadata.PieceCount * 100.0;
            }
        }

        /// <summary>
        /// True when this client is registered on a <see cref="MutableTcpServer"/>
        /// rather than running its own standalone <see cref="TcpServer"/>.
        /// </summary>
        public bool IsRegistered { get { return _mutableServer != null; } }

        /// <summary>
        /// The <see cref="TorrentTracker"/> instance created when this client is
        /// registered on a <see cref="MutableTcpServer"/>. <see langword="null"/>
        /// in standalone mode (use a separate <see cref="TorrentTracker"/> instead).
        /// </summary>
        public TorrentTracker Tracker { get; private set; }

        #endregion

        #region Events

        /// <summary>Fired when a piece is successfully downloaded and verified.</summary>
        public event Action<int> PieceCompleted;

        /// <summary>Fired when the entire torrent download is complete.</summary>
        public event Action DownloadCompleted;

        /// <summary>Fired when a new peer connects.</summary>
        public event Action<string> PeerConnectedEvent;

        /// <summary>Fired when a peer disconnects.</summary>
        public event Action<string> PeerDisconnectedEvent;

        /// <summary>Fired on errors.</summary>
        public event Action<Exception> ErrorOccurred;

        #endregion

        #region Private Fields

        // Piece state
        private readonly bool[] _bitfield;
        private readonly byte[][] _pieces;
        private readonly bool[] _requested;

        // Incoming connections (standalone mode — null when registered)
        private TcpServer _peerServer;

        // MutableTcpServer (registered mode — null when standalone)
        private MutableTcpServer _mutableServer;

        // Tracker connection
        private TcpClient _trackerClient;

        // Connected peer sessions keyed by PeerId
        private readonly ConcurrentDictionary<string, PeerSession> _peers
            = new ConcurrentDictionary<string, PeerSession>();

        private CancellationTokenSource _cts;
        private bool _disposed;
        private readonly object _pieceLock = new object();

        #endregion

        /// <summary>
        /// Initializes a new TorrentClient.
        /// </summary>
        /// <param name="metadata">Torrent metadata describing the file and tracker(s).</param>
        /// <param name="listenPort">TCP port to listen on for peer connections (standalone mode).</param>
        /// <param name="downloadPath">Directory to save downloaded files.</param>
        public TorrentClient(TorrentMetadata metadata, int listenPort = 6881, string downloadPath = "./downloads") {
            Metadata = metadata;
            PeerId = Guid.NewGuid().ToString("N").Substring(0, 20);
            ListenPort = listenPort;
            DownloadPath = downloadPath;

            _bitfield = new bool[metadata.PieceCount];
            _pieces = new byte[metadata.PieceCount][];
            _requested = new bool[metadata.PieceCount];

            // Create the standalone peer server (will be replaced if Register() is called)
            var serverOptions = NetworkOptions.NewDefault();
            serverOptions.Logging = true;
            _peerServer = new TcpServer(serverOptions, listenPort, "TorrentPeerServer");

            // Register incoming message handlers on the peer server
            RegisterPeerCallbacks(_peerServer);

            if (!Directory.Exists(downloadPath))
                Directory.CreateDirectory(downloadPath);

            SaveState("Idle");
        }

        #region MutableTcpServer Registration

        /// <summary>
        /// Registers this torrent client on a <see cref="MutableTcpServer"/>, allowing
        /// it to share a single port with HTTP, WebSocket, and other SocketJack services.
        /// <para>
        /// Torrent peers connect using the SocketJack protocol and their messages are
        /// routed through the server's existing callback pipeline. This also creates
        /// an embedded <see cref="TorrentTracker"/> on the same server, so the
        /// MutableTcpServer acts as tracker + peer server simultaneously.
        /// </para>
        /// </summary>
        /// <param name="server">The <see cref="MutableTcpServer"/> to register on.</param>
        /// <example>
        /// <code>
        /// var server = new MutableTcpServer(8080, "MultiServer");
        /// server.EnabledProtocols.UnionWith(new[] { MutableTcpProtocols.Http, MutableTcpProtocols.SocketJack });
        /// var metadata = TorrentMetadata.FromFile("patch.zip", new List&lt;string&gt; { "localhost:8080" });
        /// var client = new TorrentClient(metadata, downloadPath: "./downloads");
        /// client.Register(server);
        /// client.Seed("patch.zip");
        /// server.Listen();
        /// await client.StartAsync();
        /// </code>
        /// </example>
        public void Register(MutableTcpServer server) {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (_mutableServer != null)
                throw new InvalidOperationException("This TorrentClient is already registered on a MutableTcpServer.");

            _mutableServer = server;
            ListenPort = server.Port;

            // Dispose the standalone peer server — we won't need it
            if (_peerServer != null) {
                _peerServer.Dispose();
                _peerServer = null;
            }

            // Register peer-serving callbacks on the MutableTcpServer.
            // SocketJack-protocol connections will have their deserialized messages
            // dispatched through these callbacks automatically.
            RegisterPeerCallbacks(server);

            // Create an embedded tracker on the same server so it handles
            // announce requests and search queries from torrent peers.
            Tracker = new TorrentTracker(server);

            // Auto-register our own metadata so peers can find this torrent
            if (Metadata != null && Metadata.InfoHash != null) {
                Tracker.RegisterTorrent(Metadata);
            }
        }

        /// <summary>
        /// Registers the peer-serving message callbacks on the given server.
        /// Works for both <see cref="TcpServer"/> (standalone) and
        /// <see cref="MutableTcpServer"/> (registered) since both implement
        /// <see cref="ISocket.RegisterCallback{T}"/>.
        /// </summary>
        private void RegisterPeerCallbacks(ISocket server) {
            server.RegisterCallback<PeerHandshake>(OnIncomingHandshake);
            server.RegisterCallback<PieceRequest>(OnIncomingPieceRequest);
            server.RegisterCallback<HaveMessage>(OnIncomingHave);
            server.RegisterCallback<BitfieldMessage>(OnIncomingBitfield);
            server.RegisterCallback<InterestedMessage>(OnIncomingInterested);
        }

        /// <summary>
        /// Sends an object to the specified connection, routing through whichever
        /// server is active (standalone <see cref="TcpServer"/> or registered
        /// <see cref="MutableTcpServer"/>).
        /// </summary>
        private void SendToConnection(NetworkConnection connection, object obj) {
            if (_mutableServer != null) {
                _mutableServer.Send(connection, obj);
            } else if (_peerServer != null) {
                _peerServer.Send(connection, obj);
            }
        }

        #endregion

        /// <summary>
        /// Load an existing file to seed (marks all pieces as complete).
        /// </summary>
        /// <param name="filePath">Path to the complete file on disk.</param>
        public void Seed(string filePath) {
            if (Metadata == null || Metadata.PieceHashes == null || Metadata.PieceCount == 0) return;
            using (var stream = File.OpenRead(filePath)) {
                byte[] buffer = new byte[Metadata.PieceSize];
                int index = 0;
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0 && index < Metadata.PieceCount) {
                    byte[] piece = new byte[bytesRead];
                    Array.Copy(buffer, piece, bytesRead);

                    // Verify the piece hash matches metadata
                    string hash = ComputeSha256(piece);
                    if (index < Metadata.PieceHashes.Count && hash == Metadata.PieceHashes[index]) {
                        _pieces[index] = piece;
                        _bitfield[index] = true;
                    }
                    index++;
                }
            }
            BytesDownloaded = Metadata.TotalSize;
            SaveState("Seeding");
        }

        /// <summary>
        /// Start the torrent client: listen for peers, announce to tracker, connect to swarm.
        /// <para>In standalone mode, starts the built-in peer server.
        /// In registered mode, the <see cref="MutableTcpServer"/> must already be listening.</para>
        /// </summary>
        public async Task StartAsync() {
            _cts = new CancellationTokenSource();

            // Start our peer server (standalone mode only)
            if (_peerServer != null) {
                _peerServer.Listen();
            }

            SaveState("Connecting");

            // Announce to each tracker and get peer lists
            for (int t = 0; t < Metadata.Trackers.Count; t++) {
                string tracker = Metadata.Trackers[t];
                await AnnounceToTracker(tracker);
            }

            // Start the piece request loop
            _ = Task.Run(() => PieceRequestLoop(_cts.Token));
        }

        /// <summary>
        /// Search for torrents on the connected tracker.
        /// </summary>
        /// <param name="query">Search keywords.</param>
        /// <param name="category">Optional category filter.</param>
        /// <param name="maxResults">Maximum results to return.</param>
        /// <returns>Search results from the tracker.</returns>
        public async Task<TorrentSearchResponse> SearchAsync(string query, string category = "", int maxResults = 25) {
            if (Metadata.Trackers.Count == 0)
                throw new InvalidOperationException("No trackers configured in metadata.");

            string tracker = Metadata.Trackers[0];
            string[] parts = tracker.Split(':');
            string host = parts[0];
            int port = int.Parse(parts[1]);

            var searchClient = new TcpClient("TorrentSearch");
            var tcs = new TaskCompletionSource<TorrentSearchResponse>();

            searchClient.RegisterCallback<TorrentSearchResponse>(e => {
                var response = e.Object;
                tcs.TrySetResult(response);
            });

            searchClient.OnConnected += (e) => {
                searchClient.Send(new TorrentSearchRequest {
                    Query = query,
                    Category = category,
                    MaxResults = maxResults
                });
            };

            bool connected = await searchClient.Connect(host, port);
            if (!connected) {
                searchClient.Dispose();
                throw new InvalidOperationException("Failed to connect to tracker at " + tracker);
            }

            // Wait up to 10 seconds for a response
            var delay = Task.Delay(TimeSpan.FromSeconds(10));
            var completed = await Task.WhenAny(tcs.Task, delay);
            searchClient.Dispose();

            if (completed == delay)
                throw new TimeoutException("Search request timed out.");

            return await tcs.Task;
        }

        /// <summary>
        /// Stop the client, disconnect from all peers, and release resources.
        /// </summary>
        public void Stop() {
            if (_cts != null && !_cts.IsCancellationRequested)
                _cts.Cancel();

            SaveState("Stopped");

            // Announce "stopped" to trackers
            for (int t = 0; t < Metadata.Trackers.Count; t++) {
                try {
                    AnnounceToTracker(Metadata.Trackers[t], "stopped").Wait(TimeSpan.FromSeconds(2));
                } catch {
                    // Best-effort stop announcement
                }
            }

            // Disconnect all peer sessions
            foreach (var kvp in _peers) {
                kvp.Value.Client.Dispose();
                PeerDisconnectedEvent?.Invoke(kvp.Key);
            }
            _peers.Clear();

            if (_trackerClient != null) {
                _trackerClient.Dispose();
                _trackerClient = null;
            }
        }

        #region Tracker Communication

        /// <summary>
        /// Connect to a tracker and send an announce request.
        /// The tracker responds with a peer list which we use to connect to the swarm.
        /// </summary>
        private async Task AnnounceToTracker(string trackerEndpoint, string eventType = "started") {
            string[] parts = trackerEndpoint.Split(':');
            string host = parts[0];
            int port = int.Parse(parts[1]);

            _trackerClient = new TcpClient("TrackerClient");
            _trackerClient.RegisterCallback<AnnounceResponse>(OnAnnounceResponse);

            bool connected = await _trackerClient.Connect(host, port);
            if (!connected) {
                ErrorOccurred?.Invoke(new Exception("Failed to connect to tracker: " + trackerEndpoint));
                return;
            }

            _trackerClient.Send(new AnnounceRequest {
                InfoHash = Metadata.InfoHash,
                PeerId = PeerId,
                ListenPort = ListenPort,
                Downloaded = BytesDownloaded,
                Left = BytesRemaining,
                Event = eventType
            });
        }

        /// <summary>
        /// Process the tracker's announce response — connect to discovered peers.
        /// </summary>
        private void OnAnnounceResponse(ReceivedEventArgs<AnnounceResponse> e) {
            var response = e.Object;
            if (response == null) return;

            for (int i = 0; i < response.Peers.Count; i++) {
                var peer = response.Peers[i];
                if (peer.PeerId == PeerId) continue;
                if (_peers.ContainsKey(peer.PeerId)) continue;

                _ = ConnectToPeerAsync(peer);
            }
        }

        #endregion

        #region Peer Connections

        /// <summary>
        /// Initiate an outgoing connection to a peer.
        /// </summary>
        private async Task ConnectToPeerAsync(PeerEndpoint endpoint) {
            var peerClient = new TcpClient("Peer-" + endpoint.PeerId.Substring(0, 8));

            // Register message handlers for this peer
            peerClient.RegisterCallback<PeerHandshake>(OnPeerHandshake);
            peerClient.RegisterCallback<BitfieldMessage>(OnPeerBitfield);
            peerClient.RegisterCallback<PieceData>(OnPieceData);
            peerClient.RegisterCallback<HaveMessage>(OnPeerHave);
            peerClient.RegisterCallback<ChokeMessage>(OnPeerChoke);

            bool connected = await peerClient.Connect(endpoint.Host, endpoint.Port);
            if (!connected) {
                peerClient.Dispose();
                return;
            }

            var session = new PeerSession {
                PeerId = endpoint.PeerId,
                Client = peerClient,
                Bitfield = new bool[Metadata.PieceCount]
            };
            _peers.TryAdd(endpoint.PeerId, session);
            PeerConnectedEvent?.Invoke(endpoint.PeerId);

            // Send our handshake
            peerClient.Send(new PeerHandshake {
                InfoHash = Metadata.InfoHash,
                PeerId = PeerId
            });

            // Send our bitfield
            var bitfieldMsg = new BitfieldMessage();
            for (int i = 0; i < _bitfield.Length; i++) {
                bitfieldMsg.Bitfield.Add(_bitfield[i]);
            }
            peerClient.Send(bitfieldMsg);
        }

        // -- Incoming message handlers (peer server or MutableTcpServer) --

        private void OnIncomingHandshake(ReceivedEventArgs<PeerHandshake> e) {
            var hs = e.Object;
            if (hs == null) return;
            if (hs.InfoHash != Metadata.InfoHash) {
                // InfoHash mismatch — reject
                e.Connection.Dispose();
                return;
            }

            // Reply with our handshake and bitfield
            SendToConnection(e.Connection, new PeerHandshake {
                InfoHash = Metadata.InfoHash,
                PeerId = PeerId
            });

            var bitfieldMsg = new BitfieldMessage();
            for (int i = 0; i < _bitfield.Length; i++) {
                bitfieldMsg.Bitfield.Add(_bitfield[i]);
            }
            SendToConnection(e.Connection, bitfieldMsg);
        }

        private void OnIncomingPieceRequest(ReceivedEventArgs<PieceRequest> e) {
            var req = e.Object;
            if (req == null) return;

            if (req.PieceIndex >= 0 && req.PieceIndex < Metadata.PieceCount && _bitfield[req.PieceIndex]) {
                byte[] data = _pieces[req.PieceIndex];
                if (data != null) {
                    SendToConnection(e.Connection, new PieceData {
                        PieceIndex = req.PieceIndex,
                        Data = Convert.ToBase64String(data)
                    });
                }
            }
        }

        private void OnIncomingHave(ReceivedEventArgs<HaveMessage> e) {
            // Track that this incoming peer now has a piece (not stored per-connection in this sample)
        }

        private void OnIncomingBitfield(ReceivedEventArgs<BitfieldMessage> e) {
            // Track incoming peer's bitfield (not stored per-connection in this sample)
        }

        private void OnIncomingInterested(ReceivedEventArgs<InterestedMessage> e) {
            // Unchoke interested peers automatically in this sample
            SendToConnection(e.Connection, new ChokeMessage { IsChoked = false });
        }

        // -- Outgoing peer callbacks --

        private void OnPeerHandshake(ReceivedEventArgs<PeerHandshake> e) {
            var hs = e.Object;
            if (hs == null) return;
            if (hs.InfoHash != Metadata.InfoHash) {
                // Mismatch — disconnect
                foreach (var kvp in _peers) {
                    if (ReferenceEquals(kvp.Value.Client, e.sender)) {
                        kvp.Value.Client.Dispose();
                        _peers.TryRemove(kvp.Key, out _);
                        PeerDisconnectedEvent?.Invoke(kvp.Key);
                        break;
                    }
                }
            }
        }

        private void OnPeerBitfield(ReceivedEventArgs<BitfieldMessage> e) {
            // Find the session for this peer and store the bitfield
            foreach (var kvp in _peers) {
                if (ReferenceEquals(kvp.Value.Client, e.sender)) {
                    var bf = e.Object;
                    if (bf != null) {
                        for (int i = 0; i < bf.Bitfield.Count && i < kvp.Value.Bitfield.Length; i++) {
                            kvp.Value.Bitfield[i] = bf.Bitfield[i];
                        }
                    }
                    break;
                }
            }
        }

        private void OnPeerHave(ReceivedEventArgs<HaveMessage> e) {
            var msg = e.Object;
            if (msg == null) return;

            foreach (var kvp in _peers) {
                if (ReferenceEquals(kvp.Value.Client, e.sender)) {
                    if (msg.PieceIndex >= 0 && msg.PieceIndex < kvp.Value.Bitfield.Length)
                        kvp.Value.Bitfield[msg.PieceIndex] = true;
                    break;
                }
            }
        }

        private void OnPeerChoke(ReceivedEventArgs<ChokeMessage> e) {
            var msg = e.Object;
            if (msg == null) return;

            foreach (var kvp in _peers) {
                if (ReferenceEquals(kvp.Value.Client, e.sender)) {
                    kvp.Value.IsChoked = msg.IsChoked;
                    break;
                }
            }
        }

        private void OnPieceData(ReceivedEventArgs<PieceData> e) {
            var data = e.Object;
            if (data == null) return;

            int idx = data.PieceIndex;
            if (idx < 0 || idx >= Metadata.PieceCount) return;
            if (_bitfield[idx]) return; // Already have it

            byte[] pieceBytes = Convert.FromBase64String(data.Data);
            string hash = ComputeSha256(pieceBytes);

            lock (_pieceLock) {
                if (hash == Metadata.PieceHashes[idx]) {
                    _pieces[idx] = pieceBytes;
                    _bitfield[idx] = true;
                    _requested[idx] = false;
                    BytesDownloaded += pieceBytes.Length;

                    PieceCompleted?.Invoke(idx);
                    SaveState();

                    // Broadcast HAVE to all connected peers
                    var haveMsg = new HaveMessage { PieceIndex = idx };
                    foreach (var kvp in _peers) {
                        kvp.Value.Client.Send(haveMsg);
                    }

                    // Check if download is complete
                    if (IsComplete) {
                        AssembleFile();
                        SaveState("Complete");
                        DownloadCompleted?.Invoke();
                    }
                } else {
                    // Hash mismatch — mark as not requested so we try again
                    _requested[idx] = false;
                }
            }
        }

        #endregion

        #region Piece Selection & Download Loop

        /// <summary>
        /// Continuously requests missing pieces from connected peers.
        /// Uses a rarest-first selection strategy.
        /// </summary>
        private async Task PieceRequestLoop(CancellationToken ct) {
            while (!ct.IsCancellationRequested && !IsComplete) {
                int pieceIndex = SelectNextPiece();
                if (pieceIndex < 0) {
                    // No pieces available to request right now
                    await Task.Delay(1000);
                    continue;
                }

                // Find a peer that has this piece and is not choking us
                PeerSession selectedPeer = null;
                foreach (var kvp in _peers) {
                    var session = kvp.Value;
                    if (!session.IsChoked && session.Bitfield.Length > pieceIndex && session.Bitfield[pieceIndex]) {
                        selectedPeer = session;
                        break;
                    }
                }

                if (selectedPeer != null) {
                    _requested[pieceIndex] = true;
                    selectedPeer.Client.Send(new PieceRequest { PieceIndex = pieceIndex });
                }

                await Task.Delay(100);
            }
        }

        /// <summary>
        /// Select the next piece to download using rarest-first strategy.
        /// Rarest-first helps distribute rare pieces across the swarm faster.
        /// </summary>
        private int SelectNextPiece() {
            int rarestIndex = -1;
            int rarestCount = int.MaxValue;

            for (int i = 0; i < Metadata.PieceCount; i++) {
                if (_bitfield[i] || _requested[i]) continue;

                // Count how many peers have this piece
                int availability = 0;
                foreach (var kvp in _peers) {
                    if (kvp.Value.Bitfield.Length > i && kvp.Value.Bitfield[i])
                        availability++;
                }

                // Rarest-first: prefer pieces with lowest availability (but > 0)
                if (availability > 0 && availability < rarestCount) {
                    rarestCount = availability;
                    rarestIndex = i;
                }
            }

            return rarestIndex;
        }

        #endregion

        #region File Assembly

        /// <summary>
        /// Assemble all downloaded pieces into the final file on disk.
        /// </summary>
        private void AssembleFile() {
            string outputPath = Path.Combine(DownloadPath, Metadata.Name);
            using (var stream = File.Create(outputPath)) {
                for (int i = 0; i < Metadata.PieceCount; i++) {
                    if (_pieces[i] != null)
                        stream.Write(_pieces[i], 0, _pieces[i].Length);
                }
            }
        }

        #endregion

        #region Helpers

        private static string ComputeSha256(byte[] data) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        #endregion

        #region IDisposable

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            Stop();
            if (_peerServer != null)
                _peerServer.Dispose();
            if (Tracker != null)
                Tracker.Dispose();
        }

        #endregion

        /// <summary>
        /// Tracks state for a single connected peer.
        /// </summary>
        private class PeerSession {
            public string PeerId { get; set; }
            public TcpClient Client { get; set; }
            public bool[] Bitfield { get; set; }
            public bool IsChoked { get; set; } = true;
        }
    }
}
