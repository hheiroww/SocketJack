using System;
using System.IO;
using SocketJack.Samples.Physics.Editor;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Tests.GameplayScreenshots.Editor
{
    public static class ArenaCaptureBuild
    {
        public static void Build()
        {
            PhysicsSampleBuild.PrepareScene();
            new GameObject("Test-only raw arena screenshot").AddComponent<ArenaCapture>();
            EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), "Assets/Scenes/Physics.unity");
            PlayerSettings.defaultScreenWidth = 1920; PlayerSettings.defaultScreenHeight = 1080;
            PlayerSettings.runInBackground = true; PlayerSettings.fullScreenMode = FullScreenMode.Windowed;
            var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-arena-output"); if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing owned player output.");
            Directory.CreateDirectory(Path.GetDirectoryName(args[index + 1]));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/Physics.unity" }, locationPathName = args[index + 1], target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Actual arena capture player build failed.");
            Debug.Log("SOCKETJACK_ARENA_BUILD_PASS");
        }
    }
}
