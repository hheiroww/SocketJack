using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Transport;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SocketJack.Tests.Gameplay
{
    /// <summary>Test-only multi-process native gameplay acceptance. No fixture components ship in the package.</summary>
    public sealed class GameplayProbe : MonoBehaviour
    {
        [Serializable] public sealed class Trace { public string kind; public ulong id; public bool valid; public string scene; public int instance; }
        [Serializable] private sealed class Receipt
        {
            public string result, error, role, transport, runId, unityVersion, runtimeSha256;
            public bool playing, lateJoin;
            public bool balancedSeen, fullSeen;
            public int completedPhase, peers, sceneReadyEvents;
            public List<Trace> lifecycle;
        }
        public GameObject AvatarPrefab;
        private const ushort Control = 61001, Report = 61002;
        private static GameplayProbe current;
        private readonly List<Trace> lifecycle = new List<Trace>();
        private readonly Dictionary<int, HashSet<ulong>> reports = new Dictionary<int, HashSet<ulong>>();
        private readonly HashSet<ulong> sceneReady = new HashSet<ulong>();
        private NetworkManager network;
        private NetworkSceneCoordinator scenes;
        private string role, transport, runId, receiptPath, readyPath, lateReadyPath, runtimeHash, error;
        private bool late, finished, announced, interacted, balancedSeen, fullSeen;
        private int phase, requestedPhase, reportedPhase = -1, peakPeers, readyEvents;
        private ulong targetId, replacementOwner, interactor;
        private double deadline, finishAt;
        private readonly HashSet<ulong> oldIds = new HashSet<ulong>();
        public static void Record(string kind, ulong id, bool valid, int instance = 0)
        {
            if (current == null) return;
            current.lifecycle.Add(new Trace { kind = kind, id = id, valid = valid, scene = SceneManager.GetActiveScene().name, instance = instance });
            if (!valid) current.error = "Lifecycle contract failed: " + kind;
        }
        private static string Arg(string name, string fallback = "")
        { var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : fallback; }
        private void Awake()
        {
            current = this; DontDestroyOnLoad(gameObject); Application.runInBackground = true; Application.targetFrameRate = 120;
            role = Arg("-probe-role"); transport = Arg("-probe-transport", "Udp"); runId = Arg("-probe-run-id");
            receiptPath = Arg("-probe-receipt"); readyPath = Arg("-probe-ready"); lateReadyPath = Arg("-probe-late-ready");
            runtimeHash = Arg("-probe-runtime-hash"); late = Arg("-probe-late") == "true"; deadline = Time.realtimeSinceStartupAsDouble + 75;
        }
        private void Start()
        {
            try
            {
                if (role != "server" && role != "client") throw new ArgumentException("Supply an explicit server/client role.");
                var root = new GameObject("Gameplay network manager"); network = root.AddComponent<NetworkManager>();
                root.AddComponent<NetworkObjectPool>();
                root.AddComponent<SocketJackAdaptiveNetworking>().MaxObjectsPerFrame = 1;
                network.EnableAutomaticPortForwarding = false; network.Address = "127.0.0.1"; network.Port = int.Parse(Arg("-probe-port"));
                network.Transport = (TransportType)Enum.Parse(typeof(TransportType), transport); network.RegisterPrefabs(AvatarPrefab);
                scenes = root.AddComponent<NetworkSceneCoordinator>(); scenes.SceneCatalog = new[] { "FoundationA", "FoundationB" }; scenes.SpawnPlayersWhenReady = false;
                scenes.SceneLoadFailed += reason => error = "Scene load: " + reason;
                if (role == "server")
                {
                    scenes.PeerSceneReady += peer =>
                    {
                        readyEvents++; sceneReady.Add(peer);
                        network.Spawn(1, new Vector3((int)(peer % 3) - 1, 0, 0), Quaternion.identity, peer);
                        if (phase == 3) SendControl(peer, 3);
                    };
                    network.StartServer(); network.Server.MessageReceived += ServerReport;
                    File.WriteAllText(readyPath + ".writing", network.Server.Transport.LocalEndPoint.Port.ToString());
                    File.Move(readyPath + ".writing", readyPath);
                }
                else
                {
                    network.ClientConnected += () => network.Client.MessageReceived += ClientControl;
                    network.ClientDisconnected += reason => { if (!finished) error = "Disconnected: " + reason; };
                    network.StartClient();
                }
            }
            catch (Exception failure) { error = failure.ToString(); }
        }
        private void ServerReport(NetworkMessage message)
        {
            if (message.MessageId != Report) return;
            if (message.Payload.Length != 1 || message.Payload[0] > 6) { error = "Unexpected fixture report."; return; }
            int value = message.Payload[0]; if (!reports.TryGetValue(value, out var peers)) reports.Add(value, peers = new HashSet<ulong>());
            peers.Add(message.PeerId);
        }
        private void ClientControl(NetworkMessage message)
        {
            if (message.MessageId != Control) return;
            if (message.Payload.Length != 25) { error = "Unexpected fixture control."; return; }
            requestedPhase = message.Payload[0]; targetId = BitConverter.ToUInt64(message.Payload, 1); replacementOwner = BitConverter.ToUInt64(message.Payload, 9); interactor = BitConverter.ToUInt64(message.Payload, 17);
            if (requestedPhase == 4) foreach (var trace in lifecycle.Where(t => t.kind == "spawn")) oldIds.Add(trace.id);
            if (requestedPhase == 9) Finish(true);
        }
        private void SendControl(ulong peer, int value)
        {
            var bytes = new byte[25]; bytes[0] = (byte)value; Buffer.BlockCopy(BitConverter.GetBytes(targetId), 0, bytes, 1, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(replacementOwner), 0, bytes, 9, 8); Buffer.BlockCopy(BitConverter.GetBytes(interactor), 0, bytes, 17, 8);
            if (!network.Server.SendMessage(peer, Control, bytes)) error = "Fixture control backpressure.";
        }
        private bool Reported(int value, int count) => reports.TryGetValue(value, out var peers) && peers.Count >= count;
        private void Broadcast(int value) { foreach (ulong peer in network.Server.Peers.ToArray()) SendControl(peer, value); }
        private void Update()
        {
            if (finished) { if (Time.realtimeSinceStartupAsDouble >= finishAt) Application.Quit(string.IsNullOrEmpty(error) ? 0 : 1); return; }
            try
            {
                if (error != null) { Finish(false); return; }
                if (Time.realtimeSinceStartupAsDouble >= deadline) { error = "Gameplay acceptance deadline at phase " + phase + "/" + requestedPhase; Finish(false); return; }
                if (network == null) return;
                foreach (var identity in network.SpawnedObjects.Values)
                {
                    var adaptive = identity.GetComponent<SocketJackAdaptiveObject>(); if (adaptive == null) continue;
                    var observedProfile = adaptive.CurrentProfile;
                    // Observers infer precision from the actual tagged state; policy is sent only to owners.
                    if (network.Client != null && network.Client.Objects.TryGetValue(identity.ObjectId, out var remote))
                        observedProfile = NetworkPrecisionFamily.ForState(remote.State, true, observedProfile, true);
                    balancedSeen |= observedProfile.Preset == NetworkPrecisionPreset.Balanced;
                    fullSeen |= observedProfile.Preset == NetworkPrecisionPreset.FullPrecision;
                }
                if (network.IsServer && network.GetComponent<SocketJackAdaptiveNetworking>().ObjectsProcessedLastFrame > 1)
                    throw new InvalidOperationException("Adaptive work exceeded the configured one-object frame budget.");
                if (role == "server") ServerTick(); else ClientTick();
            }
            catch (Exception failure) { error = failure.ToString(); Finish(false); }
        }
        private void ServerTick()
        {
            peakPeers = Math.Max(peakPeers, network.Server.Peers.Count());
            if (phase == 0 && Reported(0, 2) && sceneReady.Count == 2)
            {
                var actors = network.SpawnedObjects.Values.Where(i => i.GetComponent<SocketJackHealth>() != null).OrderBy(i => i.ObjectId).ToArray();
                if (actors.Length != 2) throw new InvalidOperationException("Initial actor count differs.");
                targetId = actors[0].ObjectId; replacementOwner = actors[1].OwnerId; interactor = actors[0].OwnerId;
                actors[0].GetComponent<SocketJackHealth>().Damage(25);
                var animator = actors[0].GetComponent<Animator>(); animator.SetFloat("Speed", .75f); animator.SetInteger("Phase", 2); animator.SetBool("Open", true);
                if (!actors[0].GetComponent<SocketJackAnimator>().SetTrigger("Pulse")) throw new InvalidOperationException("Server Animator trigger was rejected.");
                phase = 1; Broadcast(1);
            }
            else if (phase == 1 && Reported(1, 2))
            { network.TransferOwnership(targetId, replacementOwner, false); phase = 2; Broadcast(2); }
            else if (phase == 2 && Reported(2, 2))
            { phase = 3; File.WriteAllText(lateReadyPath, "ready"); }
            else if (phase == 3 && Reported(3, 1) && sceneReady.Count == 3)
            {
                foreach (ulong id in network.SpawnedObjects.Keys) oldIds.Add(id);
                phase = 4; Broadcast(4); sceneReady.Clear(); scenes.LoadScene(1);
            }
            else if (phase == 4 && Reported(4, 3) && sceneReady.Count == 3)
            {
                if (network.SpawnedObjects.Count != 4 || network.SpawnedObjects.Keys.Any(oldIds.Contains)) throw new InvalidOperationException("Old scene identities survived transition.");
                if (!lifecycle.Any(t => t.kind == "ownership") || oldIds.Any(id => !lifecycle.Any(t => t.kind == "despawn" && t.id == id))) throw new InvalidOperationException("Server ownership/despawn callbacks missing.");
                var actor = network.SpawnedObjects.Values.First(i => i.GetComponent<SocketJackHealth>() != null);
                ulong oldId = actor.ObjectId, owner = actor.OwnerId; int instance = actor.gameObject.GetInstanceID();
                actor.GetComponent<SocketJackHealth>().Damage(99); actor.GetComponent<Animator>().SetFloat("Speed", 99);
                network.Despawn(oldId);
                if (actor.Manager != null || actor.ObjectId != 0 || actor.IsNetworkReady) throw new InvalidOperationException("Pooled identity was not unbound.");
                var reused = network.Spawn(1, Vector3.zero, Quaternion.identity, owner);
                if (reused.ObjectId == oldId || reused.gameObject.GetInstanceID() != instance || reused.GetComponent<SocketJackHealth>().Current != 100 || reused.GetComponent<GameplayLifecycle>().ResetCount < 2 || reused.GetComponent<Animator>().GetFloat("Speed") != 0)
                    throw new InvalidOperationException("Pool did not reuse/reset the instance with a fresh identity.");
                targetId = reused.ObjectId; replacementOwner = oldId; phase = 5; Broadcast(5);
            }
            else if (phase == 5 && Reported(5, 3) && network.TryGetIdentity(targetId, out var selected) && selected.GetComponent<SocketJackAdaptiveObject>().CurrentProfile.Preset == NetworkPrecisionPreset.FullPrecision)
            { phase = 6; Broadcast(6); }
            else if (phase == 6 && Reported(6, 3)) { phase = 9; Broadcast(9); Finish(true); }
        }
        private SocketJackDoor Door() => network.SpawnedObjects.Values.Select(i => i.GetComponent<SocketJackDoor>()).FirstOrDefault(i => i != null);
        private bool InitialReady()
        {
            if (!network.IsClient) return false;
            var owned = network.SpawnedObjects.Values.FirstOrDefault(i => i.IsOwner && i.GetComponent<SocketJackHealth>() != null);
            var door = Door(); return owned != null && owned.IsNetworkReady && door != null && door.IsNetworkReady && network.SpawnedObjects.Values.All(i => i.IsNetworkReady);
        }
        private void ReportPhase(int value)
        { if (reportedPhase == value) return; if (network.Client.SendMessage(Report, new[] { (byte)value })) { reportedPhase = value; phase = value; } }
        private void ClientTick()
        {
            if (!network.IsClient) return;
            if (!late && !announced && InitialReady()) { ReportPhase(0); announced = true; }
            if (requestedPhase == 1)
            {
                var door = Door();
                if (!interacted && network.LocalPeerId == interactor && door != null && door.IsNetworkReady) { door.Interact(); interacted = true; }
                if (!network.TryGetIdentity(targetId, out var actor) || !actor.IsNetworkReady || door == null || !door.IsOpen) return;
                if (Math.Abs(actor.GetComponent<SocketJackHealth>().Current - 75) > .001) return;
                var animator = actor.GetComponent<Animator>();
                if (Math.Abs(animator.GetFloat("Speed") - .75f) > .001 || animator.GetInteger("Phase") != 2 || !animator.GetBool("Open") || !actor.GetComponent<GameplayLifecycle>().TriggerSeen) return;
                ReportPhase(1);
            }
            else if (requestedPhase == 2)
            {
                if (!network.TryGetIdentity(targetId, out var actor) || actor.OwnerId != replacementOwner || actor.ServerAuthority || actor.GetComponent<GameplayLifecycle>().OwnershipCount != 1) return;
                if (actor.HasStateAuthority != (network.LocalPeerId == replacementOwner)) throw new InvalidOperationException("State authority differs after ownership transfer.");
                ReportPhase(2);
            }
            else if (requestedPhase == 3 && late)
            {
                if (!InitialReady() || !network.TryGetIdentity(targetId, out var actor) || !actor.IsNetworkReady || Door() == null || !Door().IsOpen) return;
                var life = actor.GetComponent<GameplayLifecycle>(); var animator = actor.GetComponent<Animator>();
                if (life.ReadyHealth != 75 || actor.GetComponent<SocketJackHealth>().Current != 75 || Math.Abs(animator.GetFloat("Speed") - .75f) > .001 || animator.GetInteger("Phase") != 2 || !animator.GetBool("Open")) return;
                if (life.TriggerSeen) throw new InvalidOperationException("A transient Animator trigger was replayed to a late joiner.");
                ReportPhase(3);
            }
            else if (requestedPhase == 4)
            {
                if (SceneManager.GetActiveScene().name != "FoundationB" || !InitialReady() || network.SpawnedObjects.Count != 4 || network.SpawnedObjects.Keys.Any(oldIds.Contains)) return;
                if (oldIds.Any(id => !lifecycle.Any(t => t.kind == "despawn" && t.id == id))) throw new InvalidOperationException("Client despawn callbacks missing during scene transition.");
                ReportPhase(4);
            }
            else if (requestedPhase == 5)
            {
                if (!network.TryGetIdentity(targetId, out var actor) || !actor.IsNetworkReady) return;
                var life = actor.GetComponent<GameplayLifecycle>();
                var previous = lifecycle.LastOrDefault(t => t.kind == "spawn" && t.id == replacementOwner);
                if (previous == null || previous.instance != actor.gameObject.GetInstanceID() || life.ResetCount < 2 || life.ReadyHealth != 100 || actor.GetComponent<SocketJackHealth>().Current != 100 || actor.GetComponent<Animator>().GetFloat("Speed") != 0)
                    throw new InvalidOperationException("Client pool reset/reuse/baseline differs.");
                ReportPhase(5);
            }
            else if (requestedPhase == 6)
            {
                if (!network.TryGetIdentity(targetId, out var actor) || !actor.IsNetworkReady || (actor.IsOwner && actor.GetComponent<SocketJackAdaptiveObject>().CurrentProfile.Preset != NetworkPrecisionPreset.FullPrecision) || network.Client.Objects[targetId].State.Length != 40) return;
                if (actor.GetComponent<GameplayLifecycle>().AdaptiveScore != .123456f) return;
                if (!balancedSeen || !fullSeen) throw new InvalidOperationException("The native peer did not observe the adaptive profile transition.");
                ReportPhase(6);
            }
        }
        private void Finish(bool passed)
        {
            if (finished) return; finished = true;
            if (passed && lifecycle.Any(t => !t.valid)) { passed = false; error = "Invalid lifecycle record."; }
            var receipt = new Receipt { result = passed ? "PASS" : "FAIL", error = error, role = role, transport = transport, runId = runId, unityVersion = Application.unityVersion,
                runtimeSha256 = runtimeHash, playing = Application.isPlaying, lateJoin = late, balancedSeen = balancedSeen, fullSeen = fullSeen, completedPhase = phase, peers = peakPeers, sceneReadyEvents = readyEvents, lifecycle = lifecycle };
            File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true));
            Debug.Log("SOCKETJACK_GAMEPLAY_" + receipt.result + " " + role + " phase=" + phase + (error == null ? "" : " " + error));
            finishAt = Time.realtimeSinceStartupAsDouble + 1;
        }
        private void OnDestroy() { if (current == this) current = null; network?.Shutdown(); }
    }
}
