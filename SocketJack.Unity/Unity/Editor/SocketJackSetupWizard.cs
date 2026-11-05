#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using SocketJack.Multiplayer.Transport;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SocketJack.Multiplayer.Components.Editor
{
    [Flags]
    public enum NetworkSetupFeatures { None = 0, AccountsAndChat = 1, Voice = 2, SpatialInterest = 4, AdaptivePrecision = 8, Pooling = 16, All = 31 }
    /// <summary>Explicit, undoable scene setup. Opening the window never changes a project.</summary>
    public sealed class SocketJackSetupWizard : EditorWindow
    {
        private enum Movement { Rigidbody3D, Rigidbody2D, ExistingController }
        private GameObject playerPrefab;
        private NetworkPrefabRegistry registry;
        private NetworkTopology topology = NetworkTopology.HostPeer;
        private TransportType transport = TransportType.Udp;
        private Movement movement;
        private string address = "127.0.0.1";
        private int port = 7777;
        private bool includeSpawnPoint = true;
        private NetworkSetupFeatures features = NetworkSetupFeatures.All;
        private Vector2 scroll;
        private string result = "";
        private readonly List<string> findings = new List<string>();

        [MenuItem("Tools/SocketJack/Setup Multiplayer", priority = 0)]
        public static void Open() => GetWindow<SocketJackSetupWizard>("SocketJack Setup");
        private void OnEnable()
        {
            var managers = SceneComponents<NetworkManager>();
            if (managers.Count != 1) return;
            topology = managers[0].Topology; transport = managers[0].Transport;
            address = managers[0].Address; port = managers[0].Port;
            var spawner = managers[0].GetComponent<NetworkPlayerSpawner>();
            if (spawner != null)
            {
                var settings = new SerializedObject(spawner);
                registry = settings.FindProperty("registry")?.objectReferenceValue as NetworkPrefabRegistry;
            }
        }
        /// <summary>Apply an explicitly supplied prefab/registry to the open scene, without file dialogs.</summary>
        public static void ApplyToScene(GameObject prefab, NetworkPrefabRegistry prefabRegistry, bool physics2D = false,
            NetworkTopology sessionTopology = NetworkTopology.HostPeer, TransportType sessionTransport = TransportType.Udp,
            NetworkSetupFeatures additions = NetworkSetupFeatures.None)
        {
            if (prefabRegistry == null) throw new ArgumentNullException(nameof(prefabRegistry));
            var wizard = CreateInstance<SocketJackSetupWizard>();
            try
            {
                wizard.playerPrefab = prefab; wizard.registry = prefabRegistry;
                wizard.movement = physics2D ? Movement.Rigidbody2D : Movement.Rigidbody3D;
                wizard.topology = sessionTopology; wizard.transport = sessionTransport;
                wizard.features = additions;
                wizard.ApplySetup();
                if (!wizard.result.StartsWith("Setup complete.", StringComparison.Ordinal))
                    throw new InvalidOperationException(wizard.result.Length > 0 ? wizard.result : string.Join("; ", wizard.findings));
            }
            finally { DestroyImmediate(wizard); }
        }

        private void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Make your game multiplayer", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Choose an existing player prefab. Setup adds connection, spawning and local-player helpers; your game keeps its levels and rules. Nothing changes until Apply setup.", MessageType.Info);
            playerPrefab = (GameObject)EditorGUILayout.ObjectField("Player prefab", playerPrefab, typeof(GameObject), false);
            registry = (NetworkPrefabRegistry)EditorGUILayout.ObjectField("Prefab registry", registry, typeof(NetworkPrefabRegistry), false);
            movement = (Movement)EditorGUILayout.EnumPopup("Movement", movement);
            topology = (NetworkTopology)EditorGUILayout.EnumPopup("Topology", topology);
            transport = (TransportType)EditorGUILayout.EnumPopup("Transport", transport);
            address = EditorGUILayout.TextField("Join address", address);
            port = EditorGUILayout.IntField("Port", port);
            includeSpawnPoint = EditorGUILayout.Toggle("Create spawn point", includeSpawnPoint);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Optional additions", EditorStyles.boldLabel);
            FeatureToggle("MMO chat, accounts and website", NetworkSetupFeatures.AccountsAndChat);
            FeatureToggle("Account-backed voice (microphone off)", NetworkSetupFeatures.Voice);
            FeatureToggle("Spatial interest / nearby visibility", NetworkSetupFeatures.SpatialInterest);
            FeatureToggle("Adaptive precision and send rate", NetworkSetupFeatures.AdaptivePrecision);
            FeatureToggle("Bounded prefab pooling", NetworkSetupFeatures.Pooling);
            EditorGUILayout.HelpBox("New additions use safe local defaults. Existing components, chat presets, visibility limits and microphone settings are preserved. A remote account website needs your own HTTPS certificate and public bind configuration.", MessageType.Info);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Changes to review", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("• Create or configure one scene NetworkManager.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("• Add stable prefab registration, player spawning and menu actions.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("• Add identity, selected movement and a local-player camera/audio gate to the prefab.", EditorStyles.wordWrappedLabel);
            EditorGUILayout.LabelField("• Keep your input, renderer, physics and project settings. Existing movement code is not disabled automatically.", EditorStyles.wordWrappedLabel);
            if (Has(NetworkSetupFeatures.AccountsAndChat)) EditorGUILayout.LabelField("• Add an account-aware Host / Join menu, MMO chat panel, and a local website on game port + 1. Sign in before joining the game.", EditorStyles.wordWrappedLabel);
            if (Has(NetworkSetupFeatures.Voice)) EditorGUILayout.LabelField("• Add permission-checked game and account voice routing with opt-in push-to-talk controls. Capture remains off.", EditorStyles.wordWrappedLabel);
            if (movement == Movement.ExistingController)
                EditorGUILayout.HelpBox("Existing-controller setup adds NetworkTransform for state replication. Integrate prediction/game rules through the motor APIs; it does not translate arbitrary controller behavior.", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (GUILayout.Button("Validate project")) ValidateConfiguration();
                if (GUILayout.Button("Apply setup")) ApplySetup();
            }
            foreach (var finding in findings) EditorGUILayout.HelpBox(finding, MessageType.Warning);
            if (!string.IsNullOrEmpty(result)) EditorGUILayout.HelpBox(result, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private bool ValidateConfiguration()
        {
            findings.Clear(); result = "";
            if (playerPrefab == null || !PrefabUtility.IsPartOfPrefabAsset(playerPrefab)) findings.Add("Select a player prefab asset, not a scene instance.");
            if (port < 1 || port > 65535) findings.Add("Port must be between 1 and 65535.");
            if (Has(NetworkSetupFeatures.AccountsAndChat) && port > 65533) findings.Add("Accounts need game port + 1 for the website and game port + 2 for the optional relay. Choose a game port up to 65533.");
            if (Has(NetworkSetupFeatures.Voice) && !Has(NetworkSetupFeatures.AccountsAndChat)) findings.Add("Select MMO chat/accounts for account-backed voice, or configure a trusted voice membership provider manually after setup.");
            if (Has(NetworkSetupFeatures.AccountsAndChat)) RequireOptionalTypes("SocketJack.Unity.Web and SocketJack.Unity.Web.Client", WebTypes);
            if (Has(NetworkSetupFeatures.Voice)) RequireOptionalTypes("SocketJack.Unity.Voice, SocketJack.Unity.Voice.Web and SocketJack.Unity.Voice.Web.Client", VoiceTypes);
            if (Has(NetworkSetupFeatures.SpatialInterest)) RequireOptionalTypes("the matching SocketJack.Unity components", new[] { Components + "SocketJackInterestManager", Components + "SocketJackInterestObject" });
            if (Has(NetworkSetupFeatures.AdaptivePrecision)) RequireOptionalTypes("the matching SocketJack.Unity components", new[] { Components + "SocketJackAdaptiveNetworking", Components + "SocketJackAdaptiveObject" });
            if (Has(NetworkSetupFeatures.Pooling)) RequireOptionalTypes("the matching SocketJack.Unity components", new[] { Components + "NetworkObjectPool" });
            if (transport != TransportType.Udp && transport != TransportType.Tcp) findings.Add("This setup supports the built-in UDP or TCP transport.");
            var managers = SceneComponents<NetworkManager>();
            if (managers.Count > 1) findings.Add("The open scene contains multiple NetworkManagers. Resolve them before setup.");
            if (movement == Movement.Rigidbody2D && FindComponentType("SocketJack.Multiplayer.Components.SocketJackObject2D") == null)
                findings.Add("Rigidbody2D support is not present. Import the complete matching SocketJack version.");
            if (playerPrefab != null)
            {
                bool body3D = playerPrefab.GetComponent<Rigidbody>() != null;
                bool body2D = playerPrefab.GetComponent("Rigidbody2D") != null;
                bool motor3D = playerPrefab.GetComponent<SocketJackObject>() != null;
                bool motor2D = playerPrefab.GetComponent("SocketJackObject2D") != null;
                bool transformSync = playerPrefab.GetComponent<NetworkTransform>() != null;
                if ((movement == Movement.Rigidbody3D && (body2D || motor2D || transformSync)) ||
                    (movement == Movement.Rigidbody2D && (body3D || motor3D || transformSync)) ||
                    (movement == Movement.ExistingController && (motor3D || motor2D)))
                    findings.Add("The prefab has a competing body or movement synchronizer. Choose its existing movement path or adjust it manually.");
                if ((motor3D ? 1 : 0) + (motor2D ? 1 : 0) + (transformSync ? 1 : 0) > 1)
                    findings.Add("Only one network movement synchronizer may control a prefab.");
            }
            if (registry != null)
            {
                try { registry.BuildPrefabArray(); }
                catch (Exception error) { findings.Add("Registry: " + error.Message); }
            }
            if (findings.Count == 0) result = "Configuration is ready. Apply setup records scene, prefab and registry changes in Undo.";
            return findings.Count == 0;
        }

        private void ApplySetup()
        {
            if (!ValidateConfiguration()) return;
            string registryPath = null;
            if (registry == null)
            {
                registryPath = EditorUtility.SaveFilePanelInProject("Save network prefab registry", "NetworkPrefabs", "asset", "Choose the registry asset location.");
                if (string.IsNullOrEmpty(registryPath)) return;
                if (AssetDatabase.LoadMainAssetAtPath(registryPath) != null) { result = "Choose an unused registry path, or select the existing registry above. Existing assets are preserved."; return; }
            }
            if (!SceneManager.GetActiveScene().IsValid()) { result = "Open a valid scene before applying setup."; return; }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Set up SocketJack multiplayer");
            try
            {
                // Modify the actual prefab asset so every server/client instantiates the same schema.
                Undo.RegisterFullObjectHierarchyUndo(playerPrefab, "Configure SocketJack player prefab");
                if (playerPrefab.GetComponent<NetworkIdentity>() == null) Undo.AddComponent<NetworkIdentity>(playerPrefab);
                Component newMotor = null;
                if (movement == Movement.Rigidbody3D && playerPrefab.GetComponent<SocketJackObject>() == null)
                    newMotor = Undo.AddComponent<SocketJackObject>(playerPrefab);
                else if (movement == Movement.Rigidbody2D && playerPrefab.GetComponent("SocketJackObject2D") == null)
                    newMotor = Undo.AddComponent(playerPrefab, FindComponentType("SocketJack.Multiplayer.Components.SocketJackObject2D"));
                else if (movement == Movement.ExistingController && playerPrefab.GetComponent<NetworkTransform>() == null)
                    newMotor = Undo.AddComponent<NetworkTransform>(playerPrefab);
                // New configurations use Balanced; existing prefabs retain their deliberately chosen precision.
                if (newMotor != null)
                {
                    var precision = newMotor.GetType().GetProperty("PrecisionPreset");
                    if (precision != null && precision.CanWrite && precision.PropertyType.IsEnum)
                        precision.SetValue(newMotor, Enum.Parse(precision.PropertyType, "Balanced"), null);
                }
                if (Has(NetworkSetupFeatures.AdaptivePrecision)) AddIfMissing(playerPrefab, Components + "SocketJackAdaptiveObject", out _);
                if (Has(NetworkSetupFeatures.SpatialInterest))
                {
                    var marker = AddIfMissing(playerPrefab, Components + "SocketJackInterestObject", out bool createdMarker);
                    if (createdMarker) SetSerialized(marker, "ObserverAnchor", true);
                }
                var local = playerPrefab.GetComponent<NetworkLocalPlayer>() ?? Undo.AddComponent<NetworkLocalPlayer>(playerPrefab);
                var targets = new List<Behaviour>();
                foreach (var component in playerPrefab.GetComponentsInChildren<Behaviour>(true))
                    if (component != null && (component is Camera || component.GetType().FullName == "UnityEngine.AudioListener")) targets.Add(component);
                Undo.RecordObject(local, "Configure local cameras and audio");
                local.Configure(targets.ToArray());
                EditorUtility.SetDirty(local); EditorUtility.SetDirty(playerPrefab);
                PrefabUtility.SavePrefabAsset(playerPrefab);

                var managers = SceneComponents<NetworkManager>();
                NetworkManager manager;
                if (managers.Count == 0)
                {
                    var root = new GameObject("SocketJack Multiplayer"); Undo.RegisterCreatedObjectUndo(root, "Create multiplayer manager");
                    manager = Undo.AddComponent<NetworkManager>(root);
                }
                else manager = managers[0];
                var serializedManager = new SerializedObject(manager);
                if (registry == null)
                {
                    registry = CreateInstance<NetworkPrefabRegistry>();
                    AssetDatabase.CreateAsset(registry, registryPath);
                    Undo.RegisterCreatedObjectUndo(registry, "Create network registry");
                    // Preserve an existing manager's registration order and IDs during migration.
                    var old = serializedManager.FindProperty("prefabs");
                    var entries = new List<NetworkPrefabRegistry.Entry>();
                    for (int i = 0; old != null && i < old.arraySize; i++)
                    {
                        var prefab = old.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                        if (prefab == null) throw new InvalidOperationException("Existing manager registration has a missing prefab. Fix it before migrating.");
                        entries.Add(new NetworkPrefabRegistry.Entry((uint)i + 1, prefab, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab))));
                    }
                    if (!entries.Exists(entry => entry.Prefab == playerPrefab))
                        entries.Add(new NetworkPrefabRegistry.Entry((uint)entries.Count + 1, playerPrefab, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(playerPrefab))));
                    registry.SetEntries(entries);
                }
                else if (!registry.TryGetId(playerPrefab, out _))
                {
                    Undo.RecordObject(registry, "Register player prefab");
                    var entries = new List<NetworkPrefabRegistry.Entry>(registry.Entries);
                    entries.Add(new NetworkPrefabRegistry.Entry((uint)entries.Count + 1, playerPrefab, AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(playerPrefab))));
                    registry.SetEntries(entries);
                }
                if (!registry.TryGetId(playerPrefab, out uint playerId)) throw new InvalidOperationException("Player prefab registration failed.");
                Undo.RecordObject(manager, "Configure multiplayer session");
                manager.Topology = topology; manager.Transport = transport; manager.Address = address; manager.Port = port;
                var points = SceneComponents<NetworkSpawnPoint>();
                if (includeSpawnPoint && points.Count == 0)
                {
                    var spawn = new GameObject("SocketJack Player Spawn"); Undo.RegisterCreatedObjectUndo(spawn, "Create player spawn point");
                    spawn.transform.position = new Vector3(0, 1, 0);
                    points.Add(Undo.AddComponent<NetworkSpawnPoint>(spawn));
                }
                var spawner = manager.GetComponent<NetworkPlayerSpawner>() ?? Undo.AddComponent<NetworkPlayerSpawner>(manager.gameObject);
                Undo.RecordObject(spawner, "Configure player spawning"); spawner.Configure(manager, registry, playerId, points.ToArray());
                var controls = manager.GetComponent<NetworkSessionControls>() ?? Undo.AddComponent<NetworkSessionControls>(manager.gameObject);
                Undo.RecordObject(controls, "Configure session controls"); controls.Configure(manager);
                ConfigureAdditions(manager);
                EditorUtility.SetDirty(registry); EditorUtility.SetDirty(manager); EditorUtility.SetDirty(spawner); EditorUtility.SetDirty(controls);
                EditorSceneManager.MarkSceneDirty(manager.gameObject.scene);
                AssetDatabase.SaveAssets();
                Selection.activeGameObject = manager.gameObject;
                result = "Setup complete. " + (Has(NetworkSetupFeatures.AccountsAndChat) ? "Enter Play Mode, start the listener in the on-screen menu, sign in or create an account in the chat panel, then Join authenticated game. " : "Enter Play Mode and use NetworkSessionControls' Host/Join buttons, or bind its methods to your menu. ") + "Review any existing force/jump motor to avoid applying movement twice.";
                Undo.CollapseUndoOperations(group);
                Undo.IncrementCurrentGroup();
            }
            catch (Exception error)
            {
                // Do not delete assets or discard unrelated work after an error; retain the undo group for review.
                result = "Setup stopped: " + error.Message + " Review the changes and use Undo if needed.";
                Debug.LogException(error);
                Undo.IncrementCurrentGroup();
            }
        }
        private const string Components = "SocketJack.Multiplayer.Components.";
        private const string WebComponents = "SocketJack.Multiplayer.Web.Components.";
        private const string VoiceComponents = "SocketJack.Multiplayer.Voice.Components.";
        private const string VoiceWebComponents = "SocketJack.Multiplayer.Voice.Web.Components.";
        private static readonly string[] WebTypes = { WebComponents + "SocketJackWebHost", WebComponents + "SocketJackWebClientBehaviour", WebComponents + "SocketJackMMOChatPanel", WebComponents + "SocketJackWebSessionControls" };
        private static readonly string[] VoiceTypes = { VoiceComponents + "SocketJackVoiceManager", VoiceComponents + "SocketJackVoiceControls", VoiceComponents + "SocketJackBrowserMicrophone", VoiceWebComponents + "SocketJackWebVoiceHost", VoiceWebComponents + "SocketJackWebVoiceClient", VoiceWebComponents + "SocketJackBrowserVoiceSocket" };
        private bool Has(NetworkSetupFeatures feature) => (features & feature) != 0;
        private void FeatureToggle(string label, NetworkSetupFeatures feature)
        { if (EditorGUILayout.Toggle(label, Has(feature))) features |= feature; else features &= ~feature; }
        private void RequireOptionalTypes(string module, string[] types)
        {
            foreach (string name in types) if (FindComponentType(name) == null)
            { findings.Add("Import " + module + " from the matching release, including its Unity scripts and managed dependencies, or deselect this addition. Missing: " + name.Substring(name.LastIndexOf('.') + 1)); break; }
        }
        private void ConfigureAdditions(NetworkManager manager)
        {
            var target = manager.gameObject;
            if (Has(NetworkSetupFeatures.Pooling)) AddIfMissing(target, Components + "NetworkObjectPool", out _);
            if (Has(NetworkSetupFeatures.AdaptivePrecision)) AddIfMissing(target, Components + "SocketJackAdaptiveNetworking", out _);
            if (Has(NetworkSetupFeatures.SpatialInterest))
            {
                var interest = AddIfMissing(target, Components + "SocketJackInterestManager", out bool created);
                if (created) { var serialized = new SerializedObject(interest); serialized.FindProperty("Dimension").enumValueIndex = movement == Movement.Rigidbody2D ? 1 : 0; serialized.ApplyModifiedProperties(); }
            }
            if (Has(NetworkSetupFeatures.AccountsAndChat))
            {
                var host = AddIfMissing(target, WebTypes[0], out bool createdHost);
                if (createdHost) { SetSerialized(host, "network", manager); SetSerialized(host, "webPort", port + 1); SetSerialized(host, "requireGameAccount", true); }
                string endpoint = "http://127.0.0.1:" + (port + 1);
                var client = AddIfMissing(target, WebTypes[1], out bool createdClient);
                if (createdClient) SetSerialized(client, "serverAddress", endpoint);
                var chat = AddIfMissing(target, WebTypes[2], out bool createdChat);
                if (createdChat) SetSerialized(chat, "endpoint", endpoint);
                AddIfMissing(target, WebTypes[3], out _);
            }
            if (Has(NetworkSetupFeatures.Voice)) foreach (string name in VoiceTypes) AddIfMissing(target, name, out _);
        }
        private static Component AddIfMissing(GameObject target, string fullName, out bool created)
        {
            Type type = FindComponentType(fullName); if (type == null) throw new InvalidOperationException("Import the module providing " + fullName + " before applying setup.");
            var component = target.GetComponent(type); created = component == null;
            if (created) component = Undo.AddComponent(target, type);
            return component;
        }
        private static void SetSerialized(Component target, string name, object value)
        {
            var serialized = new SerializedObject(target); var property = serialized.FindProperty(name);
            if (property == null) throw new InvalidOperationException("The installed module does not expose matching setup field " + name + ". Import matching release versions.");
            if (value is bool boolean) property.boolValue = boolean;
            else if (value is int integer) property.intValue = integer;
            else if (value is string text) property.stringValue = text;
            else property.objectReferenceValue = value as UnityEngine.Object;
            serialized.ApplyModifiedProperties(); EditorUtility.SetDirty(target);
        }
        private static List<T> SceneComponents<T>() where T : Component
        {
            var result = new List<T>();
            var scene = SceneManager.GetActiveScene();
            if (scene.IsValid()) foreach (var root in scene.GetRootGameObjects()) result.AddRange(root.GetComponentsInChildren<T>(true));
            return result;
        }
        private static Type FindComponentType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;
            }
            return null;
        }
    }

    [CustomEditor(typeof(NetworkSessionControls))]
    internal sealed class NetworkSessionControlsEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var controls = (NetworkSessionControls)target;
            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Host + play")) controls.Host();
                    if (GUILayout.Button("Join")) controls.Join();
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Dedicated server")) controls.StartDedicatedServer();
                    if (GUILayout.Button("Stop")) controls.Stop();
                }
            }
            if (!string.IsNullOrEmpty(controls.LastError)) EditorGUILayout.HelpBox(controls.LastError, MessageType.Error);
        }
    }
}
#endif
