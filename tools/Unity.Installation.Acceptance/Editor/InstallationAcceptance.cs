#if UNITY_EDITOR
using System;
using System.IO;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Components.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Acceptance.Installation
{
    public static class InstallationAcceptance
    {
        [Serializable] private sealed class Receipt { public string result, editorVersion; public int checks; }
        private static int checks;
        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                if (!AssetDatabase.IsValidFolder("Assets/InstallationFixture")) AssetDatabase.CreateFolder("Assets", "InstallationFixture");
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube); source.name = "Existing Single Player";
                var body = source.AddComponent<Rigidbody>(); body.mass = 3.7f; body.useGravity = false;
                var camera = new GameObject("Local camera"); camera.transform.SetParent(source.transform); camera.AddComponent<Camera>();
                var prefab = PrefabUtility.SaveAsPrefabAsset(source, "Assets/InstallationFixture/Player.prefab"); UnityEngine.Object.DestroyImmediate(source);
                var registry = ScriptableObject.CreateInstance<NetworkPrefabRegistry>(); AssetDatabase.CreateAsset(registry, "Assets/InstallationFixture/Registry.asset");
                SocketJackSetupWizard.ApplyToScene(prefab, registry);
                Check(prefab.GetComponent<NetworkIdentity>() != null && prefab.GetComponent<SocketJackObject>() != null && prefab.GetComponent<NetworkLocalPlayer>() != null, "prefab components");
                Check(Mathf.Approximately(prefab.GetComponent<Rigidbody>().mass, 3.7f) && !prefab.GetComponent<Rigidbody>().useGravity, "native physics preserved");
                Check(registry.TryGetId(prefab, out uint id) && id == 1 && registry.BuildManifest().Contains(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab))), "stable registry ID/GUID");
#if UNITY_6000_0_OR_NEWER
                var manager = UnityEngine.Object.FindFirstObjectByType<NetworkManager>();
#else
                var manager = UnityEngine.Object.FindObjectOfType<NetworkManager>();
#endif
                Check(manager != null && manager.GetComponent<NetworkPlayerSpawner>() != null && manager.GetComponent<NetworkSessionControls>() != null, "runnable scene helpers");
                Check(prefab.GetComponent<SocketJackObject>().PrecisionPreset == SocketJack.Multiplayer.Serialization.NetworkPrecisionPreset.Balanced, "new setup Balanced precision");
                // Re-running setup must be idempotent and retain IDs and native configuration.
                prefab.GetComponent<SocketJackObject>().PrecisionPreset = SocketJack.Multiplayer.Serialization.NetworkPrecisionPreset.Compact;
                SocketJackSetupWizard.ApplyToScene(prefab, registry);
#if UNITY_6000_0_OR_NEWER
                int managerCount = UnityEngine.Object.FindObjectsByType<NetworkManager>(FindObjectsSortMode.None).Length;
#else
                int managerCount = UnityEngine.Object.FindObjectsOfType<NetworkManager>().Length;
#endif
                Check(managerCount == 1 && registry.Entries.Count == 1 && prefab.GetComponents<SocketJackObject>().Length == 1, "idempotent repeated setup");
                Check(prefab.GetComponent<SocketJackObject>().PrecisionPreset == SocketJack.Multiplayer.Serialization.NetworkPrecisionPreset.Compact, "existing precision choice preserved");
                var source2D = new GameObject("Existing 2D Player"); source2D.AddComponent<BoxCollider2D>();
                var body2D = source2D.AddComponent<Rigidbody2D>(); body2D.mass = 2.3f; body2D.gravityScale = 0;
                var prefab2D = PrefabUtility.SaveAsPrefabAsset(source2D, "Assets/InstallationFixture/Player2D.prefab"); UnityEngine.Object.DestroyImmediate(source2D);
                SocketJackSetupWizard.ApplyToScene(prefab2D, registry, true);
                Check(prefab2D.GetComponent<SocketJackObject2D>() != null && registry.TryGetId(prefab2D, out uint secondId) && secondId == 2 && registry.Entries.Count == 2, "2D setup appends stable ID without renumbering existing player");
                Check(Mathf.Approximately(prefab2D.GetComponent<Rigidbody2D>().mass, 2.3f) && prefab2D.GetComponent<Rigidbody2D>().gravityScale == 0 && prefab2D.GetComponent<Rigidbody>() == null, "2D physics preserved without adding 3D body");
                if (HasType("SocketJack.Multiplayer.Web.Components.SocketJackWebHost"))
                {
                    SocketJackSetupWizard.ApplyToScene(prefab2D, registry, true, additions: NetworkSetupFeatures.All);
                    Check(manager.GetComponent("SocketJackWebHost") != null && manager.GetComponent("SocketJackWebClientBehaviour") != null && manager.GetComponent("SocketJackMMOChatPanel") != null && manager.GetComponent("SocketJackWebSessionControls") != null, "optional account/site/chat and authenticated session menu installed");
                    Check(manager.GetComponent("SocketJackVoiceManager") != null && manager.GetComponent("SocketJackVoiceControls") != null && manager.GetComponent("SocketJackWebVoiceHost") != null && manager.GetComponent("SocketJackWebVoiceClient") != null, "optional account-backed voice helpers installed");
                    var voice = new SerializedObject(manager.GetComponent("SocketJackVoiceManager"));
                    Check(!voice.FindProperty("startMicrophone").boolValue, "new voice setup does not start microphone capture");
                    Check(prefab2D.GetComponent<SocketJackInterestObject>().ObserverAnchor && manager.GetComponent<SocketJackInterestManager>().Dimension == NetworkSpatialDimension.XY && prefab2D.GetComponent<SocketJackAdaptiveObject>() != null && manager.GetComponent<NetworkObjectPool>() != null, "2D spatial anchor, adaptive precision and pool installed");
                    manager.GetComponent<SocketJackInterestManager>().Radius = 123;
                    SocketJackSetupWizard.ApplyToScene(prefab2D, registry, true, additions: NetworkSetupFeatures.All);
                    Check(manager.GetComponent<SocketJackInterestManager>().Radius == 123 && manager.GetComponents<NetworkObjectPool>().Length == 1 && registry.Entries.Count == 2 && prefab.GetComponent<SocketJackObject>().PrecisionPreset == SocketJack.Multiplayer.Serialization.NetworkPrecisionPreset.Compact, "repeated full setup preserves settings, IDs and existing precision");
                }
                AssetDatabase.SaveAssets();
                string path = Argument("-socketjackReceipt"); if (string.IsNullOrEmpty(path)) throw new ArgumentException("Pass a private receipt path.");
                File.WriteAllText(path, JsonUtility.ToJson(new Receipt { result = "pass", checks = checks, editorVersion = Application.unityVersion }, true));
                Debug.Log("SOCKETJACK_INSTALLATION_ACCEPTANCE_PASS " + checks); EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }
        private static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks++; }
        private static bool HasType(string name) { foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) if (assembly.GetType(name, false) != null) return true; return false; }
        private static string Argument(string key) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : ""; }
    }
}
#endif
