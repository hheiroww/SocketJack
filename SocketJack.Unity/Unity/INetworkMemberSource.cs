using System.Collections.Generic;
using SocketJack.Multiplayer.Serialization;
namespace SocketJack.Multiplayer.Components
{
    /// <summary>Explicit code-authored bindings for reusable components. IDs/keys must be stable and unique on a prefab.</summary>
    public interface INetworkMemberSource
    {
        IEnumerable<NetworkMemberBinding> CreateNetworkBindings(INetworkSerializer serializer);
    }
}
