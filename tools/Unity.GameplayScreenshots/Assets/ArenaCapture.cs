using System;
using System.Collections.Generic;
using System.IO;
using SocketJack.Multiplayer.Components;
using UnityEngine;

namespace SocketJack.Tests.GameplayScreenshots
{
    /// <summary>Test-only raw screenshot of the real PhysicsGame scene and network-driven players.</summary>
    public sealed class ArenaCapture : MonoBehaviour
    {
        [Serializable] private sealed class Player { public string objectId; public float planarMovement; }
        [Serializable] private sealed class Proof { public string result, error, unityVersion, graphicsApi, image; public int width, height, peers, visiblePlayers; public Player[] players; public bool syntheticInputOnly, routerMappingDisabled; }
        private readonly Dictionary<ulong, Vector3> origins = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, float> movement = new Dictionary<ulong, float>();
        private string image, receipt;
        private bool requested, written;
        private NetworkManager manager;
        private double started;
        private static string Arg(string key) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
        private void Start() { image = Arg("-arena-capture"); receipt = Arg("-arena-receipt"); started = Time.realtimeSinceStartupAsDouble; if (image.Length == 0) enabled = false; }
        private void Update()
        {
            if (written) return;
            try
            {
                if (manager == null) manager = FindObjectOfType<NetworkManager>();
                if (manager == null || !manager.IsServer) return;
                var camera = Camera.main; if (camera == null) return;
                int visible = 0; bool moved = true;
                foreach (var identity in manager.SpawnedObjects.Values)
                {
                    if (identity.GetComponent<SocketJackObject>() == null) continue;
                    var position = identity.transform.position;
                    if (!origins.TryGetValue(identity.ObjectId, out var origin)) { origins[identity.ObjectId] = origin = position; movement[identity.ObjectId] = 0; }
                    var delta = position - origin; movement[identity.ObjectId] = Mathf.Max(movement[identity.ObjectId], new Vector2(delta.x, delta.z).magnitude);
                    moved &= movement[identity.ObjectId] > .5f;
                    var renderer = identity.GetComponentInChildren<Renderer>();
                    if (renderer != null && renderer.enabled)
                    {
                        var view = camera.WorldToViewportPoint(renderer.bounds.center);
                        if (view.z > 0 && view.x > 0 && view.x < 1 && view.y > 0 && view.y < 1) visible++;
                    }
                }
                if (requested)
                {
                    if (File.Exists(image) && new FileInfo(image).Length > 100) { Write(null, visible); return; }
                }
                else if (Time.realtimeSinceStartupAsDouble - started > 7 && manager.Server.Peers.Count >= 3 && visible >= 3 && moved && movement.Count >= 3)
                {
                    if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Direct3D11 || Screen.width != 1920 || Screen.height != 1080)
                        throw new InvalidOperationException("A real1920x1080 D3D11 game frame is required.");
                    CaptureCamera(camera); requested = true;
                }
                if (Time.realtimeSinceStartupAsDouble - started > 13) throw new TimeoutException("Three visible moving admitted players were not captured in time.");
            }
            catch (Exception error) { Write(error, 0); }
        }
        private void CaptureCamera(Camera camera)
        {
            var previousTarget = camera.targetTexture; var previousActive = RenderTexture.active;
            var target = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(1920, 1080, TextureFormat.RGB24, false);
            try
            {
                target.Create(); camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1920, 1080), 0, 0); pixels.Apply();
                int colored = 0, magenta = 0;
                foreach (var color in pixels.GetPixels32())
                {
                    if (color.r > 20 || color.g > 20 || color.b > 20) colored++;
                    if (color.r > 180 && color.b > 180 && color.g < 60) magenta++;
                }
                if (colored < 10000 || magenta > 100) throw new InvalidOperationException("The actual arena camera produced a blank or broken-shader image.");
                File.WriteAllBytes(image, pixels.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
                target.Release(); Destroy(target); Destroy(pixels);
            }
        }
        private void Write(Exception error, int visible)
        {
            if (written) return; written = true;
            var players = new List<Player>(); foreach (var pair in movement) players.Add(new Player { objectId = pair.Key.ToString(), planarMovement = pair.Value });
            File.WriteAllText(receipt, JsonUtility.ToJson(new Proof { result = error == null ? "PASS" : "FAIL", error = error?.ToString() ?? "", unityVersion = Application.unityVersion,
                graphicsApi = SystemInfo.graphicsDeviceType.ToString(), image = Path.GetFileName(image), width = Screen.width, height = Screen.height,
                peers = manager?.Server?.Peers.Count ?? 0, visiblePlayers = visible, players = players.ToArray(), syntheticInputOnly = true, routerMappingDisabled = manager != null && !manager.EnableAutomaticPortForwarding }, true));
            Debug.Log("SOCKETJACK_ARENA_CAPTURE_" + (error == null ? "PASS" : "FAIL"));
        }
    }
}
