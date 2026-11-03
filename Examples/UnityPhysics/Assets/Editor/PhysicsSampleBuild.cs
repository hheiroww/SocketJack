using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using SocketJack.Unity.Components;
using SocketJack.Unity.Transport;

/// <summary>Generates a minimal scene and builds the actual Unity/PhysX player.</summary>
public static class PhysicsSampleBuild
{
    [Serializable] private sealed class BuildReceipt
    {
        public string result, unityVersion, builtUtc, target, runtimeSha256, physicsComponentSha256, networkManagerSha256, sampleSha256;
        public ulong playerBytes;
        public double buildSeconds;
        public int objectConfigurationCases;
    }
    [MenuItem("SocketJack/Physics sample/Prepare scene")]
    public static void PrepareScene()
    {
        VerifyObjectConfiguration();
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Resources");
        if (AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/SocketJackPhysicsStandard.mat") == null)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("The built-in Standard shader is unavailable in this Editor.");
            AssetDatabase.CreateAsset(new Material(shader), "Assets/Resources/SocketJackPhysicsStandard.mat");
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("SocketJack Physics Game").AddComponent<PhysicsGame>();
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/Physics.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/Physics.unity", true) };
        PlayerSettings.companyName = "SocketJack"; PlayerSettings.productName = "SocketJack Physics Multiplayer";
        PlayerSettings.runInBackground = true; PlayerSettings.resizableWindow = true;
        PlayerSettings.defaultScreenWidth = 1000; PlayerSettings.defaultScreenHeight = 700;
        PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        AssetDatabase.SaveAssets();
    }

    [MenuItem("SocketJack/Physics sample/Build Windows player")]
    public static void BuildWindows()
    {
        PrepareScene();
        string output = Argument("-socketjackOutput", Path.GetFullPath("Builds/Windows/SocketJackPhysics.exe"));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/Physics.unity" },
            locationPathName = output, target = BuildTarget.StandaloneWindows64,
            options = BuildOptions.Development | BuildOptions.StrictMode });
        if (report.summary.result != BuildResult.Succeeded)
            throw new InvalidOperationException("Unity physics player build failed: " + report.summary.result);
        var receipt = new BuildReceipt { result = report.summary.result.ToString(), unityVersion = Application.unityVersion,
            builtUtc = DateTime.UtcNow.ToString("O"), target = BuildTarget.StandaloneWindows64.ToString(),
            runtimeSha256 = Hash("Assets/Plugins/SocketJack.Unity.dll"),
            physicsComponentSha256 = Hash("Assets/SocketJack.Unity/SocketJackObject.cs"), sampleSha256 = Hash("Assets/Scripts/PhysicsGame.cs"),
            networkManagerSha256 = Hash("Assets/SocketJack.Unity/NetworkManager.cs"),
            playerBytes = report.summary.totalSize, buildSeconds = report.summary.totalTime.TotalSeconds };
        receipt.objectConfigurationCases = 4;
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-receipt.json"), JsonUtility.ToJson(receipt, true));
        Debug.Log("SOCKETJACK_PHYSICS_BUILD_PASS " + output + " bytes=" + report.summary.totalSize);
    }
    private static string Argument(string key, string fallback)
    { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    private static void VerifyObjectConfiguration()
    {
        var fixture = new GameObject("SocketJack object configuration check") { hideFlags = HideFlags.HideAndDontSave };
        var material = new PhysicMaterial("Configuration check") { dynamicFriction = 0.17f, staticFriction = 0.31f };
        try
        {
            var collider = fixture.AddComponent<BoxCollider>(); collider.sharedMaterial = material;
            var body = fixture.AddComponent<Rigidbody>(); body.mass = 3.7f; body.drag = 0.13f; body.angularDrag = 0.29f;
            body.useGravity = false; body.interpolation = RigidbodyInterpolation.Extrapolate;
            var component = fixture.AddComponent<SocketJackObject>();
            foreach (ObjectMovementMode mode in Enum.GetValues(typeof(ObjectMovementMode)))
            {
                component.MovementMode = mode;
                // Drive the same main-thread update that applies an Inspector mode change.
                component.SendMessage("Update", SendMessageOptions.RequireReceiver);
                bool kinematic = mode == ObjectMovementMode.Static || mode == ObjectMovementMode.StaticWithColliders;
                bool colliders = mode == ObjectMovementMode.StaticWithColliders || mode == ObjectMovementMode.PhysicsWithColliders;
                if (body.isKinematic != kinematic || collider.enabled != colliders || body.mass != 3.7f || body.drag != 0.13f ||
                    body.angularDrag != 0.29f || body.useGravity || body.interpolation != RigidbodyInterpolation.Extrapolate ||
                    collider.sharedMaterial != material || material.dynamicFriction != 0.17f || material.staticFriction != 0.31f)
                    throw new InvalidOperationException("SocketJackObject changed native Rigidbody/Collider configuration for mode " + mode);
            }
            Debug.Log("SOCKETJACK_OBJECT_CONFIGURATION_PASS cases=4 native Rigidbody settings preserved");
        }
        finally { UnityEngine.Object.DestroyImmediate(fixture); UnityEngine.Object.DestroyImmediate(material); }
    }

    /// <summary>Real Unity transforms and loopback sessions, with controlled simulation steps.
    /// This check runs in batch Editor mode and never requests a router mapping.</summary>
    public static void VerifyTransformAuthority()
    {
        foreach (bool serverAuthority in new[] { false, true })
        {
            var serverRoot = new GameObject("Transform authority server");
            var ownerRoot = new GameObject("Transform authority owner");
            var prefab = new GameObject("Transform authority prefab"); prefab.SetActive(false);
            prefab.AddComponent<NetworkIdentity>(); prefab.AddComponent<NetworkTransform>(); prefab.AddComponent<ExamplePlayerController>();
            var server = serverRoot.AddComponent<NetworkManager>(); var owner = ownerRoot.AddComponent<NetworkManager>();
            NetworkIdentity serverIdentity = null, ownerIdentity = null;
            try
            {
                server.EnableAutomaticPortForwarding = owner.EnableAutomaticPortForwarding = false;
                server.ServerAuthority = owner.ServerAuthority = serverAuthority;
                server.Port = 0; server.RegisterPrefabs(prefab); owner.RegisterPrefabs(prefab);
                server.StartServer(); owner.Port = server.Server.Transport.LocalEndPoint.Port; owner.StartClient();
                PumpUntil(server, owner, () => owner.IsClient);
                serverIdentity = server.Spawn(1, Vector3.zero, Quaternion.identity, owner.LocalPeerId);
                PumpUntil(server, owner, () => owner.TryGetIdentity(serverIdentity.ObjectId, out ownerIdentity));
                var serverTransform = serverIdentity.GetComponent<NetworkTransform>();
                var ownerTransform = ownerIdentity.GetComponent<NetworkTransform>();
                var serverController = serverIdentity.GetComponent<ExamplePlayerController>();
                var ownerController = ownerIdentity.GetComponent<ExamplePlayerController>();
                serverTransform.SendMessage("Awake"); ownerTransform.SendMessage("Awake");
                serverController.SendMessage("Start"); ownerController.SendMessage("Start");
                if (serverIdentity.HasStateAuthority != serverAuthority || ownerIdentity.HasStateAuthority == serverAuthority)
                    throw new InvalidOperationException("Canonical transform authority was not applied from spawn metadata.");
                if (!serverAuthority)
                {
                    ownerIdentity.transform.position = new Vector3(4, 0, 0);
                    ownerTransform.SendMessage("Interpolate", 1f);
                    if (Vector3.Distance(ownerIdentity.transform.position, new Vector3(4, 0, 0)) > 0.0001f)
                        throw new InvalidOperationException("Owner-authoritative transform was pulled back to its spawn baseline.");
                    ownerController.SetMoveIntent(Vector2.right); ownerController.SendMessage("SimulateOwner", 0.2f);
                    if (Mathf.Abs(ownerIdentity.transform.position.x - 5) > 0.001f)
                        throw new InvalidOperationException("Owner-authoritative example controller did not simulate locally.");
                    if (!owner.Client.SendOwnerState(serverIdentity.ObjectId, ownerTransform.CaptureState()))
                        throw new InvalidOperationException("Canonical owner transform was not accepted for sending.");
                    PumpUntil(server, owner, () => server.Server.Objects[serverIdentity.ObjectId].State.Length == 40 &&
                        BitConverter.ToSingle(server.Server.Objects[serverIdentity.ObjectId].State, 0) > 4.9f);
                    serverTransform.SendMessage("Interpolate", 1f);
                    if (serverIdentity.transform.position.x < 4.9f)
                        throw new InvalidOperationException("Server observer did not follow its owner's canonical transform.");
                    float before = serverIdentity.transform.position.x;
                    serverController.SendMessage("SimulateServer", 0.2f);
                    if (serverIdentity.transform.position.x != before)
                        throw new InvalidOperationException("Server example controller overwrote owner-authoritative movement.");
                }
                else
                {
                    ownerController.SetMoveIntent(Vector2.right); ownerController.SendMessage("SimulateOwner", 0.2f);
                    if (ownerIdentity.transform.position != Vector3.zero)
                        throw new InvalidOperationException("Server-authoritative example controller simulated on the owner.");
                    if (!owner.Client.SendInput(serverIdentity.ObjectId, new byte[] { 254, 127 }, DeliveryMode.ReliableUnordered))
                        throw new InvalidOperationException("Legacy server-authoritative movement input was not accepted.");
                    for (int i = 0; i < 20; i++) { Pump(server, owner); System.Threading.Thread.Sleep(1); }
                    serverController.SendMessage("SimulateServer", 0.2f);
                    if (serverIdentity.transform.position.x < 0.9f)
                        throw new InvalidOperationException("Server-authoritative example controller stopped applying owner input.");
                }
            }
            finally
            {
                // This probe runs in Edit mode: remove its native scene instances immediately
                // before session teardown so the normal Play-mode lifecycle does not call Destroy.
                if (ownerIdentity != null) UnityEngine.Object.DestroyImmediate(ownerIdentity.gameObject);
                if (serverIdentity != null) UnityEngine.Object.DestroyImmediate(serverIdentity.gameObject);
                owner.Shutdown(); server.Shutdown();
                UnityEngine.Object.DestroyImmediate(ownerRoot); UnityEngine.Object.DestroyImmediate(serverRoot);
                UnityEngine.Object.DestroyImmediate(prefab);
            }
        }
        Debug.Log("SOCKETJACK_TRANSFORM_AUTHORITY_PASS cases=2 owner prediction, canonical server observer, legacy controller guards");
    }
    private static void Pump(NetworkManager server, NetworkManager owner)
    { server.SendMessage("Update"); owner.SendMessage("Update"); }
    private static void PumpUntil(NetworkManager server, NetworkManager owner, Func<bool> predicate)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate())
        {
            if (elapsed.Elapsed.TotalSeconds > 5) throw new TimeoutException("Transform authority loopback probe timed out.");
            Pump(server, owner); System.Threading.Thread.Sleep(1);
        }
    }
}
