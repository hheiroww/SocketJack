using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Multiplayer.Transport;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SocketJack.Tests.MemberSync
{
    public sealed class NativeMemberProbe : MonoBehaviour
    {
        [Serializable] public sealed class CaseReceipt { public string transport, formatter, topology; public int baselineBytes, replacementBytes, members, receivers; public bool passed; }
        [Serializable] public sealed class Receipt { public string unityVersion, runtimeSha256, result, execution, error, runId; public bool playing; public CaseReceipt[] cases; }
        private readonly List<CaseReceipt> cases = new List<CaseReceipt>();
        private readonly List<NetworkManager> managers = new List<NetworkManager>();
        private IEnumerator steps;
        private float deadline;
        private bool finished;

        private void OnEnable() { Application.logMessageReceived += OnLog; }
        private void OnDisable() { Application.logMessageReceived -= OnLog; }
        private void OnLog(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Finish(new InvalidOperationException(message + "\n" + trace));
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (!Environment.GetCommandLineArgs().Contains("-member-sync-probe")) return;
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var sample in root.GetComponentsInChildren<SocketJack.Samples.Physics.PhysicsGame>(true)) sample.enabled = false;
            };
            var go = new GameObject("SocketJack member acceptance probe"); DontDestroyOnLoad(go); go.AddComponent<NativeMemberProbe>();
        }
        private void Start()
        {
            Application.runInBackground = true; Application.targetFrameRate = 120; QualitySettings.vSyncCount = 0;
            steps = Run();
        }
        private void Update()
        {
            if (finished || steps == null) return;
            try { if (!steps.MoveNext()) Finish(null); }
            catch (Exception error) { Finish(error); }
        }
        private IEnumerator Run()
        {
            Require(Application.isPlaying, "Probe must run in actual native Play Mode.");
            foreach (var transport in new[] { TransportType.Udp, TransportType.Tcp })
                foreach (bool custom in new[] { false, true })
                {
                    var routine = RunCase(transport, custom, false);
                    while (routine.MoveNext()) yield return null;
                    foreach (var manager in managers) { manager.Shutdown(); Destroy(manager.gameObject); }
                    managers.Clear(); yield return null;
                }
            foreach (var transport in new[] { TransportType.Udp, TransportType.Tcp })
            {
                var routine = RunCase(transport, true, true);
                while (routine.MoveNext()) yield return null;
                foreach (var manager in managers) { manager.Shutdown(); Destroy(manager.gameObject); }
                managers.Clear(); yield return null;
            }
        }
        private NetworkManager CreateManager(TransportType transport, bool custom, string name)
        {
            var prefab = Resources.Load<GameObject>("MemberProbeManager");
            Require(prefab != null, "Serialized manager prefab missing.");
            var clone = Instantiate(prefab); clone.name = name;
            var manager = clone.GetComponent<NetworkManager>(); managers.Add(manager);
            Require(manager.SerializerKind == NetworkSerializerKind.CustomProvider && manager.SerializerProvider == clone.GetComponent<MemberProbeProvider>(), "Serialized Inspector provider reference did not remap to clone.");
            manager.SerializerKind = custom ? NetworkSerializerKind.CustomProvider : NetworkSerializerKind.BinaryFormatterSocketJack;
            manager.EnableAutomaticPortForwarding = false; manager.Transport = transport; manager.Port = 0; manager.Address = "127.0.0.1";
            return manager;
        }
        private IEnumerator RunCase(TransportType transport, bool custom, bool host)
        {
            deadline = Time.realtimeSinceStartup + 30;
            var server = CreateManager(transport, custom, "Server"); var owner = host ? server : CreateManager(transport, custom, "Owner");
            var observer = CreateManager(transport, custom, "Observer"); var late = CreateManager(transport, custom, "LateJoin");
            if (host) { foreach (var manager in managers) manager.Topology = NetworkTopology.HostPeer; server.StartHost(); }
            else server.StartServer();
            int port = server.Server.Transport.LocalEndPoint.Port;
            observer.Port = late.Port = port;
            if (!host) { owner.Port = port; owner.StartClient(); }
            if (host) observer.StartPeer(); else observer.StartClient();
            while (!owner.IsClient || !observer.IsClient) { CheckTime(); yield return null; }
            var identity = server.Spawn(1, Vector3.zero, Quaternion.identity, owner.LocalPeerId);
            ulong id = identity.ObjectId;
            while (!Ready(owner, id) || !Ready(observer, id)) { CheckTime(); yield return null; }
            Require(identity.ServerAuthority, "Member ownership must also work on a server-authoritative object.");
            var serverValue = identity.GetComponent<MemberProbeValue>(); var ownerValue = Value(owner, id); var observerValue = Value(observer, id);
            Require(ownerValue.OwnerValue == 11 && ownerValue.ServerValue == 7 && ownerValue.Blob.Length == 6000, "Owner initial member baseline failed.");
            Require(observerValue.OwnerValue == 11 && observerValue.ServerValue == 7 && observerValue.Blob.Length == 6000, "Observer initial member baseline failed.");
            foreach (var manager in new[] { server, owner, observer })
            {
                manager.TryGetIdentity(id, out var cloneIdentity);
                var bindings = cloneIdentity.GetComponent<SocketJackMemberSync>();
                Require(bindings.Members.Count == 5 && bindings.Members[0].Target == cloneIdentity.GetComponent<MemberProbeValue>(), "Member accessor still references original prefab instead of clone.");
                Require(!bindings.Members[4].Enabled, "Disabled arbitrary object member was unexpectedly enabled.");
            }
            serverValue.ServerValue = 123; serverValue.ServerVector = new Vector3(9, 8, 7);
            ownerValue.OwnerValue = 321; ownerValue.Blob = Enumerable.Range(0, 6100).Select(i => (byte)(i * 19)).ToArray();
            while (serverValue.OwnerValue != 321 || serverValue.Blob.Length != 6100 || observerValue.OwnerValue != 321 || observerValue.Blob.Length != 6100 || observerValue.ServerValue != 123 || observerValue.ServerVector != serverValue.ServerVector)
            { CheckTime(); yield return null; }
            Require(serverValue.Blob.SequenceEqual(ownerValue.Blob) && observerValue.Blob.SequenceEqual(ownerValue.Blob), "Large member state bytes changed across formatter/fragmentation.");
            if (host) late.StartPeer(); else late.StartClient();
            while (!Ready(late, id)) { CheckTime(); yield return null; }
            var lateValue = Value(late, id);
            Require(lateValue.OwnerValue == 321 && lateValue.ServerValue == 123 && lateValue.Blob.SequenceEqual(ownerValue.Blob) && lateValue.ServerVector == serverValue.ServerVector, "Late join canonical member baseline failed.");
            if (custom)
                foreach (var manager in managers) Require(manager.Serializer is MemberProbeProvider.ProbeSerializer selected && selected.Reads > 0, "Custom Inspector provider was bypassed by a receiving endpoint.");
            bool locked = false; try { owner.Serializer = new SocketJackBinaryFormatter(); } catch (InvalidOperationException) { locked = true; }
            Require(locked, "Active manager serializer configuration was not locked.");
            Require(managers.All(manager => !manager.EnableAutomaticPortForwarding && !manager.IsPortMapped), "Probe must never mutate a router.");
            cases.Add(new CaseReceipt { transport = transport.ToString(), formatter = custom ? "custom Inspector provider" : "Binary formatter (SocketJack)", topology = host ? "Host / Peer" : "Dedicated Server / Client", baselineBytes = 6000, replacementBytes = 6100, members = 4, receivers = 4, passed = true });
            Debug.Log("NATIVE_MEMBER_CASE_PASS " + transport + " custom=" + custom + " host=" + host);
        }
        private static bool Ready(NetworkManager manager, ulong id) => manager.TryGetIdentity(id, out var identity) && identity.GetComponent<SocketJackMemberSync>().IsSynchronized && manager.MemberClient != null && manager.MemberClient.IsReady(id);
        private static MemberProbeValue Value(NetworkManager manager, ulong id) { manager.TryGetIdentity(id, out var identity); return identity.GetComponent<MemberProbeValue>(); }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private void CheckTime() { Require(Time.realtimeSinceStartup < deadline, "Native member condition timed out."); }
        private void Finish(Exception error)
        {
            if (finished) return; finished = true;
            foreach (var manager in managers) if (manager != null) manager.Shutdown();
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-member-receipt");
            var receipt = new Receipt { unityVersion = Application.unityVersion, playing = Application.isPlaying, execution = Application.isEditor ? "Editor Play Mode" : "Native Player", result = error == null ? "PASS" : "FAIL", error = error?.ToString(), cases = cases.ToArray() };
            int runIndex = Array.IndexOf(args, "-member-run-id"); receipt.runId = runIndex >= 0 && runIndex + 1 < args.Length ? args[runIndex + 1] : "";
            string dll = Path.Combine(Application.dataPath, "Managed", "SocketJack.Unity.dll");
#if UNITY_EDITOR
            dll = Path.Combine(Application.dataPath, "Plugins", "SocketJack.Unity.dll");
#endif
            if (File.Exists(dll)) using (var stream = File.OpenRead(dll)) using (var sha = SHA256.Create()) receipt.runtimeSha256 = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            if (index >= 0 && index + 1 < args.Length) File.WriteAllText(args[index + 1], JsonUtility.ToJson(receipt, true));
            if (error != null) Debug.LogException(error); else Debug.Log("NATIVE_MEMBER_PROBE_PASS cases=" + cases.Count);
#if UNITY_EDITOR
            UnityEditor.EditorApplication.Exit(error == null ? 0 : 1);
#else
            Application.Quit(error == null ? 0 : 1);
#endif
        }
    }
}
