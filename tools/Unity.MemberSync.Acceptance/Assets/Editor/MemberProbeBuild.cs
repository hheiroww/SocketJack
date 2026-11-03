using System;
using System.IO;
using SocketJack.Tests.MemberSync;
using SocketJack.Multiplayer;
using SocketJack.Multiplayer.Components;
using SocketJack.Multiplayer.Serialization;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MemberProbeBuild
{
    public static void Play()
    {
        EditorSceneManager.OpenScene("Assets/Scenes/Physics.unity");
#if UNITY_6000_0_OR_NEWER
        // Batch Play Mode does not exercise Unity's background asset search indexer. Prevent
        // its startup task restarting during this fixture's domain reload (session only).
        // UnityCsReference/6000.3/Modules/QuickSearch/Editor/SearchInit.cs uses this gate.
        SessionState.SetBool("IndexingOnEditorStartup_Started", true);
#endif
        EditorApplication.isPlaying = true;
    }
    public static void BuildWindows()
    {
        Directory.CreateDirectory("Assets/Resources");
        var target = new GameObject("MemberProbeObject");
        var managerRoot = new GameObject("MemberProbeManager");
        try
        {
            target.AddComponent<NetworkIdentity>();
            var value = target.AddComponent<MemberProbeValue>();
            var sync = target.AddComponent<SocketJackMemberSync>();
            sync.AddMember(value, nameof(MemberProbeValue.ServerValue), NetworkMemberAuthority.Server, .016f);
            sync.AddMember(value, nameof(MemberProbeValue.OwnerValue), NetworkMemberAuthority.Owner, .016f);
            sync.AddMember(value, nameof(MemberProbeValue.Blob), NetworkMemberAuthority.Owner, .016f, maxBytes: 8192);
            sync.AddMember(value, nameof(MemberProbeValue.ServerVector), NetworkMemberAuthority.Server, .016f);
            sync.AddMember(value, nameof(MemberProbeValue.DisabledReference), enabled: false);
            target.SetActive(false);
            var prefab = PrefabUtility.SaveAsPrefabAsset(target, "Assets/Resources/MemberProbeObject.prefab");
            var manager = managerRoot.AddComponent<NetworkManager>();
            var provider = managerRoot.AddComponent<MemberProbeProvider>();
            var serialized = new SerializedObject(manager);
            serialized.FindProperty("serializerKind").enumValueIndex = (int)NetworkSerializerKind.CustomProvider;
            serialized.FindProperty("serializerProvider").objectReferenceValue = provider;
            serialized.FindProperty("enableAutomaticPortForwarding").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            manager.RegisterPrefabs(prefab);
            PrefabUtility.SaveAsPrefabAsset(managerRoot, "Assets/Resources/MemberProbeManager.prefab");
            AssetDatabase.SaveAssets();
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Resources/MemberProbeObject.prefab");
            if (saved.GetComponent<SocketJackMemberSync>().Members.Count != 5 || saved.GetComponent<SocketJackMemberSync>().Members[0].Target != saved.GetComponent<MemberProbeValue>())
                throw new InvalidOperationException("Serialized member target binding was not preserved in prefab.");
        }
        finally { UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(managerRoot); }
        SocketJack.Samples.Physics.Editor.PhysicsSampleBuild.BuildWindows();
    }
}
