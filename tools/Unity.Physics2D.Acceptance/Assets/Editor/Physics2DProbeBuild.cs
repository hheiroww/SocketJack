using System;
using System.IO;
using System.Security.Cryptography;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using SocketJack.Tests.Physics2D;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Tests.Physics2D.Editor
{
    public static class Physics2DProbeBuild
    {
        [Serializable] private sealed class Receipt { public string result, unityVersion, runtimeSha256; public int configurationCases; public ulong bytes; }
        public static void BuildWindows()
        {
            Directory.CreateDirectory("Assets/Resources"); Directory.CreateDirectory("Assets/Scenes");
            foreach (NetworkMovement2D mode in Enum.GetValues(typeof(NetworkMovement2D)))
            {
                var go = new GameObject("Physics2D" + mode);
                try
                {
                    var collider = go.AddComponent<BoxCollider2D>(); collider.size = new Vector2(.9f, 1);
                    var body = go.AddComponent<Rigidbody2D>(); body.mass = 2.3f; body.constraints = RigidbodyConstraints2D.FreezeRotation;
                    body.gravityScale = mode == NetworkMovement2D.Platformer ? 1 : 0;
                    body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
                    var motor = go.AddComponent<SocketJackObject2D>(); motor.Movement = mode; motor.ReadKeyboard = false;
                    motor.MovementForce = 15; motor.MaxSpeed = 3; motor.JumpVelocity = 5; motor.PrecisionPreset = NetworkPrecisionPreset.Compact;
                    string materialPath = "Assets/Resources/Physics2D" + mode + ".physicsMaterial2D";
                    var material = AssetDatabase.LoadAssetAtPath<PhysicsMaterial2D>(materialPath);
                    if (material == null) { material = new PhysicsMaterial2D("2D probe friction"); AssetDatabase.CreateAsset(material, materialPath); }
                    material.friction = .05f; material.bounciness = 0; collider.sharedMaterial = material;
                    foreach (ObjectMovementMode movement in Enum.GetValues(typeof(ObjectMovementMode)))
                    {
                        motor.MovementMode = movement; motor.SendMessage("Update");
                        bool dynamic = movement == ObjectMovementMode.PhysicsEnabled || movement == ObjectMovementMode.PhysicsWithColliders;
                        bool colliders = movement == ObjectMovementMode.PhysicsWithColliders || movement == ObjectMovementMode.StaticWithColliders;
                        if ((body.bodyType == RigidbodyType2D.Dynamic) != dynamic || collider.enabled != colliders || body.mass != 2.3f || body.gravityScale != (mode == NetworkMovement2D.Platformer ? 1 : 0) || collider.sharedMaterial != material || body.constraints != RigidbodyConstraints2D.FreezeRotation)
                            throw new InvalidOperationException("2D movement mode changed native physics configuration.");
                    }
                    motor.MovementMode = ObjectMovementMode.PhysicsWithColliders; motor.SendMessage("Update");
                    go.SetActive(false);
                    PrefabUtility.SaveAsPrefabAsset(go, "Assets/Resources/Physics2D" + mode + ".prefab");
                }
                finally { UnityEngine.Object.DestroyImmediate(go); }
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("Physics2D acceptance").AddComponent<Physics2DAcceptance>(); EditorSceneManager.SaveScene(scene, "Assets/Scenes/Physics2D.unity");
            PlayerSettings.companyName = "SocketJack"; PlayerSettings.productName = "SocketJack Physics2D acceptance"; PlayerSettings.runInBackground = true;
            PlayerSettings.SetApiCompatibilityLevel(NamedBuildTarget.Standalone, ApiCompatibilityLevel.NET_Standard);
            PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x); AssetDatabase.SaveAssets();
            string output = Argument("-socketjackOutput"); Directory.CreateDirectory(Path.GetDirectoryName(output));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions { scenes = new[] { "Assets/Scenes/Physics2D.unity" }, locationPathName = output,
                target = BuildTarget.StandaloneWindows64, options = BuildOptions.Development | BuildOptions.StrictMode });
            if (report.summary.result != BuildResult.Succeeded) throw new InvalidOperationException("2D player build failed: " + report.summary.result);
            string hash; using (var stream = File.OpenRead("Assets/Plugins/SocketJack.Unity.dll")) using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(output), "build-receipt.json"), JsonUtility.ToJson(new Receipt { result = "PASS", unityVersion = Application.unityVersion, runtimeSha256 = hash, bytes = report.summary.totalSize, configurationCases = 8 }, true));
            Debug.Log("SOCKETJACK_PHYSICS2D_BUILD_PASS modes=8 " + output);
        }
        private static string Argument(string key)
        { var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, key); if (i < 0 || i + 1 >= args.Length) throw new ArgumentException("Missing " + key); return args[i + 1]; }
    }
}
