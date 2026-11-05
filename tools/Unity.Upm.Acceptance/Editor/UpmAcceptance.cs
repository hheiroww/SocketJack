#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.UI;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SocketJack.Acceptance.Upm.Editor
{
    /// <summary>Exercises Package Manager's tarball installation and sample importer, then verifies the
    /// compiled package in isolation. No source checkout, package cache, or package contents are changed.</summary>
    public static class UpmAcceptance
    {
        private const string PackageName = "com.socketjack.multiplayer";
        private const string PackageRoot = "Packages/" + PackageName;
        private const string Pending = "SocketJack.UpmAcceptance.SamplesPending";
        private const string Deadline = "SocketJack.UpmAcceptance.Deadline";
        private const string SamplePaths = "SocketJack.UpmAcceptance.SamplePaths";
        private const string Compiled = "SocketJack.UpmAcceptance.SourcesCompiled";
        [Serializable] private sealed class Definition { public string name = ""; public string[] includePlatforms = Array.Empty<string>(), excludePlatforms = Array.Empty<string>(); }
        [Serializable] private sealed class PackageMetadata { public string name = "", version = "", socketjackRuntimeSha256 = ""; }
        [Serializable] private sealed class Receipt
        {
            public string result, phase, editorVersion, packageVersion, packageSource, packageSha256, runtimeSha256, checkedUtc;
            public int checks;
            public string[] assemblies, samplePaths, scenes;
        }
        private static int checks;

        public static void ImportSamples()
        {
            try
            {
                VerifyPackage();
                SessionState.SetBool(Pending, true);
                SessionState.SetString(Deadline, (EditorApplication.timeSinceStartup + 240).ToString(System.Globalization.CultureInfo.InvariantCulture));
                var samples = Sample.FindByPackage(PackageName, Required("-socketjackVersion")).ToArray();
                Check(samples.Length == 2, "Both public sample definitions are available through Package Manager.");
                SessionState.SetString(SamplePaths, string.Join("\n", samples.Select(sample => sample.importPath)));
                foreach (var sample in samples)
                {
                    // Keep matching imports. A new reviewed tarball may retain the same package version;
                    // refresh differing sample sources through Package Manager in this isolated owned
                    // project, so a cached earlier sample cannot make a later package appear to pass.
                    bool refresh = sample.isImported && !SampleMatches(sample);
                    string target = Path.GetFullPath(sample.importPath);
                    if (!target.StartsWith(Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Sample import is outside the owned project's Assets directory.");
                    if ((!sample.isImported || refresh) && !sample.Import(refresh ? Sample.ImportOptions.OverridePreviousImports : Sample.ImportOptions.None))
                        throw new InvalidOperationException("Package Manager could not import sample: " + sample.displayName);
                }
                // Reused projects can retain script-to-asset registrations when Package Manager's
                // sample folder changes. Rebuild through Unity's supported compilation API rather
                // than editing/deleting its caches or accepting stale assemblies.
                SessionState.SetBool(Compiled, false);
                CompilationPipeline.compilationFinished -= OnCompilationFinished;
                CompilationPipeline.compilationFinished += OnCompilationFinished;
                CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);
                ResumeImport();
            }
            catch (Exception error) { Fail(error); }
        }
        private static void OnCompilationFinished(object context) { SessionState.SetBool(Compiled, true); }
        [InitializeOnLoadMethod]
        private static void ResumeImport()
        {
            if (!SessionState.GetBool(Pending, false)) return;
            EditorApplication.update -= CheckSampleImport;
            EditorApplication.update += CheckSampleImport;
        }
        private static void CheckSampleImport()
        {
            try
            {
                if (!SessionState.GetBool(Pending, false)) { EditorApplication.update -= CheckSampleImport; return; }
                double end; double.TryParse(SessionState.GetString(Deadline, "0"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out end);
                if (EditorApplication.timeSinceStartup > end) throw new TimeoutException("Package Manager sample import did not finish within four minutes.");
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                if (!SessionState.GetBool(Compiled, false)) return;
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("Imported samples have script compilation errors.");
                if (FindType("SocketJack.Samples.Physics.PhysicsGame") == null || FindType("SocketJack.Samples.Multiplayer.MultiplayerGame") == null) return;
                SessionState.SetBool(Pending, false); EditorApplication.update -= CheckSampleImport;
                WriteReceipt("samples-imported", Array.Empty<string>(), Array.Empty<string>());
                Debug.Log("SOCKETJACK_UPM_SAMPLES_IMPORTED"); EditorApplication.Exit(0);
            }
            catch (Exception error) { Fail(error); }
        }
        public static void VerifyAndCreateScenes()
        {
            try
            {
                VerifyPackage();
                if (EditorUtility.scriptCompilationFailed) throw new InvalidOperationException("The isolated project has script compilation errors.");
                Check(Sample.FindByPackage(PackageName, Required("-socketjackVersion")).Count(sample => sample.isImported) == 2, "Both samples remain imported after a fresh Editor launch.");
                foreach (var sample in Sample.FindByPackage(PackageName, Required("-socketjackVersion"))) Check(SampleMatches(sample), "Imported sample sources match this exact installed tarball: " + sample.displayName);
                // Unity2022 can leave a null MonoScript class registration after sample folders move
                // during a package display-name change. Reimport the task-owned sample assets through
                // the public API; compilation alone does not rebuild those importer registrations.
                foreach (var sample in Sample.FindByPackage(PackageName, Required("-socketjackVersion")))
                    foreach (string file in Directory.GetFiles(sample.importPath, "*.cs", SearchOption.AllDirectories))
                    {
                        string path = "Assets/" + Path.GetRelativePath(Application.dataPath, file).Replace('\\', '/');
                        var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);
                        if (script == null || script.GetClass() == null) AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        Check(AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass() != null, "Imported sample script is bound to its asset: " + path);
                    }
                var expectedAssemblies = new[] {
                    "SocketJack.Unity", "SocketJack.Unity.Web.Client", "SocketJack.Unity.Web", "SocketJack.Unity.Voice", "SocketJack.Unity.Voice.Web.Client", "SocketJack.Unity.Voice.Web",
                    "SocketJack.Multiplayer.Components", "SocketJack.Multiplayer.Editor", "SocketJack.Multiplayer.Web.Components", "SocketJack.Multiplayer.Web.NativeHost", "SocketJack.Multiplayer.Web.Editor",
                    "SocketJack.Multiplayer.Voice.Components", "SocketJack.Multiplayer.Voice.Web.Components", "SocketJack.Multiplayer.Voice.Web.NativeHost",
                    "SocketJack.Samples.Physics", "SocketJack.Samples.Physics.Editor", "SocketJack.Samples.Multiplayer", "SocketJack.Samples.Multiplayer.Editor"
                };
                var loaded = new HashSet<string>(AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name), StringComparer.Ordinal);
                foreach (string name in expectedAssemblies) Check(loaded.Contains(name), "Compiled assembly: " + name);
                foreach (string name in new[] { "ExamplePlayerController", "INetworkMemberSource", "INetworkStateController", "NetworkBehaviour", "NetworkIdentity", "NetworkInputBridge", "NetworkLocalPlayer", "NetworkManager", "NetworkObjectPool", "NetworkPlayerSpawner", "NetworkPrecisionAsset", "NetworkPrefabRegistry", "NetworkSceneCoordinator", "NetworkSceneObject", "NetworkSessionControls", "NetworkSpawnPoint", "NetworkTransform", "SocketJackAnimator", "SocketJackAdaptiveNetworking", "SocketJackAdaptiveObject", "SocketJackBrowserTransport", "SocketJackCharacterMotor", "SocketJackDoor", "SocketJackHealth", "SocketJackInterestManager", "SocketJackInterestObject", "SocketJackMemberSync", "SocketJackNetworkEvent", "SocketJackObject", "SocketJackObject2D", "SocketJackPickup", "SocketJackProjectile" })
                    RequireType("SocketJack.Multiplayer.Components." + name, "SocketJack.Multiplayer.Components");
                foreach (string name in new[] { "SocketJackWebClientBehaviour", "SocketJackWebSessionControls", "SocketJackMMOChatPanel", "SocketJackSocialPanel", "SocketJackChatPreset" })
                    RequireType("SocketJack.Multiplayer.Web.Components." + name, "SocketJack.Multiplayer.Web.Components");
                RequireType("SocketJack.Multiplayer.Web.Components.SocketJackWebHost", "SocketJack.Multiplayer.Web.NativeHost");
                foreach (string name in new[] { "SocketJackVoiceManager", "SocketJackVoiceControls", "SocketJackBrowserMicrophone" })
                    RequireType("SocketJack.Multiplayer.Voice.Components." + name, "SocketJack.Multiplayer.Voice.Components");
                foreach (string name in new[] { "SocketJackWebVoiceClient", "SocketJackBrowserVoiceSocket" })
                    RequireType("SocketJack.Multiplayer.Voice.Web.Components." + name, "SocketJack.Multiplayer.Voice.Web.Components");
                RequireType("SocketJack.Multiplayer.Voice.Web.Components.SocketJackWebVoiceHost", "SocketJack.Multiplayer.Voice.Web.NativeHost");
                RequireType("SocketJack.Multiplayer.Components.Editor.SocketJackSetupWizard", "SocketJack.Multiplayer.Editor");
                VerifyBrowserExclusions();
                var sources = CompilationPipeline.GetAssemblies(AssembliesType.Editor).Where(assembly => expectedAssemblies.Contains(assembly.name)).ToArray();
                Check(sources.Any(assembly => assembly.name == "SocketJack.Multiplayer.Components" && assembly.sourceFiles.Any(file => file.Replace('\\', '/').Contains("com.socketjack.multiplayer"))), "Base components compile from the installed package.");
                Invoke("SocketJack.Samples.Physics.Editor.PhysicsSampleBuild", "PrepareScene");
                CheckScene("Assets/Scenes/Physics.unity", "SocketJack.Samples.Physics.PhysicsGame", null);
                string completeRoot = "Assets/UpmAcceptance/Complete-" + Required("-socketjackPackageHash").Substring(0, 12);
                Invoke("SocketJack.Samples.Multiplayer.Editor.MultiplayerSampleBuild", "PrepareScenes", completeRoot);
                CheckScene(completeRoot + "/Complete3D.unity", "SocketJack.Samples.Multiplayer.MultiplayerGame", false);
                CheckScene(completeRoot + "/Complete2D.unity", "SocketJack.Samples.Multiplayer.MultiplayerGame", true);
                VerifyFullSetup();
                AssetDatabase.SaveAssets();
                WriteReceipt("verified", expectedAssemblies, new[] { "Assets/Scenes/Physics.unity", completeRoot + "/Complete3D.unity", completeRoot + "/Complete2D.unity", "Assets/UpmAcceptance/FullSetup.unity" });
                Debug.Log("SOCKETJACK_UPM_ACCEPTANCE_PASS " + checks); EditorApplication.Exit(0);
            }
            catch (Exception error) { Fail(error); }
        }
        private static PackageInfo VerifyPackage()
        {
            var info = PackageInfo.FindForAssetPath(PackageRoot + "/package.json");
            Check(info != null && info.name == PackageName && info.version == Required("-socketjackVersion"), "Package Manager resolved the expected package/version.");
            Check(info.source == PackageSource.LocalTarball, "Package Manager installed an actual local tarball.");
            var metadata = JsonUtility.FromJson<PackageMetadata>(File.ReadAllText(Path.Combine(info.resolvedPath, "package.json")));
            Check(metadata.name == PackageName && metadata.version == info.version && metadata.socketjackRuntimeSha256 == Required("-socketjackRuntimeHash"), "Installed manifest declares the reviewed runtime hash.");
            Check(Hash(Path.Combine(info.resolvedPath, "Runtime/Plugins/SocketJack.Unity.dll")) == Required("-socketjackRuntimeHash"), "Installed runtime bytes match the reviewed DLL.");
            Check(Hash(Required("-socketjackPackage")) == Required("-socketjackPackageHash"), "Input tarball remains unchanged.");
            return info;
        }
        private static bool SampleMatches(Sample sample)
        {
            if (!Directory.Exists(sample.resolvedPath) || !Directory.Exists(sample.importPath)) return false;
            var files = Directory.GetFiles(sample.resolvedPath, "*", SearchOption.AllDirectories).Where(path => !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (files.Length == 0) return false;
            foreach (string file in files)
            {
                string target = Path.Combine(sample.importPath, Path.GetRelativePath(sample.resolvedPath, file));
                if (!File.Exists(target) || Hash(file) != Hash(target)) return false;
            }
            return true;
        }
        private static void VerifyBrowserExclusions()
        {
            foreach (string name in new[] { "SocketJack.Unity.Web.dll", "SocketJack.Unity.Voice.Web.dll" })
            {
                var importer = AssetImporter.GetAtPath(PackageRoot + "/Optional/Plugins/" + name) as PluginImporter;
                string compatibility = importer == null ? "missing importer" : "Editor=" + EffectiveEditor(importer) + ", Any=" + importer.GetCompatibleWithAnyPlatform() + ", WebGL=" + EffectivePlatform(importer, BuildTarget.WebGL);
                Check(importer != null && EffectiveEditor(importer) && !EffectivePlatform(importer, BuildTarget.WebGL), "Native server plugin excludes WebGL: " + name + " (" + compatibility + ")");
                Check(EffectivePlatform(importer, BuildTarget.StandaloneWindows64) && EffectivePlatform(importer, BuildTarget.StandaloneLinux64), "Native server plugin includes supported Windows and Linux hosts: " + name);
            }
            foreach (string name in new[] { "SocketJack.Unity.Web.Client.dll", "SocketJack.Unity.Voice.dll", "SocketJack.Unity.Voice.Web.Client.dll" })
            {
                var importer = AssetImporter.GetAtPath(PackageRoot + "/Optional/Plugins/" + name) as PluginImporter;
                Check(importer != null && EffectivePlatform(importer, BuildTarget.WebGL), "Client plugin remains available on WebGL: " + name);
            }
            foreach (string relative in new[] { "Optional/Web/NativeHost/SocketJack.Multiplayer.Web.NativeHost.asmdef", "Optional/VoiceWeb/NativeHost/SocketJack.Multiplayer.Voice.Web.NativeHost.asmdef" })
            {
                var definition = JsonUtility.FromJson<Definition>(File.ReadAllText(PackageRoot + "/" + relative));
                Check(ExcludesWebGl(definition), "Native source assembly excludes WebGL: " + definition.name);
            }
            var complete = Sample.FindByPackage(PackageName, Required("-socketjackVersion")).First(sample => File.Exists(Path.Combine(sample.resolvedPath, "Scripts/SocketJack.Samples.Multiplayer.asmdef")));
            string completeDefinitionPath = Path.Combine(complete.resolvedPath, "Scripts/SocketJack.Samples.Multiplayer.asmdef");
            var completeDefinition = JsonUtility.FromJson<Definition>(File.ReadAllText(completeDefinitionPath));
            Check(ExcludesWebGl(completeDefinition), "Native complete sample source assembly excludes WebGL.");
        }
        private static bool ExcludesWebGl(Definition definition) => definition.includePlatforms != null && definition.includePlatforms.Length > 0 ? !definition.includePlatforms.Contains("WebGL") : definition.excludePlatforms != null && definition.excludePlatforms.Contains("WebGL");
        // Unity exposes the per-platform flags and Any Platform's exclusions separately. Evaluate
        // whichever policy is active, just as the Plugin Inspector does.
        private static bool EffectiveEditor(PluginImporter importer) => importer.GetCompatibleWithAnyPlatform() ? !importer.GetExcludeEditorFromAnyPlatform() : importer.GetCompatibleWithEditor();
        private static bool EffectivePlatform(PluginImporter importer, BuildTarget target) => importer.GetCompatibleWithAnyPlatform() ? !importer.GetExcludeFromAnyPlatform(target) : importer.GetCompatibleWithPlatform(target);
        private static void CheckScene(string path, string typeName, bool? twoD)
        {
            Check(AssetDatabase.LoadAssetAtPath<SceneAsset>(path) != null, "Created scene asset: " + path);
            var scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Single); Type type = FindType(typeName);
            var component = scene.GetRootGameObjects().Select(root => root.GetComponent(type)).FirstOrDefault(value => value != null);
            Check(component != null, "Scene contains its compiled sample controller: " + typeName);
            if (twoD.HasValue) Check((bool)type.GetField("Use2D").GetValue(component) == twoD.Value && type.GetField("MaterialTemplate").GetValue(component) != null, "Complete sample has the correct dimension and a retained material.");
        }
        private static void VerifyFullSetup()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var player = GameObject.CreatePrimitive(PrimitiveType.Cube); player.name = "Existing single-player cube";
            player.AddComponent<Rigidbody>().mass = 3.7f;
            var prefab = PrefabUtility.SaveAsPrefabAsset(player, "Assets/UpmAcceptance/Player.prefab"); UnityEngine.Object.DestroyImmediate(player);
            var registry = ScriptableObject.CreateInstance(FindType("SocketJack.Multiplayer.Components.NetworkPrefabRegistry"));
            AssetDatabase.CreateAsset(registry, "Assets/UpmAcceptance/NetworkPrefabs.asset");
            var method = FindType("SocketJack.Multiplayer.Components.Editor.SocketJackSetupWizard").GetMethod("ApplyToScene", BindingFlags.Public | BindingFlags.Static);
            var parameters = method.GetParameters();
            method.Invoke(null, new object[] { prefab, registry, false, Enum.Parse(parameters[3].ParameterType, "HostPeer"), Enum.Parse(parameters[4].ParameterType, "Udp"), Enum.Parse(parameters[5].ParameterType, "All") });
            var managerType = FindType("SocketJack.Multiplayer.Components.NetworkManager");
            var manager = scene.GetRootGameObjects().Select(root => root.GetComponent(managerType)).FirstOrDefault(value => value != null);
            Check(manager != null && prefab.GetComponent("SocketJackObject") != null && Mathf.Approximately(prefab.GetComponent<Rigidbody>().mass, 3.7f), "Installed wizard converts a prefab while preserving native physics.");
            foreach (string name in new[] { "SocketJackWebHost", "SocketJackWebClientBehaviour", "SocketJackMMOChatPanel", "SocketJackWebSessionControls", "SocketJackVoiceManager", "SocketJackVoiceControls", "SocketJackBrowserMicrophone", "SocketJackWebVoiceHost", "SocketJackWebVoiceClient", "SocketJackBrowserVoiceSocket", "SocketJackInterestManager", "SocketJackAdaptiveNetworking", "NetworkObjectPool" })
                Check(manager.GetComponent(name) != null, "Full installed wizard configures: " + name);
            Check(!new SerializedObject(manager.GetComponent("SocketJackVoiceManager")).FindProperty("startMicrophone").boolValue, "Full setup leaves microphone capture off.");
            Check(EditorSceneManager.SaveScene(scene, "Assets/UpmAcceptance/FullSetup.unity"), "Full setup scene is saved.");
            scene = EditorSceneManager.OpenScene("Assets/UpmAcceptance/FullSetup.unity", OpenSceneMode.Single);
            manager = scene.GetRootGameObjects().Select(root => root.GetComponent(managerType)).FirstOrDefault(value => value != null);
            Check(manager != null && manager.GetComponent("SocketJackWebHost") != null && manager.GetComponent("SocketJackBrowserMicrophone") != null && manager.GetComponent("SocketJackBrowserVoiceSocket") != null, "Full setup retains serialized base and optional components after reopening.");
        }
        private static Type FindType(string name) { foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) { var type = assembly.GetType(name, false); if (type != null) return type; } return null; }
        private static void RequireType(string name, string assemblyName) { var type = FindType(name); Check(type != null && type.Assembly.GetName().Name == assemblyName, "Compiled type has the correct assembly: " + name); }
        private static void Invoke(string typeName, string method, params object[] arguments)
        { var type = FindType(typeName) ?? throw new InvalidOperationException("Missing imported sample type: " + typeName); type.GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, arguments); }
        private static void WriteReceipt(string phase, string[] assemblies, string[] scenes)
        {
            var info = VerifyPackage();
            File.WriteAllText(Required("-socketjackReceipt"), JsonUtility.ToJson(new Receipt { result = "pass", phase = phase, editorVersion = Application.unityVersion, packageVersion = info.version, packageSource = info.source.ToString(), packageSha256 = Required("-socketjackPackageHash"), runtimeSha256 = Required("-socketjackRuntimeHash"), checkedUtc = DateTime.UtcNow.ToString("O"), checks = checks, assemblies = assemblies, samplePaths = Sample.FindByPackage(PackageName, info.version).Where(sample => sample.isImported).Select(sample => sample.importPath).ToArray(), scenes = scenes }, true));
        }
        private static string Required(string key) { var args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, key); if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1])) throw new ArgumentException("Missing acceptance argument: " + key); return args[index + 1]; }
        private static string Hash(string path) { using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
        private static void Fail(Exception error) { SessionState.SetBool(Pending, false); EditorApplication.update -= CheckSampleImport; Debug.LogException(error); EditorApplication.Exit(1); }
    }
}
#endif
