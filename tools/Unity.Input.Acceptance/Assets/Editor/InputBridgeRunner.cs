using SocketJack.Tests.Input;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class InputBridgeRunner
{
    public static void Run()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        new GameObject("Input acceptance").AddComponent<InputBridgeProbe>();
        EditorApplication.isPlaying = true;
    }
}
