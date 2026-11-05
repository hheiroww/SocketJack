#if UNITY_EDITOR
using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Acceptance.Web.Editor
{
    public static class NativeWebBuild
    {
        public static void Build()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                new GameObject("Explicit Web Acceptance").AddComponent<NativeWebGame>();
                const string scene = "Assets/SocketJackWebAcceptance.unity"; EditorSceneManager.SaveScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene(), scene);
                string output = Argument("-socketjackOutput"); if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Pass an owned output executable path.");
                Directory.CreateDirectory(Path.GetDirectoryName(output));
                PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.Standalone, ApiCompatibilityLevel.NET_Standard_2_0);
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);
                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { scene }, locationPathName = output, target = BuildTarget.StandaloneWindows64, options = BuildOptions.None });
                if (report.summary.result != BuildResult.Succeeded) throw new BuildFailedException("Native web player build failed.");
                Debug.Log("SOCKETJACK_WEB_NATIVE_BUILD_PASS " + report.summary.totalSize); EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static string Argument(string key) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
    }
}
#endif
