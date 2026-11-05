using System;
using System.Collections.Generic;
using System.Linq;
using SocketJack.Multiplayer.Serialization;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Opt-in field/property replication, separate from physics and transform state.
    /// Configure identical prefab schemas on all peers. Accessors and property side effects are local game code.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkIdentity))]
    public sealed class SocketJackMemberSync : MonoBehaviour
    {
        [Serializable]
        public sealed class MemberSetting
        {
            public bool Enabled;
            public Component Target;
            public string Member;
            public NetworkMemberAuthority Authority = NetworkMemberAuthority.Server;
            [Min(0.016f)] public float SendInterval = 0.1f;
            public bool OnlyWhenChanged = true;
            [Range(1, NetworkMemberSyncOptions.AbsoluteMaxMemberBytes)] public int MaxBytes = 256;
            public NetworkPrecisionPreset Precision = NetworkPrecisionPreset.FullPrecision;
            public NetworkPrecisionAsset PrecisionAsset;
            public NetworkQuantizationSemantic Semantic = NetworkQuantizationSemantic.Scalar;
        }
        [SerializeField] private List<MemberSetting> members = new List<MemberSetting>();
        private bool bound;
        public IReadOnlyList<MemberSetting> Members => members;
        public bool IsSynchronized
        {
            get
            {
                var identity = GetComponent<NetworkIdentity>();
                return identity != null && identity.Manager != null &&
                    (identity.IsServer || (identity.Manager.MemberClient != null && identity.Manager.MemberClient.IsReady(identity.ObjectId)));
            }
        }

        /// <summary>Programmatic alternative to the Inspector. Configure before spawning. Member may be an
        /// unambiguous local name or a key returned by NetworkMemberAccessors.GetEligibleMembers.</summary>
        public MemberSetting AddMember(Component target, string member, NetworkMemberAuthority authority = NetworkMemberAuthority.Server,
            float sendInterval = 0.1f, bool onlyWhenChanged = true, int maxBytes = 256, bool enabled = true)
        {
            if (bound) throw new InvalidOperationException("Configure member bindings before spawning; respawn after changing a schema.");
            if (target == null) throw new ArgumentNullException(nameof(target));
            var descriptor = NetworkMemberAccessors.Find(target.GetType(), member);
            var setting = new MemberSetting { Enabled = enabled, Target = target, Member = descriptor.Key, Authority = authority,
                SendInterval = sendInterval, OnlyWhenChanged = onlyWhenChanged, MaxBytes = maxBytes };
            members.Add(setting); return setting;
        }
        internal void MarkBound() { bound = true; }
        internal NetworkMemberSchema CreateSchema(INetworkSerializer serializer)
        {
            var configured = new List<NetworkMemberBinding>();
            if (members.Count > 1024) throw new InvalidOperationException("Too many configured member entries.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < members.Count; i++)
            {
                var entry = members[i]; if (entry == null || !entry.Enabled) continue;
                if (entry.Target == null || (entry.Target.transform != transform && !entry.Target.transform.IsChildOf(transform)))
                    throw new InvalidOperationException("Member targets must be components on this network object or its children.");
                var descriptor = NetworkMemberAccessors.Find(entry.Target.GetType(), entry.Member);
                var path = new List<string>(); var current = entry.Target.transform;
                while (current != transform) { path.Add(current.GetSiblingIndex().ToString(System.Globalization.CultureInfo.InvariantCulture)); current = current.parent; }
                path.Reverse();
                int componentIndex = Array.IndexOf(entry.Target.GetComponents(entry.Target.GetType()), entry.Target);
                string key = string.Join("/", path) + ":" + NetworkMemberAccessors.GetTypeKey(entry.Target.GetType()) + ":" + componentIndex + ":" + descriptor.Key;
                if (!keys.Add(key)) throw new InvalidOperationException("The same member is configured more than once.");
                INetworkSerializer valueCodec = entry.Precision == NetworkPrecisionPreset.FullPrecision && entry.PrecisionAsset == null ? null :
                    new UnityQuantizedNetworkSerializer(entry.PrecisionAsset != null ? entry.PrecisionAsset.CreateProfile() : NetworkPrecisionProfile.ForPreset(entry.Precision), entry.Semantic);
                configured.Add(descriptor.Bind(entry.Target, checked((ushort)(i + 1)), entry.Authority,
                    entry.SendInterval, entry.OnlyWhenChanged, entry.MaxBytes, key, valueCodec));
            }
            foreach (var component in GetComponents<MonoBehaviour>())
                if (component is INetworkMemberSource source)
                    foreach (var binding in source.CreateNetworkBindings(serializer)) configured.Add(binding);
            return configured.Count == 0 ? null : new NetworkMemberSchema(serializer, configured);
        }
    }
}

