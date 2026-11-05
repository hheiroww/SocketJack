using System;
using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Server-binds an existing scene instance instead of instantiating a prefab. Save identical IDs in all builds.</summary>
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class NetworkSceneObject : MonoBehaviour
    {
        [SerializeField] private uint sceneObjectId;
        public uint SceneObjectId { get => sceneObjectId; set { if (value == 0 || value > 0x7fffffffu) throw new ArgumentOutOfRangeException(nameof(value)); sceneObjectId = value; } }
#if UNITY_EDITOR
        private void OnValidate()
        {
            if (sceneObjectId != 0) return;
            do { sceneObjectId = unchecked((uint)Guid.NewGuid().GetHashCode()) & 0x7fffffffu; } while (sceneObjectId == 0);
        }
#endif
    }
}
