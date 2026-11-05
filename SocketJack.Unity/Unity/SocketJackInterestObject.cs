using UnityEngine;
namespace SocketJack.Multiplayer.Components
{
    [DisallowMultipleComponent, RequireComponent(typeof(NetworkIdentity))]
    public sealed class SocketJackInterestObject : MonoBehaviour
    {
        public bool AlwaysVisible;
        public bool ObserverAnchor;
    }
}