#if UNITY_EDITOR
namespace SocketJack.Publishing.Editor
{
    using SocketJack.Multiplayer;
    using SocketJack.Multiplayer.Components;
    using UnityEditor;

    [CustomPropertyDrawer(typeof(NetworkSerializerKind))]
    public sealed class NetworkSerializerKindDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            property.enumValueIndex = EditorGUI.Popup(position, label.text, property.enumValueIndex,
                new[] { "Binary formatter (SocketJack)", "Custom provider" });
            EditorGUI.EndProperty();
        }
    }

    [CustomEditor(typeof(SocketJackMemberSync))]
    public sealed class SocketJackMemberSyncEditor : UnityEditor.Editor
    {
        private Component scanTarget;
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EditorGUILayout.HelpBox("Opt in only the fields/properties this object should replicate. Targets must be on this object or its children. Server/Owner authority is per member and independent of physics authority. Properties can execute game code. Configure matching prefabs and respawn after changes.", MessageType.Info);
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                scanTarget = (Component)EditorGUILayout.ObjectField("List members on", scanTarget, typeof(Component), true);
                var list = serializedObject.FindProperty("members");
                if (scanTarget != null && GUILayout.Button("Add eligible members (disabled)"))
                {
                    var all = NetworkMemberAccessors.GetEligibleMembers(scanTarget.GetType());
                    foreach (var descriptor in all)
                    {
                        bool exists = false;
                        for (int i = 0; i < list.arraySize; i++)
                        {
                            var previous = list.GetArrayElementAtIndex(i);
                            if (previous.FindPropertyRelative("Target").objectReferenceValue == scanTarget && previous.FindPropertyRelative("Member").stringValue == descriptor.Key) { exists = true; break; }
                        }
                        if (exists || list.arraySize >= 1024) continue;
                        int index = list.arraySize++; var entry = list.GetArrayElementAtIndex(index);
                        entry.FindPropertyRelative("Enabled").boolValue = false; entry.FindPropertyRelative("Target").objectReferenceValue = scanTarget;
                        entry.FindPropertyRelative("Member").stringValue = descriptor.Key; entry.FindPropertyRelative("Authority").enumValueIndex = 0;
                        entry.FindPropertyRelative("SendInterval").floatValue = 0.1f; entry.FindPropertyRelative("OnlyWhenChanged").boolValue = true;
                        entry.FindPropertyRelative("MaxBytes").intValue = 256;
                    }
                }
                var preview = NetworkManager.CreateDefaultSerializer();
                for (int i = 0; i < list.arraySize; i++)
                {
                    var entry = list.GetArrayElementAtIndex(i);
                    EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Enabled"), new GUIContent("Enable member " + (i + 1)));
                    bool remove = GUILayout.Button("Remove", GUILayout.Width(65));
                    EditorGUILayout.EndHorizontal();
                    if (remove) { list.DeleteArrayElementAtIndex(i); EditorGUILayout.EndVertical(); break; }
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Target"));
                    var component = entry.FindPropertyRelative("Target").objectReferenceValue as Component;
                    if (component != null)
                    {
                        var descriptors = NetworkMemberAccessors.GetEligibleMembers(component.GetType());
                        string selectedKey = entry.FindPropertyRelative("Member").stringValue;
                        int selected = -1;
                        var labels = new string[descriptors.Count + 1]; labels[0] = "Select a writable member";
                        for (int j = 0; j < descriptors.Count; j++)
                        {
                            var descriptor = descriptors[j]; if (descriptor.Key == selectedKey || descriptor.Name == selectedKey) selected = j;
                            labels[j + 1] = descriptor.Name + " (" + descriptor.ValueType.Name + (descriptor.IsProperty ? ", property" : ", field") + ")";
                        }
                        int choice = EditorGUILayout.Popup("Member", selected + 1, labels) - 1;
                        if (choice >= 0 && choice != selected) entry.FindPropertyRelative("Member").stringValue = descriptors[choice].Key;
                        if (choice >= 0 && !preview.CanSerialize(descriptors[choice].ValueType))
                            EditorGUILayout.HelpBox("This type requires an explicitly configured custom serializer/codec. Object references, arbitrary classes and collections are not automatically serialized.", MessageType.Warning);
                    }
                    else EditorGUILayout.HelpBox("Assign the component whose member should be synchronized.", MessageType.Warning);
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Authority"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("SendInterval"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("OnlyWhenChanged"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("MaxBytes"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Precision"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("PrecisionAsset"));
                    EditorGUILayout.PropertyField(entry.FindPropertyRelative("Semantic"));
                    EditorGUILayout.EndVertical();
                }
            }
            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
