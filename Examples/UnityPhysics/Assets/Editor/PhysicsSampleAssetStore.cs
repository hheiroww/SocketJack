using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace SocketJack.Publishing.Editor
{
    /// <summary>Explicit export and fresh-import acceptance entry points. This helper is kept outside the exported asset root.</summary>
    public static class PhysicsSampleAssetStore
    {
        private const string Root = "Assets/SocketJack.Unity";
        private const string DemoScene = Root + "/Samples/Physics/Scenes/SocketJackPhysics.unity";
        private const string ImportPending = "SocketJack.AssetStore.ImportPending";
        private const string ImportDeadline = "SocketJack.AssetStore.ImportDeadline";

        [Serializable] private sealed class Receipt
        {
            public string result, unityVersion, runtimeSha256, packageSha256, checkedUtc;
            public string[] assetPaths;
            public string[] compiledTypes;
            public int compiledAssemblies;
        }

        public static void Export() { ExportInternal(true); }
        public static void ReExportStaged() { ExportInternal(false); }
        private static void ExportInternal(bool prepareScene)
        {
            string package = RequiredArgument("-socketjackPackage");
            string manifest = RequiredArgument("-socketjackAssetManifest");
            if (prepareScene) PreparePackageScene();
            var assets = File.ReadAllLines(manifest).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();
            if (assets.Length < 10) throw new InvalidOperationException("The explicit asset manifest is incomplete.");
            foreach (string path in assets)
            {
                if ((path != Root && !path.StartsWith(Root + "/", StringComparison.Ordinal)) || path.Contains("..") || path.Contains('\\') ||
                    (!File.Exists(path) && !Directory.Exists(path))) throw new InvalidOperationException("Invalid export allowlist path: " + path);
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.ExportPackage(assets, package, ExportPackageOptions.Default);
            if (!File.Exists(package) || new FileInfo(package).Length == 0) throw new InvalidOperationException("Unity did not export the package.");
            WriteReceipt(new Receipt { result = "exported", assetPaths = assets, packageSha256 = Hash(package),
                runtimeSha256 = Hash(Root + "/Runtime/Plugins/SocketJack.Unity.dll") });
            Debug.Log("SOCKETJACK_ASSET_STORE_EXPORT_PASS assets=" + assets.Length);
        }

        public static void Import()
        {
            // ImportPackage is asynchronous. This entry point is launched without -quit and exits
            // only after Unity confirms that package files reached the project. A second launch compiles them.
            SessionState.SetBool(ImportPending, true);
            SessionState.SetString(ImportDeadline, (EditorApplication.timeSinceStartup + 180).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AssetDatabase.importPackageCompleted += packageName =>
            {
                // Importing scripts may reload the domain before this callback runs. The resumable
                // update check below verifies completion after a reload as well.
                CheckImportCompletion();
            };
            AssetDatabase.importPackageFailed += (packageName, error) =>
            {
                Debug.LogError("SocketJack package import failed: " + error);
                EditorApplication.Exit(1);
            };
            AssetDatabase.importPackageCancelled += packageName => EditorApplication.Exit(1);
            ResumeImport();
            AssetDatabase.ImportPackage(RequiredArgument("-socketjackPackage"), false);
        }

        [InitializeOnLoadMethod]
        private static void ResumeImport()
        {
            string[] args = Environment.GetCommandLineArgs();
            bool importCommand = Array.IndexOf(args, "SocketJack.Publishing.Editor.PhysicsSampleAssetStore.Import") >= 0;
            if (!importCommand && !SessionState.GetBool(ImportPending, false)) return;
            SessionState.SetBool(ImportPending, true);
            if (string.IsNullOrEmpty(SessionState.GetString(ImportDeadline, "")))
                SessionState.SetString(ImportDeadline, (EditorApplication.timeSinceStartup + 180).ToString(System.Globalization.CultureInfo.InvariantCulture));
            EditorApplication.update -= CheckImportCompletion;
            EditorApplication.update += CheckImportCompletion;
        }

        private static void CheckImportCompletion()
        {
            if (!SessionState.GetBool(ImportPending, false)) return;
            double deadline;
            double.TryParse(SessionState.GetString(ImportDeadline, "0"), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out deadline);
            if (EditorApplication.timeSinceStartup > deadline)
            { Debug.LogError("SocketJack package import timed out."); SessionState.SetBool(ImportPending, false); EditorApplication.Exit(1); return; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            if (EditorUtility.scriptCompilationFailed)
            { Debug.LogError("SocketJack package import has compilation errors."); SessionState.SetBool(ImportPending, false); EditorApplication.Exit(1); return; }
            string[] compiled;
            if (!File.Exists(Root + "/Runtime/Plugins/SocketJack.Unity.dll") || AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScene) == null || !TryGetCompiledTypes(out compiled)) return;
            SessionState.SetBool(ImportPending, false); SessionState.EraseString(ImportDeadline);
            Debug.Log("SOCKETJACK_ASSET_STORE_IMPORT_COMPLETED");
            EditorApplication.Exit(0);
        }

        public static void VerifyImport()
        {
            if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("Imported scripts have compilation errors.");
            string runtime = Root + "/Runtime/Plugins/SocketJack.Unity.dll";
            if (!File.Exists(runtime) || !File.Exists(Root + "/Documentation/QuickStart.md")) throw new InvalidOperationException("Required package files are missing.");
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(DemoScene) == null) throw new InvalidOperationException("The demo scene failed to import.");
            var scene = EditorSceneManager.OpenScene(DemoScene, OpenSceneMode.Single);
            Type game = DemoScriptType();
            if (!scene.GetRootGameObjects().Any(go => go.GetComponent(game) != null)) throw new InvalidOperationException("The imported scene has no working physics sample component.");
            bool loaded = AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "SocketJack.Unity");
            if (!loaded) throw new InvalidOperationException("The managed networking runtime did not load.");
            string[] compiled;
            if (!TryGetCompiledTypes(out compiled)) throw new InvalidOperationException("One or more imported component/sample types did not compile.");
            WriteReceipt(new Receipt { result = "fresh-import-compiled", runtimeSha256 = Hash(runtime),
                packageSha256 = Hash(RequiredArgument("-socketjackPackage")),
                assetPaths = AssetDatabase.GetAllAssetPaths().Where(path => path.StartsWith(Root + "/", StringComparison.Ordinal)).OrderBy(path => path).ToArray(),
                compiledTypes = compiled,
                compiledAssemblies = CompilationPipeline.GetAssemblies().Length });
            Debug.Log("SOCKETJACK_ASSET_STORE_FRESH_IMPORT_PASS");
        }

        private static void PreparePackageScene()
        {
            Directory.CreateDirectory(Root + "/Samples/Physics/Scenes");
            Directory.CreateDirectory(Root + "/Samples/Physics/Resources");
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            string materialPath = Root + "/Samples/Physics/Resources/SocketJackPhysicsStandard.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(materialPath) == null)
            {
                var shader = Shader.Find("Standard");
                if (shader == null) throw new InvalidOperationException("The Built-in Standard shader is required for the physics demo.");
                AssetDatabase.CreateAsset(new Material(shader), materialPath);
            }
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            new GameObject("SocketJack Physics Sample").AddComponent(DemoScriptType());
            if (!EditorSceneManager.SaveScene(scene, DemoScene)) throw new InvalidOperationException("Could not save the demo scene.");
            AssetDatabase.SaveAssets();
        }

        private static Type DemoScriptType()
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Samples/Physics/Scripts/PhysicsGame.cs");
            Type type = script == null ? null : script.GetClass();
            if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type)) throw new InvalidOperationException("PhysicsGame did not compile as a MonoBehaviour.");
            return type;
        }
        private static bool TryGetCompiledTypes(out string[] types)
        {
            var compiled = new System.Collections.Generic.List<string>();
            foreach (string name in new[] { "NetworkManager", "NetworkIdentity", "NetworkTransform", "SocketJackObject", "SocketJackMemberSync", "ExamplePlayerController" })
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Runtime/Components/" + name + ".cs");
                Type type = script == null ? null : script.GetClass();
                if (type == null || !typeof(MonoBehaviour).IsAssignableFrom(type)) { types = Array.Empty<string>(); return false; }
                compiled.Add(type.FullName);
            }
            var game = AssetDatabase.LoadAssetAtPath<MonoScript>(Root + "/Samples/Physics/Scripts/PhysicsGame.cs");
            Type gameType = game == null ? null : game.GetClass();
            if (gameType == null || !typeof(MonoBehaviour).IsAssignableFrom(gameType)) { types = Array.Empty<string>(); return false; }
            compiled.Add(gameType.FullName); types = compiled.ToArray(); return true;
        }
        private static void WriteReceipt(Receipt receipt)
        {
            receipt.unityVersion = Application.unityVersion; receipt.checkedUtc = DateTime.UtcNow.ToString("O");
            File.WriteAllText(RequiredArgument("-socketjackReceipt"), JsonUtility.ToJson(receipt, true));
        }
        private static string RequiredArgument(string key)
        {
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArgumentException("Missing argument " + key);
            return args[index + 1];
        }
        private static string Hash(string path)
        { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
    }
}
