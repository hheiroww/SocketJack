using UnityEngine;

namespace SocketJack.Multiplayer.Components
{
    /// <summary>An explicit player spawn point. Its room is assigned to the joining peer before spawning.</summary>
    public sealed class NetworkSpawnPoint : MonoBehaviour
    {
        [SerializeField] private int roomId;
        public int RoomId { get => roomId; set => roomId = value; }
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.white;
            Gizmos.DrawWireSphere(transform.position, 0.4f);
            Gizmos.DrawLine(transform.position, transform.position + transform.forward);
        }
    }
}
