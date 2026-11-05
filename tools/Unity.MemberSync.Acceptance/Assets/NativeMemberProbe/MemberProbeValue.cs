using UnityEngine;

namespace SocketJack.Tests.MemberSync
{
    public sealed class MemberProbeValue : MonoBehaviour
    {
        public int OwnerValue = 11;
        public int ServerValue { get; set; } = 7;
        public byte[] Blob = new byte[6000];
        public Vector3 ServerVector = new Vector3(1, 2, 3);
        public GameObject DisabledReference;
    }
}
