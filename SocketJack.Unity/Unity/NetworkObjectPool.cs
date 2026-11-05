using System;
using System.Collections.Generic;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    public interface INetworkObjectPool
    {
        GameObject Rent(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation);
        void Return(uint prefabId, GameObject instance);
    }
    /// <summary>Optional bounded prefab pool. Identity/baselines/prediction reset before reuse; game scripts reset through OnNetworkReset.</summary>
    [DefaultExecutionOrder(-12000), RequireComponent(typeof(NetworkManager)), DisallowMultipleComponent]
    public sealed class NetworkObjectPool : MonoBehaviour, INetworkObjectPool
    {
        [Min(0)] public int MaxPerPrefab = 32;
        [Min(0)] public int MaxTotal = 256;
        private readonly Dictionary<uint, Stack<GameObject>> idle = new Dictionary<uint, Stack<GameObject>>();
        private readonly HashSet<GameObject> returned = new HashSet<GameObject>();
        private NetworkManager manager;
        public int PooledCount => returned.Count;
        private void Awake() { manager = GetComponent<NetworkManager>(); manager.ObjectPool = this; }
        public GameObject Rent(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation)
        {
            if (prefab == null || prefabId == 0) throw new ArgumentException("A registered prefab is required.");
            GameObject instance = null;
            if (idle.TryGetValue(prefabId, out var entries))
                while (entries.Count > 0 && instance == null) { var candidate = entries.Pop(); returned.Remove(candidate); if (candidate != null) instance = candidate; }
            if (instance == null) instance = Instantiate(prefab, position, rotation);
            instance.transform.SetParent(null, false); instance.transform.localScale = prefab.transform.localScale; instance.transform.SetPositionAndRotation(position, rotation); return instance;
        }
        public void Return(uint prefabId, GameObject instance)
        {
            if (instance == null) return;
            if (!returned.Add(instance)) throw new InvalidOperationException("A network instance was returned twice.");
            if (!idle.TryGetValue(prefabId, out var entries)) idle.Add(prefabId, entries = new Stack<GameObject>());
            if (entries.Count >= Math.Max(0, MaxPerPrefab) || returned.Count > Math.Max(0, MaxTotal))
            { returned.Remove(instance); Destroy(instance); return; }
            var body = instance.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic)
            {
#if UNITY_6000_0_OR_NEWER
                body.linearVelocity = Vector3.zero;
#else
                body.velocity = Vector3.zero;
#endif
                body.angularVelocity = Vector3.zero;
            }
            var body2D = instance.GetComponent<Rigidbody2D>();
            if (body2D != null && body2D.bodyType == RigidbodyType2D.Dynamic)
            {
#if UNITY_6000_0_OR_NEWER
                body2D.linearVelocity = Vector2.zero;
#else
                body2D.velocity = Vector2.zero;
#endif
                body2D.angularVelocity = 0;
            }
            instance.SetActive(false); instance.transform.SetParent(transform, false); entries.Push(instance);
        }
    }
}
