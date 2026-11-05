using System;
using System.Linq;
using System.Runtime.InteropServices;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;

namespace SocketJack.Tests.Browser
{
    public sealed class BrowserProbe : MonoBehaviour
    {
        private NetworkManager manager;
        private GameObject template;
        private double deadline;
        private int phase;
        private ulong objectId;
#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void SJBrowserProbe_Result(string result);
#endif
        private void Start()
        {
            try
            {
                Application.runInBackground = true; deadline = Time.realtimeSinceStartupAsDouble + 45;
                string endpoint = "ws://127.0.0.1:19875/ws/game";
                var query = new Uri(Application.absoluteURL).Query.TrimStart('?').Split('&');
                foreach (string pair in query) if (pair.StartsWith("endpoint=", StringComparison.Ordinal)) endpoint = Uri.UnescapeDataString(pair.Substring(9));
                var root = new GameObject("Browser network manager"); manager = root.AddComponent<NetworkManager>();
                var connector = root.AddComponent<SocketJackBrowserTransport>(); connector.Endpoint = endpoint;
                // Deliberately nonsecret, loopback-only fixture ticket. Production tickets come
                // from the authenticated account API and must never be embedded in a build/URL.
                if (query.Contains("relay=1")) connector.RelayTicket = "loopback-probe-client";
                manager.EnableAutomaticPortForwarding = false;
                manager.Serializer = new SocketJackBinaryFormatter(); // Shared portable byte[] schema; no Unity-specific values in this probe.
                template = new GameObject("Browser replicated object"); template.AddComponent<NetworkIdentity>();
                var value = template.AddComponent<BrowserProbeValue>();
                template.AddComponent<SocketJackMemberSync>().AddMember(value, nameof(BrowserProbeValue.Blob), NetworkMemberAuthority.Server, maxBytes: 8192);
                template.SetActive(false); manager.RegisterPrefabs(template);
                manager.ClientDisconnected += reason => { if (phase < 3) Finish(false, "disconnected: " + reason); };
                manager.StartClient();
            }
            catch (Exception error) { Finish(false, error.GetType().Name + ": " + error.Message); }
        }
        private void Update()
        {
            if (phase >= 3) return;
            try
            {
                if (Time.realtimeSinceStartupAsDouble >= deadline) { Finish(false, "browser network deadline"); return; }
                if (manager == null || !manager.IsClient) return;
                if (phase == 0)
                {
                    if (manager.Client.Objects.Count == 0) return;
                    var obj = manager.Client.Objects.Values.First(); objectId = obj.Id;
                    if (!manager.TryGetIdentity(objectId, out var identity) || !identity.GetComponent<SocketJackMemberSync>().IsSynchronized) return;
                    var bytes = identity.GetComponent<BrowserProbeValue>().Blob;
                    if (bytes.Length != 6000 || !bytes.SequenceEqual(Enumerable.Range(0, 6000).Select(i => (byte)(i * 13)))) throw new InvalidOperationException("6000-byte WebGL baseline differs.");
                    phase = 1;
                }
                if (phase == 1 && manager.Client.SendInput(objectId, new byte[] { 42 })) phase = 2;
                if (phase == 2 && manager.TryGetIdentity(objectId, out var changed) && changed.GetComponent<BrowserProbeValue>().Blob.Length == 6100)
                {
                    if (!changed.GetComponent<BrowserProbeValue>().Blob.SequenceEqual(Enumerable.Range(0, 6100).Select(i => (byte)(i * 17)))) throw new InvalidOperationException("6100-byte WebGL update differs.");
                    Finish(true, "actual WebGL/browser WebSocket:6000baseline,ownerinput,6100update");
                }
            }
            catch (Exception error) { Finish(false, error.GetType().Name + ": " + error.Message); }
        }
        private void Finish(bool passed, string message)
        {
            if (phase >= 3) return; phase = 3; manager?.Shutdown();
            string status = (passed ? "PASS " : "FAIL ") + message;
            Debug.Log("SOCKETJACK_BROWSER_" + status);
#if UNITY_WEBGL && !UNITY_EDITOR
            SJBrowserProbe_Result(status);
#endif
        }
    }
}
