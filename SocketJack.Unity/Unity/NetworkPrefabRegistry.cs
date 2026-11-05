using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>Stable, explicit prefab IDs. Reordering entries never renumbers a prefab.</summary>
    [CreateAssetMenu(menuName = "SocketJack/Network Prefab Registry", fileName = "NetworkPrefabs")]
    public sealed class NetworkPrefabRegistry : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            [Min(1)] public uint Id;
            public GameObject Prefab;
            [Tooltip("Persistent asset GUID assigned by the setup tools. Retain it in every build.")]
            public string StableKey;
            public Entry(uint id, GameObject prefab, string stableKey)
            { Id = id; Prefab = prefab; StableKey = stableKey; }
        }
        [SerializeField] private Entry[] entries = Array.Empty<Entry>();
        public IReadOnlyList<Entry> Entries => Array.AsReadOnly(entries);

        public void SetEntries(IEnumerable<Entry> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var next = new List<Entry>();
            foreach (var entry in values)
            {
                if (entry == null) throw new ArgumentException("Null registry entry.", nameof(values));
                next.Add(new Entry(entry.Id, entry.Prefab, entry.StableKey));
            }
            Validate(next.ToArray());
            entries = next.ToArray();
        }
        public GameObject[] BuildPrefabArray()
        {
            Validate(entries);
            var result = new GameObject[entries.Length];
            foreach (var entry in entries) result[entry.Id - 1] = entry.Prefab;
            return result;
        }
        public void ApplyTo(NetworkManager manager)
        {
            if (manager == null) throw new ArgumentNullException(nameof(manager));
            manager.RegisterPrefabs(BuildPrefabArray());
            manager.CompatibilityId = BuildManifest();
        }
        /// <summary>Canonical catalog text. Movement/member/precision schemas are appended by the manager.</summary>
        public string BuildManifest()
        {
            Validate(entries);
            var sorted = (Entry[])entries.Clone();
            Array.Sort(sorted, (left, right) => left.Id.CompareTo(right.Id));
            var text = new StringBuilder("SocketJack.PrefabRegistry/1\n");
            foreach (var entry in sorted)
                text.Append(entry.Id.ToString(CultureInfo.InvariantCulture)).Append(':').Append(entry.StableKey).Append('\n');
            return text.ToString();
        }
        public bool TryGetId(GameObject prefab, out uint id)
        {
            foreach (var entry in entries)
                if (entry != null && entry.Prefab == prefab) { id = entry.Id; return true; }
            id = 0; return false;
        }
        private static void Validate(Entry[] values)
        {
            if (values == null || values.Length > 65535) throw new ArgumentException("Registry supports at most 65,535 prefabs.");
            var ids = new HashSet<uint>();
            var prefabs = new HashSet<GameObject>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in values)
            {
                if (entry == null || entry.Prefab == null || entry.Id < 1 || entry.Id > values.Length || !ids.Add(entry.Id) ||
                    !prefabs.Add(entry.Prefab) || string.IsNullOrWhiteSpace(entry.StableKey) || entry.StableKey.Length > 128 ||
                    entry.StableKey.IndexOfAny(new[] { '\r', '\n', ':' }) >= 0 || !keys.Add(entry.StableKey))
                    throw new ArgumentException("Prefab registry needs unique prefabs, stable keys and contiguous explicit IDs starting at one.");
                if (entry.Prefab.GetComponent<NetworkIdentity>() == null)
                    throw new ArgumentException("Every registered prefab must have NetworkIdentity: " + entry.Prefab.name);
            }
        }
    }
}
