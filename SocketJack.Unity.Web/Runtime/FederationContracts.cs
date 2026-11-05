using System;
using System.Collections.Generic;

namespace SocketJack.Multiplayer.Web
{
    public sealed class FederationNode
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string KeyHash { get; set; }
        public bool Enabled { get; set; }
    }
    public interface IFederationStore
    {
        FederationNode FindNode(string id);
        bool TryAddNode(FederationNode node);
        void RevokeNode(string id);
        string ReadNodeKey(Uri hub, string nodeId);
        void SaveNodeKey(Uri hub, string nodeId, string nodeKey);
    }
    public sealed class FederationPresence
    {
        public string AccountId { get; set; }
        public string WorldId { get; set; }
        public int RoomId { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }
    public sealed class FederationPresenceUpdate
    {
        public List<FederationPresence> Players { get; set; } = new List<FederationPresence>();
        public string[] RemovedAccountIds { get; set; } = Array.Empty<string>();
    }
}
