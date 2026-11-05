using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Opt-in adaptation. Samples peers once a second and spreads object work over frames.
    /// Only an object's owner needs a policy message; observers decode the declared tagged family.</summary>
    [RequireComponent(typeof(NetworkManager)), DisallowMultipleComponent]
    public sealed class SocketJackAdaptiveNetworking : MonoBehaviour
    {
        private const ushort PolicyMessage = 60016;
        [Range(1, 4096)] public int MaxObjectsPerFrame = 128;
        public int ObjectsProcessedLastFrame { get; private set; }
        private sealed class Entry
        {
            internal NetworkIdentity Identity;
            internal SocketJackAdaptiveObject Adaptive;
            internal LinkedListNode<ulong> Node;
            internal ulong Owner, View;
            internal uint Epoch, Revision;
            internal NetworkPrecisionPreset Sent;
            internal bool HasSent;
        }
        private NetworkManager network;
        private NetworkClient observedClient;
        private readonly Dictionary<ulong, AdaptiveNetworkPolicy> policies = new Dictionary<ulong, AdaptiveNetworkPolicy>();
        private readonly Dictionary<ulong, AdaptiveNetworkDecision> decisions = new Dictionary<ulong, AdaptiveNetworkDecision>();
        private readonly Dictionary<ulong, Entry> objects = new Dictionary<ulong, Entry>();
        private readonly LinkedList<ulong> schedule = new LinkedList<ulong>();
        private LinkedListNode<ulong> cursor;
        private NetworkPrecisionPreset serverOwnedPrecision = NetworkPrecisionPreset.Balanced;
        private double nextUpdate, lastUpdate;
        private void Awake()
        {
            network = GetComponent<NetworkManager>(); network.ClientConnected += Connected; network.SessionStopped += Stopped;
            network.PeerDisconnected += Disconnected; network.ObjectSpawned += Spawned; network.ObjectDespawned += Despawned;
            foreach (var pair in network.SpawnedObjects) Spawned(pair.Value);
        }
        private void Connected() { if (observedClient != null) observedClient.MessageReceived -= Message; observedClient = network.Client; observedClient.MessageReceived += Message; }
        private void Stopped()
        {
            policies.Clear(); decisions.Clear(); objects.Clear(); schedule.Clear(); cursor = null; nextUpdate = lastUpdate = 0;
            serverOwnedPrecision = NetworkPrecisionPreset.Balanced;
            if (observedClient != null) observedClient.MessageReceived -= Message; observedClient = null;
        }
        private void Disconnected(ulong peer, string reason) { policies.Remove(peer); decisions.Remove(peer); }
        private void Spawned(NetworkIdentity identity)
        {
            if (!network.IsServer || identity == null || objects.ContainsKey(identity.ObjectId)) return;
            var adaptive = identity.GetComponent<SocketJackAdaptiveObject>(); if (adaptive == null) return;
            objects.Add(identity.ObjectId, new Entry { Identity = identity, Adaptive = adaptive, Node = schedule.AddLast(identity.ObjectId) });
        }
        private void Despawned(ulong id)
        {
            if (!objects.TryGetValue(id, out var entry)) return;
            if (cursor == entry.Node) cursor = entry.Node.Next;
            schedule.Remove(entry.Node); objects.Remove(id);
        }
        private void SamplePolicies(double now)
        {
            double elapsed = lastUpdate == 0 ? 1 : now - lastUpdate; lastUpdate = now; nextUpdate = now + 1;
            var metrics = TransportMetrics.Source(network.Server.Transport)?.GetTransportMetrics(Math.Min(4096, network.Server.Peers.Count));
            var native = metrics?.Peers.ToDictionary(peer => peer.PeerId);
            var latency = network.Latency?.GetServerSamples(4096).ToDictionary(peer => peer.PeerId);
            serverOwnedPrecision = NetworkPrecisionPreset.FullPrecision;
            foreach (ulong peer in network.Server.Peers)
            {
                if (!policies.TryGetValue(peer, out var policy)) policies.Add(peer, policy = new AdaptiveNetworkPolicy());
                double? rtt = null; long queued = network.Server.GetQueuedBytes(peer);
                if (native != null && native.TryGetValue(peer, out var detail)) { rtt = detail.RoundTripMilliseconds; queued += detail.QueuedBytes; }
                if (!rtt.HasValue && latency != null && latency.TryGetValue(peer, out var game)) rtt = game.RttMilliseconds;
                var decision = policy.Update(elapsed, rtt, queued); decisions[peer] = decision;
                network.Server.SetPeerReplication(peer, decision.SnapshotEvery);
                if (decision.Precision > serverOwnedPrecision) serverOwnedPrecision = decision.Precision;
            }
        }
        private void Update()
        {
            ObjectsProcessedLastFrame = 0;
            if (!network.IsServer) return;
            if (Time.unscaledTimeAsDouble >= nextUpdate) SamplePolicies(Time.unscaledTimeAsDouble);
            int count = Math.Min(schedule.Count, Mathf.Clamp(MaxObjectsPerFrame, 1, 4096));
            for (int i = 0; i < count; i++)
            {
                var node = cursor ?? schedule.First; if (node == null) break;
                ObjectsProcessedLastFrame++;
                cursor = node.Next;
                if (!objects.TryGetValue(node.Value, out var entry)) continue;
                var identity = entry.Identity; var adaptive = entry.Adaptive;
                if (identity == null || adaptive == null) { Despawned(node.Value); continue; }
                if (!adaptive.isActiveAndEnabled) continue;
                var precision = identity.OwnerId == 0 ? serverOwnedPrecision : decisions.TryGetValue(identity.OwnerId, out var owner) ? owner.Precision : NetworkPrecisionPreset.Balanced;
                adaptive.Select(precision);
                if (identity.OwnerId == 0) continue;
                ulong view = network.Server.GetViewId(identity.OwnerId, identity.ObjectId); if (view == 0) continue;
                var selected = adaptive.CurrentProfile.Preset;
                if (entry.HasSent && entry.Owner == identity.OwnerId && entry.Epoch == identity.AuthorityEpoch && entry.View == view && entry.Sent == selected) continue;
                uint revision = ++entry.Revision; if (revision == 0) revision = ++entry.Revision;
                using (var stream = new MemoryStream(25)) using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(identity.ObjectId); writer.Write(identity.AuthorityEpoch); writer.Write(view); writer.Write(revision); writer.Write((byte)selected);
                    if (!network.Server.SendMessage(identity.OwnerId, PolicyMessage, stream.ToArray())) continue;
                }
                entry.Owner = identity.OwnerId; entry.Epoch = identity.AuthorityEpoch; entry.View = view; entry.Sent = selected; entry.HasSent = true;
            }
        }
        private void Message(NetworkMessage message)
        {
            if (message.MessageId != PolicyMessage || message.Payload.Length != 25 || message.Channel != 3 || message.Delivery != DeliveryMode.ReliableUnordered) return;
            using (var reader = new BinaryReader(new MemoryStream(message.Payload, false)))
            {
                ulong id = reader.ReadUInt64(); uint epoch = reader.ReadUInt32(); ulong view = reader.ReadUInt64(); uint revision = reader.ReadUInt32(); byte preset = reader.ReadByte();
                if (preset > 2 || revision == 0 || !network.Client.Objects.TryGetValue(id, out var state) || state.AuthorityEpoch != epoch || state.ViewId != view || state.OwnerId != network.LocalPeerId || !network.TryGetIdentity(id, out var identity)) return;
                identity.GetComponent<SocketJackAdaptiveObject>()?.SelectPolicy(epoch, view, revision, (NetworkPrecisionPreset)preset);
            }
        }
        private void OnDestroy()
        {
            if (network != null) { network.ClientConnected -= Connected; network.SessionStopped -= Stopped; network.PeerDisconnected -= Disconnected; network.ObjectSpawned -= Spawned; network.ObjectDespawned -= Despawned; }
            Stopped();
        }
    }
}
