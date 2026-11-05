using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Web;
using SocketJack.Multiplayer.Web.Components;
using UnityEngine;

namespace SocketJack.Acceptance.Web
{
    /// <summary>Explicit acceptance scene only; no automatic initialization in consumer scenes.</summary>
    public sealed class NativeWebGame : MonoBehaviour
    {
        [Serializable] private sealed class Credentials { public string passwordA, passwordB; }
        [Serializable] private sealed class Receipt
        {
            public string result, role, editorVersion, error, realtimeError;
            public bool live;
            public bool loggedIn, connected, mainThreadCallbacks;
            public int maxObjects, globalMessages, proximityMessages, authenticatedPeers, peakPeers;
            public long hardCorrections;
        }
        private NetworkManager network;
        private SocketJackWebHost host;
        private SocketJackWebClientBehaviour web;
        private Credentials credentials;
        private string role, receiptPath;
        private double finishAt = double.MaxValue;
        private float duration, movementStart;
        private bool initialized, sent, stopping;
        private int mainThread;
        private readonly Receipt receipt = new Receipt { mainThreadCallbacks = true };
        private readonly HashSet<string> global = new HashSet<string>(), proximity = new HashSet<string>();
        private void Awake()
        {
            Application.runInBackground = true; Application.targetFrameRate = 60; Time.fixedDeltaTime = 0.02f;
            mainThread = Thread.CurrentThread.ManagedThreadId;
            try
            {
                role = Argument("-webRole", "server"); receiptPath = Argument("-webReceipt", ""); duration = float.Parse(Argument("-webSeconds", "20"), System.Globalization.CultureInfo.InvariantCulture);
                credentials = JsonUtility.FromJson<Credentials>(File.ReadAllText(Argument("-webCredentials", "")));
                receipt.role = role; receipt.editorVersion = Application.unityVersion;
                network = gameObject.AddComponent<NetworkManager>(); network.Topology = NetworkTopology.DedicatedServerClient;
                network.Port = int.Parse(Argument("-gamePort", "18777")); network.EnableAutomaticPortForwarding = false;
                var template = GameObject.CreatePrimitive(PrimitiveType.Cube); template.name = "Native Web Player"; template.SetActive(false);
                var motor = template.AddComponent<SocketJackObject>(); motor.ReadKeyboard = false;
                template.GetComponent<Rigidbody>().useGravity = false;
                var registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>(); registry.SetEntries(new[] { new NetworkPrefabRegistry.Entry(1, template, "native-web-player") });
                var firstSpawn = new GameObject("First player spawn").AddComponent<NetworkSpawnPoint>(); firstSpawn.transform.position = new Vector3(-10, 1, 0);
                var secondSpawn = new GameObject("Second player spawn").AddComponent<NetworkSpawnPoint>(); secondSpawn.transform.position = new Vector3(10, 1, 0);
                var spawner = gameObject.AddComponent<NetworkPlayerSpawner>(); spawner.Configure(network, registry, 1, new[] { firstSpawn, secondSpawn });
                if (role == "server")
                {
                    host = gameObject.AddComponent<SocketJackWebHost>();
                    host.Configure("Native Web Acceptance", "native", int.Parse(Argument("-webPort", "18778")), Argument("-webData", ""));
                    host.RequireGameAccount = true; host.Started += server => { _ = InitializeAccountsAsync(); };
                    network.StartServer();
                }
                else
                {
                    web = gameObject.AddComponent<SocketJackWebClientBehaviour>(); web.ConfigureEndpoint("http://127.0.0.1:" + Argument("-webPort", "18778"));
                    web.MessageReceived += message =>
                    {
                        receipt.mainThreadCallbacks &= Thread.CurrentThread.ManagedThreadId == mainThread;
                        if (message.Text.StartsWith("native:", StringComparison.Ordinal))
                        { if (message.Channel == "global") global.Add(message.Id); if (message.Channel == "proximity") proximity.Add(message.Id); }
                    };
                    _ = ConnectAsync();
                }
            }
            catch (Exception error) { Fail(error); }
        }
        private async Task InitializeAccountsAsync()
        {
            try
            {
                await host.Server.Accounts.RegisterAsync("native_a", credentials.passwordA, "Native A");
                await host.Server.Accounts.RegisterAsync("native_b", credentials.passwordB, "Native B");
                initialized = true; finishAt = Time.realtimeSinceStartupAsDouble + duration;
                File.WriteAllText(Argument("-webReady", ""), "ready");
            }
            catch (Exception error) { Fail(error); }
        }
        private async Task ConnectAsync()
        {
            try
            {
                bool first = role == "clientA";
                await web.LoginAsync(first ? "native_a" : "native_b", first ? credentials.passwordA : credentials.passwordB);
                receipt.loggedIn = web.IsSignedIn; await web.JoinGameAsync(network);
                initialized = true; movementStart = Time.realtimeSinceStartup; finishAt = Time.realtimeSinceStartupAsDouble + duration;
            }
            catch (Exception error) { Fail(error); }
        }
        private void Update()
        {
            if (stopping || !initialized) return;
            receipt.maxObjects = Math.Max(receipt.maxObjects, network.ObjectCount);
            if (role == "server")
            {
                receipt.peakPeers = Math.Max(receipt.peakPeers, network.Server.Peers.Count); int authenticated = 0;
                foreach (var peer in network.Server.Peers) if (host.TryGetAuthenticatedAccount(peer, out _)) authenticated++;
                receipt.authenticatedPeers = Math.Max(receipt.authenticatedPeers, authenticated);
            }
            else
            {
                receipt.connected |= network.IsClient;
                receipt.live |= web.IsLive; receipt.realtimeError = web.LastError;
                foreach (var pair in network.SpawnedObjects)
                {
                    var identity = pair.Value; if (!identity.IsOwner) continue;
                    var motor = identity.GetComponent<SocketJackObject>();
                    motor.SetInput(((int)((Time.realtimeSinceStartup - movementStart) / 3) % 2 == 0) ? 1 : -1, 0.3f);
                    receipt.hardCorrections = motor.HardCorrections;
                }
                if (!sent && network.IsClient && network.ObjectCount >= 2 && Time.realtimeSinceStartup - movementStart > 2)
                { sent = true; _ = SendMessagesAsync(); }
                receipt.globalMessages = global.Count; receipt.proximityMessages = proximity.Count;
            }
            if (Time.realtimeSinceStartupAsDouble >= finishAt) Finish();
        }
        private async Task SendMessagesAsync()
        {
            try
            {
                await web.SendAsync("global", "native:" + role);
                await web.SendAsync("proximity", "native:" + role);
            }
            catch (Exception error) { Fail(error); }
        }
        private void Finish()
        {
            bool pass = role == "server" ? receipt.authenticatedPeers == 2 && receipt.peakPeers == 2 : receipt.loggedIn && receipt.connected && receipt.mainThreadCallbacks && receipt.maxObjects >= 2 && receipt.globalMessages >= 2 && receipt.proximityMessages >= 2 && receipt.hardCorrections == 0;
            receipt.result = pass ? "pass" : "fail"; if (!pass) receipt.error = "Native web/account/physics acceptance condition did not pass.";
            WriteReceipt(); stopping = true; network.Shutdown(); Application.Quit(pass ? 0 : 1);
        }
        private void Fail(Exception error)
        {
            if (stopping) return; stopping = true; receipt.result = "fail"; receipt.error = error.GetType().Name + ": " + error.Message;
            WriteReceipt(); Debug.LogError("SOCKETJACK_WEB_NATIVE_FAIL " + receipt.error); Application.Quit(1);
        }
        private void WriteReceipt() { if (!string.IsNullOrWhiteSpace(receiptPath)) File.WriteAllText(receiptPath, JsonUtility.ToJson(receipt, true)); }
        private static string Argument(string key, string fallback) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback; }
    }
}
