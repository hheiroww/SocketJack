using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
using UnityEngine;

namespace SocketJack.Tests.Physics2D
{
    /// <summary>Native, real-time two-client Rigidbody2D fixture. This source is tooling, never a runtime bootstrap.</summary>
    public sealed class Physics2DAcceptance : MonoBehaviour
    {
        [Serializable] public sealed class ObjectResult
        {
            public string id, ownerId, movement;
            public bool owned;
            public long inputsSent, inputsReceived, keyframes, ownerStates, jumps, hardCorrections;
            public int collisions, stateBytes;
            public float minX, maxX, minY, maxY, peakError, peakVelocityRepair, peakVisualStep;
        }
        [Serializable] public sealed class Receipt
        {
            public string runId, result, error, role, transport, authority, unityVersion, runtimeSha256;
            public int port, spawned, active;
            public bool playing;
            public float seconds;
            public ObjectResult[] objects;
        }
        [Serializable] private sealed class Ready { public int port; public string runId; }
        private readonly Dictionary<ulong, ObjectResult> results = new Dictionary<ulong, ObjectResult>();
        private readonly Dictionary<ulong, BodyProbe> bodies = new Dictionary<ulong, BodyProbe>();
        private readonly List<ulong> peers = new List<ulong>();
        private NetworkManager manager;
        private string role, authority, receiptPath, readyPath, runId;
        private float createdAt, runningAt = -1, duration, nextJump;
        private bool finished;
        private int spawned;

