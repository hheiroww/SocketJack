using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SocketJack.Net.Torrent {

    /// <summary>
    /// A torrent tracker built on SocketJack's <see cref="TcpServer"/>.
    /// <para>
    /// Peers connect and send an <see cref="AnnounceRequest"/>; the tracker replies
    /// with an <see cref="AnnounceResponse"/> containing the current peer swarm.
    /// Also supports keyword-based <see cref="TorrentSearchRequest"/> lookups
    /// against registered torrent metadata.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <b>Standalone mode:</b>
    /// <code>
    /// var tracker = new TorrentTracker(port: 6969);
    /// tracker.Start();
    /// tracker.RegisterTorrent(metadata, category: "software");
    /// </code>
    /// <b>MutableTcpServer mode:</b>
    /// <code>
    /// var server = new MutableTcpServer(8080, "MultiServer");
    /// server.EnabledProtocols.UnionWith(new[] { MutableTcpProtocols.Http, MutableTcpProtocols.SocketJack });
    /// var tracker = new TorrentTracker(server);
    /// tracker.RegisterTorrent(metadata, category: "software");
    /// server.Listen();
    /// </code>
    /// </remarks>
    public class TorrentTracker : IDisposable {

        private readonly TcpServer _standaloneServer;
        private readonly MutableTcpServer _mutableServer;
        private bool _disposed;

        /// <summary>
        /// Swarm state keyed by InfoHash.
        /// Each entry maps PeerId ? endpoint + stats.
        /// </summary>
        internal readonly ConcurrentDictionary<string, ConcurrentDictionary<string, TrackerPeerEntry>> Swarms
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, TrackerPeerEntry>>();

        /// <summary>
        /// Searchable index of registered torrents keyed by InfoHash.
        /// </summary>
        internal readonly ConcurrentDictionary<string, TorrentIndexEntry> Index
            = new ConcurrentDictionary<string, TorrentIndexEntry>();

        /// <summary>
        /// Default re-announce interval in seconds sent to clients.
        /// </summary>
        public int AnnounceInterval { get; set; } = 120;

        /// <summary>
        /// True when this tracker is registered on a <see cref="MutableTcpServer"/>
        /// rather than running its own standalone <see cref="TcpServer"/>.
        /// </summary>
        public bool IsRegistered { get { return _mutableServer != null; } }

        /// <summary>
        /// Initializes a standalone tracker on the given port.
        /// </summary>
        /// <param name="port">TCP port to listen on (default 6969).</param>
        /// <param name="name">Server name for logging.</param>
        public TorrentTracker(int port = 6969, string name = "TorrentTracker") {
            var options = NetworkOptions.NewDefault();
            options.Logging = true;
            _standaloneServer = new TcpServer(options, port, name);

            RegisterCallbacks(_standaloneServer);
            _standaloneServer.ClientDisconnected += OnClientDisconnected;
        }

        /// <summary>
        /// Initializes a tracker that is hosted on a <see cref="MutableTcpServer"/>.
        /// Torrent clients connect using SocketJack protocol and their messages are
        /// routed through the server's existing SocketJack pipeline.
        /// </summary>
        /// <param name="server">The MutableTcpServer to register on.</param>
        public TorrentTracker(MutableTcpServer server) {
            if (server == null) throw new ArgumentNullException(nameof(server));
            _mutableServer = server;

            RegisterCallbacks(server);
            server.ClientDisconnected += OnClientDisconnected;
        }

        /// <summary>
        /// Start listening for incoming peer connections (standalone mode only).
        /// When hosted on a <see cref="MutableTcpServer"/>, call <c>Listen()</c>
        /// on the server instead.
        /// </summary>
        /// <returns>True if listening started successfully.</returns>
        public bool Start() {
            if (IsRegistered)
                throw new InvalidOperationException(
                    "This tracker is registered on a MutableTcpServer. Call Listen() on the server instead.");
            return _standaloneServer.Listen();
        }

        /// <summary>
        /// Register torrent metadata so it appears in search results.
        /// </summary>
        /// <param name="metadata">Torrent metadata.</param>
        /// <param name="category">Optional category tag for filtering.</param>
        public void RegisterTorrent(TorrentMetadata metadata, string category = "") {
            var entry = new TorrentIndexEntry {
                Metadata = metadata,
                Category = category
            };
            Index.TryAdd(metadata.InfoHash, entry);
        }

        /// <summary>
        /// Registers announce and search callbacks on the given server.
        /// </summary>
        private void RegisterCallbacks(ISocket server) {
            server.RegisterCallback<AnnounceRequest>(OnAnnounce);
            server.RegisterCallback<TorrentSearchRequest>(OnSearch);
        }

        /// <summary>
        /// Sends an object to the specified connection, routing through whichever
        /// server is active (standalone or mutable).
        /// </summary>
        private void SendToConnection(NetworkConnection connection, object obj) {
            if (_mutableServer != null) {
                _mutableServer.Send(connection, obj);
            } else {
                _standaloneServer.Send(connection, obj);
            }
        }

        /// <summary>
        /// Handle an announce request from a peer.
        /// Updates the swarm and replies with the current peer list.
        /// </summary>
        internal void OnAnnounce(ReceivedEventArgs<AnnounceRequest> e) {
            var req = e.Object;
            if (req == null) return;

            var swarm = Swarms.GetOrAdd(req.InfoHash, _ => new ConcurrentDictionary<string, TrackerPeerEntry>());

            if (req.Event == "stopped") {
                // Peer is leaving the swarm
                swarm.TryRemove(req.PeerId, out _);
            } else {
                // Add or update the peer
                var peerEntry = new TrackerPeerEntry {
                    PeerId = req.PeerId,
                    Host = e.Connection.EndPoint != null ? e.Connection.EndPoint.Address.ToString() : "0.0.0.0",
                    Port = req.ListenPort,
                    Downloaded = req.Downloaded,
                    Left = req.Left,
                    LastSeen = DateTime.UtcNow,
                    ConnectionId = e.Connection.ID
                };
                swarm.AddOrUpdate(req.PeerId, peerEntry, (key, existing) => peerEntry);
            }

            // Build response with up to 50 peers (excluding the requester)
            var response = new AnnounceResponse { Interval = AnnounceInterval };
            foreach (var kvp in swarm) {
                if (kvp.Key == req.PeerId) continue;
                if (response.Peers.Count >= 50) break;
                response.Peers.Add(new PeerEndpoint {
                    PeerId = kvp.Value.PeerId,
                    Host = kvp.Value.Host,
                    Port = kvp.Value.Port
                });
            }

            // Update seed/peer counts in the index
            if (Index.TryGetValue(req.InfoHash, out var indexEntry)) {
                int seeds = 0;
                int peers = 0;
                foreach (var kvp in swarm) {
                    if (kvp.Value.Left == 0) seeds++; else peers++;
                }
                indexEntry.SeedCount = seeds;
                indexEntry.PeerCount = peers;
            }

            SendToConnection(e.Connection, response);
        }

        /// <summary>
        /// Handle a keyword search request.
        /// Matches against torrent names using case-insensitive contains.
        /// </summary>
        internal void OnSearch(ReceivedEventArgs<TorrentSearchRequest> e) {
            var req = e.Object;
            if (req == null) return;

            var response = new TorrentSearchResponse();
            string query = (req.Query != null) ? req.Query.ToLowerInvariant() : "";
            string category = (req.Category != null) ? req.Category.ToLowerInvariant() : "";

            foreach (var kvp in Index) {
                if (response.Results.Count >= req.MaxResults) break;

                var entry = kvp.Value;
                string name = (entry.Metadata.Name != null) ? entry.Metadata.Name.ToLowerInvariant() : "";
                string cat = (entry.Category != null) ? entry.Category.ToLowerInvariant() : "";

                // Filter by category if specified
                if (category.Length > 0 && cat != category) continue;

                // Match query against name
                if (query.Length > 0 && !name.Contains(query)) continue;

                response.Results.Add(new TorrentSearchResult {
                    Name = entry.Metadata.Name,
                    InfoHash = entry.Metadata.InfoHash,
                    TotalSize = entry.Metadata.TotalSize,
                    SeedCount = entry.SeedCount,
                    PeerCount = entry.PeerCount,
                    Category = entry.Category,
                    CreatedUtc = entry.Metadata.CreatedUtc
                });
            }

            SendToConnection(e.Connection, response);
        }

        /// <summary>
        /// Remove disconnected peers from all swarms.
        /// </summary>
        private void OnClientDisconnected(DisconnectedEventArgs e) {
            foreach (var swarm in Swarms.Values) {
                // Find entries that belong to the disconnected connection
                var toRemove = new List<string>();
                foreach (var kvp in swarm) {
                    if (kvp.Value.ConnectionId == e.Connection.ID)
                        toRemove.Add(kvp.Key);
                }
                for (int i = 0; i < toRemove.Count; i++) {
                    swarm.TryRemove(toRemove[i], out _);
                }
            }
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            if (_standaloneServer != null)
                _standaloneServer.Dispose();
        }

        // -----------------------------------------------------------------------
        //  Internal types
        // -----------------------------------------------------------------------

        internal class TrackerPeerEntry {
            public string PeerId { get; set; }
            public string Host { get; set; }
            public int Port { get; set; }
            public long Downloaded { get; set; }
            public long Left { get; set; }
            public DateTime LastSeen { get; set; }
            public Guid ConnectionId { get; set; }
        }

        internal class TorrentIndexEntry {
            public TorrentMetadata Metadata { get; set; }
            public string Category { get; set; }
            public int SeedCount { get; set; }
            public int PeerCount { get; set; }
        }
    }
}
