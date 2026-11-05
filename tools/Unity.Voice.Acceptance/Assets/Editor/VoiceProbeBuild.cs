using System;
using System.IO;
using SocketJack.Multiplayer.Components;
using SocketJack.Tests.Voice;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Tests.Voice.Editor
{
    public static class VoiceProbeBuild
    {
        public static void BuildWindows()
        {
            Directory.CreateDirectory("Assets/Resources"); Directory.CreateDirectory("Assets/Scenes");
            var player = new GameObject("VoiceAcceptancePlayer");
            try { player.AddComponent<NetworkTransform>(); player.SetActive(false); PrefabUtility.SaveAsPrefabAsset(player, "Assets/Resources/VoiceAcceptancePlayer.prefab"); }
            finally { UnityEngine.Object.DestroyImmediate(player); }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Native voice acceptance").AddComponent<VoiceAcceptance>(); EditorSceneManager.SaveScene(scene, "Assets/Scenes/VoiceAcceptance.unity");
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard); PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-socketjackOutput"); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing output path.");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/VoiceAcceptance.unity" }, locationPathName = args[index + 1], target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Native voice build failed: " + report.summary.result);
            Debug.Log("SOCKETJACK_VOICE_BUILD_PASS " + args[index + 1]);
        }
    }
}
