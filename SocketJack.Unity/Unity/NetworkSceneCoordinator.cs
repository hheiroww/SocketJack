using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.SceneManagement;
using SocketJack.Multiplayer.Transport;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Server-directed single-scene transitions. Network state is hidden until each peer acknowledges
    /// the configured scene generation. Catalog indices, not arbitrary remote file paths, cross the network.</summary>
    [DefaultExecutionOrder(-11000), RequireComponent(typeof(NetworkManager)), DisallowMultipleComponent]
    public sealed class NetworkSceneCoordinator : MonoBehaviour
    {
        private const ushort LoadMessage = 60008, ReadyMessage = 60009;
        private const int LoadingRoom = int.MinValue;
        public string[] SceneCatalog = Array.Empty<string>();
        [Min(1)] public float ReadyTimeout = 30;
        public bool SpawnPlayersWhenReady = true;
        public event Action<ulong> PeerSceneReady;
        public event Action<string> SceneLoadFailed;
        public bool IsLoading => serverLoad != null;
        private sealed class Waiting { internal int Room; internal double Since; internal bool Sent, Ready, Force; }
        private readonly Dictionary<ulong, Waiting> waiting = new Dictionary<ulong, Waiting>();
        private NetworkManager network;
        private NetworkClient observedClient;
        private uint generation = 1, requestedGeneration;
        private int sceneIndex, requestedIndex;
        private AsyncOperation serverLoad, clientLoad;
        private bool clientPending;
        private void Awake()
        {
            network = GetComponent<NetworkManager>(); DontDestroyOnLoad(network.gameObject);
            network.ListenerStarted += Started; network.PeerConnected += Connected; network.PeerDisconnected += Disconnected;
            network.ClientConnected += ClientConnected; network.SessionStopped += Stopped;
        }
        private void Started() { generation = 1; sceneIndex = CurrentSceneIndex(); network.Server.MessageReceived += ServerMessage; }
        private int CurrentSceneIndex()
        { int index = Array.IndexOf(SceneCatalog, SceneManager.GetActiveScene().name); return index >= 0 ? index : 0; }
        private void Connected(ulong peer)
        {
            if (SceneCatalog.Length == 0) return;
            waiting[peer] = new Waiting { Room = network.Server.GetPeerRoom(peer), Since = Time.unscaledTimeAsDouble, Force = false };
            network.Server.SetPeerRoom(peer, LoadingRoom);
        }
        private void Disconnected(ulong peer, string reason) => waiting.Remove(peer);
        public bool IsPeerReady(ulong peer) => !waiting.ContainsKey(peer);
        public void LoadScene(int catalogIndex)
        {
            if (!network.IsServer) throw new InvalidOperationException("Only the server may change the game scene.");
            if (serverLoad != null) throw new InvalidOperationException("A scene transition is already running.");
            if (catalogIndex < 0 || catalogIndex >= SceneCatalog.Length || string.IsNullOrWhiteSpace(SceneCatalog[catalogIndex])) throw new ArgumentOutOfRangeException(nameof(catalogIndex));
            if (generation == uint.MaxValue) throw new InvalidOperationException("Scene generation exhausted; restart the session.");
            generation++; sceneIndex = catalogIndex;
            foreach (ulong peer in new List<ulong>(network.Server.Peers))
            {
                int room = waiting.TryGetValue(peer, out var prior) ? prior.Room : network.Server.GetPeerRoom(peer);
                waiting[peer] = new Waiting { Room = room, Since = Time.unscaledTimeAsDouble, Force = true };
                network.Server.SetPeerRoom(peer, LoadingRoom);
            }
            foreach (ulong id in new List<ulong>(network.SpawnedObjects.Keys)) network.Despawn(id);
            try { serverLoad = SceneManager.LoadSceneAsync(SceneCatalog[catalogIndex], LoadSceneMode.Single); }
            catch (Exception error) { Fail(error.Message); }
        }
        private void ClientConnected()
        {
            if (observedClient != null) observedClient.MessageReceived -= ClientMessage;
            observedClient = network.Client; observedClient.MessageReceived += ClientMessage;
        }
        private void ClientMessage(NetworkMessage message)
        {
            if (message.MessageId != LoadMessage) return;
            try
            {
                if (message.Payload.Length != 9 || message.Channel != 3 || message.Delivery != DeliveryMode.ReliableUnordered) throw new InvalidDataException("Invalid scene request.");
                using (var reader = new BinaryReader(new MemoryStream(message.Payload, false)))
                {
                    uint next = reader.ReadUInt32(); int index = reader.ReadInt32(); byte force = reader.ReadByte();
                    if (next == 0 || index < 0 || index >= SceneCatalog.Length || force > 1) throw new InvalidDataException("Scene catalog mismatch.");
                    if (requestedGeneration != 0 && unchecked((int)(next - requestedGeneration)) <= 0) return;
                    if (clientLoad != null) throw new InvalidDataException("Overlapping scene transitions.");
                    requestedGeneration = next; requestedIndex = index; clientPending = true;
                    if (!network.IsServer && (force != 0 || SceneManager.GetActiveScene().name != SceneCatalog[index]))
                        clientLoad = SceneManager.LoadSceneAsync(SceneCatalog[index], LoadSceneMode.Single);
                }
            }
            catch (Exception error) { Fail(error.Message); }
        }
        private void ServerMessage(NetworkMessage message)
        {
            if (message.MessageId != ReadyMessage) return;
            if (message.Payload.Length != 8 || message.Channel != 3 || message.Delivery != DeliveryMode.ReliableUnordered)
            { network.Server.Disconnect(message.PeerId, "Invalid scene readiness"); return; }
            using (var reader = new BinaryReader(new MemoryStream(message.Payload, false)))
                if (reader.ReadUInt32() == generation && reader.ReadInt32() == sceneIndex && waiting.TryGetValue(message.PeerId, out var state)) state.Ready = true;
        }
        private void Update()
        {
            if (network == null) return;
            if (serverLoad != null && serverLoad.isDone) { serverLoad = null; network.SpawnSceneObjects(); }
            if (network.IsServer)
            {
                foreach (ulong peer in new List<ulong>(waiting.Keys))
                {
                    if (!waiting.TryGetValue(peer, out var state)) continue;
                    if (Time.unscaledTimeAsDouble - state.Since > ReadyTimeout) { network.Server.Disconnect(peer, "Scene readiness timed out"); waiting.Remove(peer); continue; }
                    if (!state.Sent)
                    {
                        using (var stream = new MemoryStream(9)) using (var writer = new BinaryWriter(stream))
                        { writer.Write(generation); writer.Write(sceneIndex); writer.Write(state.Force); state.Sent = network.Server.SendMessage(peer, LoadMessage, stream.ToArray()); }
                    }
                    if (!state.Ready || serverLoad != null) continue;
                    waiting.Remove(peer); network.Server.SetPeerRoom(peer, state.Room);
                    if (SpawnPlayersWhenReady) network.GetComponent<NetworkPlayerSpawner>()?.SpawnPlayer(peer);
                    PeerSceneReady?.Invoke(peer);
                }
            }
            if (clientPending && network.IsClient && (clientLoad == null || clientLoad.isDone) && (!network.IsServer || serverLoad == null))
            {
                clientLoad = null;
                using (var stream = new MemoryStream(8)) using (var writer = new BinaryWriter(stream))
                { writer.Write(requestedGeneration); writer.Write(requestedIndex); if (network.Client.SendMessage(ReadyMessage, stream.ToArray())) clientPending = false; }
            }
        }
        private void Fail(string message) { SceneLoadFailed?.Invoke(message); Debug.LogError("SocketJack scene load failed: " + message, this); network.Shutdown(); }
        private void Stopped()
        {
            waiting.Clear(); serverLoad = clientLoad = null; clientPending = false; requestedGeneration = 0;
            if (observedClient != null) observedClient.MessageReceived -= ClientMessage; observedClient = null;
        }
        private void OnDestroy()
        {
            if (network == null) return;
            network.ListenerStarted -= Started; network.PeerConnected -= Connected; network.PeerDisconnected -= Disconnected;
            network.ClientConnected -= ClientConnected; network.SessionStopped -= Stopped;
            Stopped();
        }
    }
}
