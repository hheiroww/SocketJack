using System;
using System.IO;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Tests.Gameplay;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class GameplayProbeBuild
{
    public static void Build()
    {
        Directory.CreateDirectory("Assets/Generated");
        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>("Assets/Generated/Probe.controller");
        if (controller == null)
        {
        controller = AnimatorController.CreateAnimatorControllerAtPath("Assets/Generated/Probe.controller");
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float); controller.AddParameter("Phase", AnimatorControllerParameterType.Int);
        controller.AddParameter("Open", AnimatorControllerParameterType.Bool); controller.AddParameter("Pulse", AnimatorControllerParameterType.Trigger);
        var machine = controller.layers[0].stateMachine; var idle = machine.AddState("Idle"); var triggered = machine.AddState("Triggered"); machine.defaultState = idle;
        var clip = new AnimationClip(); clip.SetCurve("", typeof(Transform), "localPosition.y", AnimationCurve.Constant(0, .2f, 0));
        AssetDatabase.CreateAsset(clip, "Assets/Generated/Pulse.anim"); triggered.motion = clip;
        var enter = machine.AddAnyStateTransition(triggered); enter.hasExitTime = false; enter.duration = 0; enter.canTransitionToSelf = false; enter.AddCondition(AnimatorConditionMode.If, 0, "Pulse");
        var exit = triggered.AddTransition(idle); exit.hasExitTime = true; exit.exitTime = 1; exit.duration = 0;
        }
        var avatar = new GameObject("GameplayActor"); avatar.AddComponent<NetworkIdentity>(); avatar.AddComponent<GameplayLifecycle>();
        avatar.AddComponent<NetworkTransform>().PrecisionPreset = NetworkPrecisionPreset.Balanced;
        avatar.AddComponent<SocketJackHealth>(); var animator = avatar.AddComponent<Animator>(); animator.runtimeAnimatorController = controller; animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        avatar.AddComponent<SocketJackAdaptiveObject>();
        var numeric = avatar.GetComponent<SocketJackMemberSync>().AddMember(avatar.GetComponent<GameplayLifecycle>(), nameof(GameplayLifecycle.AdaptiveScore), maxBytes:64);
        numeric.AdaptivePrecision = true;
        var sync = avatar.AddComponent<SocketJackAnimator>(); sync.Animator = animator;
        sync.Parameters = new[] {
            new SocketJackAnimator.Parameter { Name="Speed", Type=AnimatorControllerParameterType.Float },
            new SocketJackAnimator.Parameter { Name="Phase", Type=AnimatorControllerParameterType.Int },
            new SocketJackAnimator.Parameter { Name="Open", Type=AnimatorControllerParameterType.Bool },
            new SocketJackAnimator.Parameter { Name="Pulse", Type=AnimatorControllerParameterType.Trigger }
        };
        var prefab = PrefabUtility.SaveAsPrefabAsset(avatar, "Assets/Generated/GameplayActor.prefab"); UnityEngine.Object.DestroyImmediate(avatar);
        foreach (string name in new[] { "FoundationA", "FoundationB" })
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var door = new GameObject("Placed door " + name); door.AddComponent<NetworkIdentity>(); door.AddComponent<GameplayLifecycle>();
            door.AddComponent<NetworkSceneObject>().SceneObjectId = name == "FoundationA" ? 101u : 102u;
            door.AddComponent<SocketJackDoor>();
            if (name == "FoundationA") new GameObject("Gameplay acceptance bootstrap").AddComponent<GameplayProbe>().AvatarPrefab = prefab;
            EditorSceneManager.SaveScene(scene, "Assets/Generated/" + name + ".unity");
        }
        var arguments = Environment.GetCommandLineArgs(); int at = Array.IndexOf(arguments, "-gameplayOutput"); if (at < 0 || at + 1 >= arguments.Length) throw new ArgumentException("Supply -gameplayOutput.");
        string output = Path.GetFullPath(arguments[at + 1]); Directory.CreateDirectory(Path.GetDirectoryName(output));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { target = BuildTarget.StandaloneWindows64, scenes = new[] { "Assets/Generated/FoundationA.unity", "Assets/Generated/FoundationB.unity" }, locationPathName = output, options = BuildOptions.Development | BuildOptions.StrictMode });
        if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("Gameplay build failed: " + report.summary.result);
        Debug.Log("SOCKETJACK_GAMEPLAY_BUILD_PASS " + Application.unityVersion);
    }
}
