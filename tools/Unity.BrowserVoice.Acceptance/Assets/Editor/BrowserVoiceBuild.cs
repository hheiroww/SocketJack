using System;
using System.IO;
using SocketJack.Tests.BrowserVoice;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BrowserVoiceBuild
{
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Browser Voice acceptance").AddComponent<BrowserVoiceProbe>();
        Directory.CreateDirectory("Assets/Scenes"); EditorSceneManager.SaveScene(scene, "Assets/Scenes/Voice.unity");
        PlayerSettings.companyName = "SocketJack"; PlayerSettings.productName = "SocketJack browser Voice acceptance";
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.WebGL, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
        PlayerSettings.runInBackground = true;
        var args = Environment.GetCommandLineArgs(); int at = Array.IndexOf(args, "-browserOutput"); if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("Supply -browserOutput.");
        string output = Path.GetFullPath(args[at + 1]); Directory.CreateDirectory(output);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { target = BuildTarget.WebGL, scenes = new[] { "Assets/Scenes/Voice.unity" }, locationPathName = output, options = BuildOptions.Development | BuildOptions.StrictMode });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Browser Voice build failed: " + report.summary.result);
        Debug.Log("SOCKETJACK_BROWSER_VOICE_BUILD_PASS " + Application.unityVersion);
    }
}
