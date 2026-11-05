using System;
using System.IO;
using SocketJack.Tests.Browser;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class BrowserProbeBuild
{
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("WebSocket browser acceptance").AddComponent<BrowserProbe>();
        Directory.CreateDirectory("Assets/Scenes"); EditorSceneManager.SaveScene(scene, "Assets/Scenes/Browser.unity");
        PlayerSettings.companyName = "SocketJack"; PlayerSettings.productName = "SocketJack browser acceptance";
        PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.WebGL, ApiCompatibilityLevel.NET_Standard);
        PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
        PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.FullWithStacktrace;
        PlayerSettings.runInBackground = true;
        var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-browserOutput");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Supply -browserOutput for the isolated build.");
        string output = Path.GetFullPath(args[index + 1]); Directory.CreateDirectory(output);
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { target = BuildTarget.WebGL, scenes = new[] { "Assets/Scenes/Browser.unity" }, locationPathName = output, options = BuildOptions.Development | BuildOptions.StrictMode });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("WebGL build failed: " + report.summary.result);
        Debug.Log("SOCKETJACK_WEBGL_BUILD_PASS " + Application.unityVersion);
    }
}
