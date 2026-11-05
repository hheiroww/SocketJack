using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Tests.Voice.Editor
{
    public static class AccountVoiceProbeBuild
    {
        public static void BuildWindows()
        {
            Directory.CreateDirectory("Assets/Scenes"); var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Exact Unity account voice acceptance").AddComponent<AccountVoiceAcceptance>(); EditorSceneManager.SaveScene(scene, "Assets/Scenes/AccountVoiceAcceptance.unity");
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard); PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-socketjackOutput"); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing player output path.");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/AccountVoiceAcceptance.unity" }, locationPathName = args[index + 1], target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Account voice player build failed: " + report.summary.result); Debug.Log("SOCKETJACK_VOICE_BUILD_PASS account bridges " + args[index + 1]);
        }
    }
}
