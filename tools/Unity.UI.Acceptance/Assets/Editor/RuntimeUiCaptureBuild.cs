using System;
using System.IO;
using SocketJack.Tests.Voice;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Acceptance.UI.Editor
{
    public static class RuntimeUiCaptureBuild
    {
        public static void BuildWindows()
        {
            Directory.CreateDirectory("Assets/Scenes"); var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Actual account chat and voice").AddComponent<AccountVoiceAcceptance>();
            new GameObject("Documentation screenshot capture").AddComponent<RuntimeUiCapture>();
            var camera = new GameObject("Plain UI backdrop").AddComponent<Camera>(); camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(.035f, .04f, .05f);
            EditorSceneManager.SaveScene(scene, "Assets/Scenes/RuntimeUiCapture.unity");
            PlayerSettings.defaultScreenWidth = 1440; PlayerSettings.defaultScreenHeight = 840; PlayerSettings.fullScreenMode = FullScreenMode.Windowed; PlayerSettings.runInBackground = true;
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard); PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-socketjackOutput"); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing owned player output.");
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/RuntimeUiCapture.unity" }, locationPathName = args[index + 1], target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Actual UI capture player build failed."); Debug.Log("SOCKETJACK_UI_CAPTURE_BUILD_PASS");
        }
    }
}
