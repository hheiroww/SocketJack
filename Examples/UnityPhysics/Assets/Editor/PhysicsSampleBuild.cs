using System;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Generates a minimal scene and builds the actual Unity/PhysX player.</summary>
public static class PhysicsSampleBuild
{
    [Serializable] private sealed class BuildReceipt
    {
        public string result, unityVersion, builtUtc, target, runtimeSha256, physicsComponentSha256, networkManagerSha256, sampleSha256;
        public ulong playerBytes;
        public double buildSeconds;
    }
    [MenuItem("SocketJack/Physics sample/Prepare scene")]
    public static void PrepareScene()
    {
        Directory.CreateDirectory("Assets/Scenes");
        Directory.CreateDirectory("Assets/Resources");
        if (AssetDatabase.LoadAssetAtPath<Material>("Assets/Resources/PhysicsStandard.mat") == null)
        {
            var shader = Shader.Find("Standard");
            if (shader == null) throw new InvalidOperationException("The built-in Standard shader is unavailable in this Editor.");
            AssetDatabase.CreateAsset(new Material(shader), "Assets/Resources/PhysicsStandard.mat");
        }
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        // PhysicsGame's runtime initializer creates the world, player registry and role menu.
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
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-receipt.json"), JsonUtility.ToJson(receipt, true));
        Debug.Log("SOCKETJACK_PHYSICS_BUILD_PASS " + output + " bytes=" + report.summary.totalSize);
    }
    private static string Argument(string key, string fallback)
    { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
    private static string Hash(string path)
    { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
}