        private void OnEnable() => Application.logMessageReceived += OnLog;
        private void OnDisable() => Application.logMessageReceived -= OnLog;
        private void OnLog(string message, string trace, LogType type)
        { if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert) Finish(new InvalidOperationException(message + "\n" + trace)); }
        private void Start()
        {
            try
            {
                Require(Application.isPlaying, "Fixture requires a real native player or Editor Play Mode.");
                Application.runInBackground = true; Application.targetFrameRate = 120; QualitySettings.vSyncCount = 0;
                Time.fixedDeltaTime = .02f;
                role = Argument("-probe-role", "client"); authority = Argument("-probe-authority", "server");
                receiptPath = Argument("-probe-receipt", ""); readyPath = Argument("-probe-ready", ""); runId = Argument("-probe-run-id", "");
                duration = float.Parse(Argument("-probe-duration", "20"), System.Globalization.CultureInfo.InvariantCulture);
                Require(role == "server" || role == "client", "Invalid fixture role.");
                Require(authority == "server" || authority == "owner", "Invalid fixture authority.");
                BuildWorld();
                var go = new GameObject("Physics2D network manager"); manager = go.AddComponent<NetworkManager>();
                manager.EnableAutomaticPortForwarding = false; manager.Address = "127.0.0.1";
                manager.Transport = (TransportType)Enum.Parse(typeof(TransportType), Argument("-probe-transport", "Udp"));
                manager.Port = int.Parse(Argument("-probe-port", "0")); manager.ServerAuthority = authority == "server";
                manager.RegisterPrefabs(Resources.Load<GameObject>("Physics2DPlatformer"), Resources.Load<GameObject>("Physics2DTopDown"));
                manager.ObjectSpawned += Track;
                if (role == "server")
                {
                    manager.PeerConnected += peer =>
                    {
                        peers.Add(peer);
                        if (peers.Count != 2) return;
                        for (int i = 0; i < peers.Count; i++)
                        {
                            manager.Spawn(1, new Vector3(-2 + i * 4, .55f, 0), Quaternion.identity, peers[i]);
                            manager.Spawn(2, new Vector3(-2 + i * 4, 12, 0), Quaternion.identity, peers[i]);
                        }
                    };
                    manager.StartServer();
                    if (!string.IsNullOrEmpty(readyPath)) File.WriteAllText(readyPath, JsonUtility.ToJson(new Ready { port = manager.Server.Transport.LocalEndPoint.Port, runId = runId }));
                }
                else manager.StartClient();
                createdAt = Time.realtimeSinceStartup;
            }
            catch (Exception error) { Finish(error); }
        }
        private static void BuildWorld()
        {
            Wall("Platform floor", new Vector2(0, -.5f), new Vector2(18, 1));
            Wall("Platform left wall", new Vector2(-8.5f, 1.5f), new Vector2(1, 4));
            Wall("Platform right wall", new Vector2(8.5f, 1.5f), new Vector2(1, 4));
            Wall("Top-down bottom", new Vector2(0, 7.5f), new Vector2(18, 1));
            Wall("Top-down top", new Vector2(0, 16.5f), new Vector2(18, 1));
            Wall("Top-down left", new Vector2(-8.5f, 12), new Vector2(1, 8));
            Wall("Top-down right", new Vector2(8.5f, 12), new Vector2(1, 8));
        }
        private static void Wall(string name, Vector2 position, Vector2 size)
        { var go = new GameObject(name); go.transform.position = position; go.AddComponent<BoxCollider2D>().size = size; }
        private void Track(NetworkIdentity identity)
        {
            var motor = identity.GetComponent<SocketJackObject2D>(); Require(motor != null, "Spawn missed 2D motor.");
            Require(motor.PrecisionPreset == NetworkPrecisionPreset.Compact, "Serialized Compact profile was not retained.");
            var body = identity.GetComponent<Rigidbody2D>();
            Require(body.mass == 2.3f && body.constraints == RigidbodyConstraints2D.FreezeRotation, "Network controller changed native body configuration.");
            Require(body.gravityScale == (motor.Movement == NetworkMovement2D.Platformer ? 1 : 0), "Network controller changed native gravityScale.");
            var probe = identity.gameObject.AddComponent<BodyProbe>();
            var result = new ObjectResult { id = identity.ObjectId.ToString(), ownerId = identity.OwnerId.ToString(), movement = motor.Movement.ToString(),
                owned = identity.IsOwner, minX = body.position.x, maxX = body.position.x, minY = body.position.y, maxY = body.position.y };
            results.Add(identity.ObjectId, result); bodies.Add(identity.ObjectId, probe); spawned++;
        }
        private void Update()
        {
            if (finished || manager == null) return;
            try
            {
                Require(Time.realtimeSinceStartup - createdAt < duration + 35, "Connection/object baseline timed out.");
                if (runningAt < 0)
                {
                    if (manager.ObjectCount != 4) return;
                    runningAt = Time.realtimeSinceStartup; nextJump = .8f;
                }
                float time = Time.realtimeSinceStartup - runningAt;
                bool jump = time >= nextJump; if (jump) nextJump += 1.4f;
                foreach (var pair in bodies)
                {
                    if (pair.Value == null) continue;
                    var identity = pair.Value.GetComponent<NetworkIdentity>(); var motor = pair.Value.GetComponent<SocketJackObject2D>(); var body = pair.Value.GetComponent<Rigidbody2D>();
                    if (identity.IsOwner)
                    {
                        float direction = ((int)(time / 4) & 1) == 0 ? 1 : -1;
                        motor.SetInput(direction, motor.Movement == NetworkMovement2D.TopDown ? .6f : 0, jump && motor.Movement == NetworkMovement2D.Platformer);
                    }
                    var result = results[pair.Key]; result.owned = identity.IsOwner;
                    result.inputsSent = motor.InputsSent; result.inputsReceived = motor.InputsReceived; result.keyframes = motor.KeyframesReceived; result.ownerStates = motor.OwnerStatesSent;
                    result.jumps = motor.JumpsPerformed; result.hardCorrections = motor.HardCorrections; result.collisions = pair.Value.Collisions;
                    result.minX = Mathf.Min(result.minX, body.position.x); result.maxX = Mathf.Max(result.maxX, body.position.x);
                    result.minY = Mathf.Min(result.minY, body.position.y); result.maxY = Mathf.Max(result.maxY, body.position.y);
                    result.peakError = motor.PeakPositionError; result.peakVelocityRepair = motor.PeakNetworkVelocityCorrection; result.peakVisualStep = motor.PeakVisualStep;
                    byte[] state = motor.CaptureState(); Require(motor.ValidateState(state), "Local quantized 2D snapshot failed validation."); result.stateBytes = state.Length;
                }
                if (time >= duration + (role == "server" ? 4 : 0)) Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }
        private void Verify()
        {
            Require(spawned == 4 && results.Count == 4, "Did not receive all four platformer/top-down objects.");
            if (role == "client") Require(manager.IsClient && manager.ObjectCount == 4, "Client disconnected before acceptance finished.");
            Require(!manager.EnableAutomaticPortForwarding && !manager.IsPortMapped, "Fixture must never alter a router.");
            foreach (var item in results.Values)
            {
                Require(item.stateBytes > 0 && item.stateBytes < 70, "Compact 2D snapshots did not reduce the full precision payload.");
                Require(item.maxX - item.minX > .8f, "2D horizontal movement was not simulated.");
                Require(item.minX > -9 && item.maxX < 9, "2D wall collision bounds were breached.");
                if (item.movement == NetworkMovement2D.Platformer.ToString())
                {
                    Require(item.jumps > 0 && item.maxY > 1.2f, "Grounded platformer jump was not simulated.");
                    Require(item.collisions > 0 && item.minY > -.2f, "Platformer did not collide with the native floor.");
                }
                else Require(item.maxY - item.minY > .8f && item.minY > 7 && item.maxY < 17, "Top-down 2D movement/collision failed.");
                if (role == "server" || !item.owned) Require(item.inputsReceived >= 20, "Owner intent was not received/relayed to simulation.");
                if (role == "client")
                {
                    if (item.owned) Require(item.inputsSent >= 20, "Owner did not send bounded movement intent.");
                    if (authority == "owner" && item.owned) Require(item.ownerStates >= 20, "Owner-authoritative keyframes were not sent.");
                    else Require(item.keyframes >= 20, "Authoritative 2D keyframes were not received.");
                }
                Require(!float.IsNaN(item.peakError) && item.peakVelocityRepair < 30, "2D correction produced an invalid or unbounded repair.");
            }
        }
        private void Finish(Exception error)
        {
            if (finished) return; finished = true;
            try { if (error == null) Verify(); } catch (Exception verification) { error = verification; }
            string dll = Path.Combine(Application.dataPath, "Managed", "SocketJack.Unity.dll");
#if UNITY_EDITOR
            dll = Path.Combine(Application.dataPath, "Plugins", "SocketJack.Unity.dll");
#endif
            string hash = ""; if (File.Exists(dll)) using (var stream = File.OpenRead(dll)) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (error == null && hash != Argument("-probe-runtime-hash", hash)) error = new InvalidOperationException("Native runtime DLL hash differs from the requested candidate.");
            var receipt = new Receipt { runId = runId, result = error == null ? "PASS" : "FAIL", error = error?.ToString(), role = role,
                transport = manager == null ? "" : manager.Transport.ToString(), authority = authority, unityVersion = Application.unityVersion, runtimeSha256 = hash,
                port = manager != null && manager.IsServer ? manager.Server.Transport.LocalEndPoint.Port : 0, spawned = spawned, active = manager == null ? 0 : manager.ObjectCount,
                playing = Application.isPlaying, seconds = runningAt < 0 ? 0 : Time.realtimeSinceStartup - runningAt, objects = results.Values.ToArray() };
            if (!string.IsNullOrEmpty(receiptPath)) File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true));
            if (manager != null) manager.Shutdown();
            if (error != null) Debug.LogException(error); else Debug.Log("NATIVE_PHYSICS2D_PASS " + role + " " + receipt.transport + " " + authority + " objects=" + spawned);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(error == null ? 0 : 1);
#else
            Application.Quit(error == null ? 0 : 1);
#endif
        }
        private static string Argument(string key, string fallback)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
    public sealed class BodyProbe : MonoBehaviour
    {
        public int Collisions { get; private set; }
        private void OnCollisionEnter2D(Collision2D collision) => Collisions++;
    }
}
