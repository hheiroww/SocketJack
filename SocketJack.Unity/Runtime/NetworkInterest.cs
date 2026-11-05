using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace SocketJack.Multiplayer
{
    public interface INetworkInterestProvider
    {
        IEnumerable<ulong> QueryCandidates(ulong peerId, int roomId);
        bool IsVisible(ulong peerId, NetworkObject obj, bool alreadyVisible);
    }
    public enum NetworkSpatialDimension { XZ = 0, XY = 1, XYZ = 2 }
    public sealed class SpatialInterestOptions
    {
        public NetworkSpatialDimension Dimension { get; set; } = NetworkSpatialDimension.XZ;
        public float Radius { get; set; } = 60;
        public float Hysteresis { get; set; } = 8;
        public float CellSize { get; set; } = 32;
        public int MaxObjects { get; set; } = 100000;
        public int MaxPeers { get; set; } = 4096;
        public int MaxCandidates { get; set; } = 4096;
        public int MaxCellsPerQuery { get; set; } = 4096;
        internal SpatialInterestOptions Snapshot()
        {
            if (!Enum.IsDefined(typeof(NetworkSpatialDimension), Dimension) || !Finite(Radius) || !Finite(Hysteresis) || !Finite(CellSize) ||
                Radius <= 0 || Hysteresis < 0 || CellSize <= 0 || MaxObjects < 1 || MaxPeers < 1 || MaxCandidates < 1 || MaxCellsPerQuery < 1)
                throw new ArgumentOutOfRangeException(nameof(SpatialInterestOptions));
            double width = Math.Ceiling((Radius + Hysteresis) / CellSize) * 2 + 1;
            if (Math.Pow(width, Dimension == NetworkSpatialDimension.XYZ ? 3 : 2) > MaxCellsPerQuery)
                throw new ArgumentException("Radius/cell size exceeds the bounded spatial query budget.");
            return (SpatialInterestOptions)MemberwiseClone();
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
    /// <summary>Manually pumped, room-partitioned spatial hash. Update it from canonical server positions.
    /// Ownership keeps an object visible even outside radius. Unregistered objects are hidden.</summary>
    public sealed class SpatialInterestIndex : INetworkInterestProvider
    {
        private sealed class Position { internal Vector3 Value; internal int Room; internal ulong Owner; internal bool Always; }
        private readonly SpatialInterestOptions options;
        private readonly Dictionary<ulong, Position> objects = new Dictionary<ulong, Position>();
        private readonly Dictionary<ulong, Vector3> peers = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<(int, int, int, int), HashSet<ulong>> cells = new Dictionary<(int, int, int, int), HashSet<ulong>>();
        private readonly Dictionary<ulong, HashSet<ulong>> owned = new Dictionary<ulong, HashSet<ulong>>();
        private readonly HashSet<ulong> always = new HashSet<ulong>();
        public SpatialInterestIndex(SpatialInterestOptions options = null) { this.options = (options ?? new SpatialInterestOptions()).Snapshot(); }
        public void SetPeerPosition(ulong peerId, Vector3 position)
        {
            Validate(position); if (peerId == 0) throw new ArgumentOutOfRangeException(nameof(peerId));
            if (!peers.ContainsKey(peerId) && peers.Count >= options.MaxPeers) throw new InvalidOperationException("Spatial peer capacity reached.");
            peers[peerId] = Project(position);
        }
        public void RemovePeer(ulong peerId) => peers.Remove(peerId);
        public void SetObjectPosition(ulong objectId, int roomId, Vector3 position, ulong ownerId = 0, bool alwaysVisible = false)
        {
            Validate(position); if (objectId == 0) throw new ArgumentOutOfRangeException(nameof(objectId));
            var projected = Project(position);
            if (objects.TryGetValue(objectId, out var previous) && previous.Room == roomId && previous.Owner == ownerId && previous.Always == alwaysVisible &&
                Cell(previous.Value, roomId).Equals(Cell(projected, roomId))) { previous.Value = projected; return; }
            if (!objects.ContainsKey(objectId) && objects.Count >= options.MaxObjects) throw new InvalidOperationException("Spatial object capacity reached.");
            RemoveObject(objectId);
            var record = new Position { Value = projected, Room = roomId, Owner = ownerId, Always = alwaysVisible }; objects.Add(objectId, record);
            var key = Cell(record.Value, roomId); if (!cells.TryGetValue(key, out var bucket)) cells.Add(key, bucket = new HashSet<ulong>()); bucket.Add(objectId);
            if (ownerId != 0) { if (!owned.TryGetValue(ownerId, out var owner)) owned.Add(ownerId, owner = new HashSet<ulong>()); owner.Add(objectId); }
            if (alwaysVisible) always.Add(objectId);
        }
        public void RemoveObject(ulong objectId)
        {
            if (!objects.TryGetValue(objectId, out var record)) return;
            var key = Cell(record.Value, record.Room); if (cells.TryGetValue(key, out var bucket)) { bucket.Remove(objectId); if (bucket.Count == 0) cells.Remove(key); }
            if (record.Owner != 0 && owned.TryGetValue(record.Owner, out var owner)) { owner.Remove(objectId); if (owner.Count == 0) owned.Remove(record.Owner); }
            objects.Remove(objectId); always.Remove(objectId);
        }
        public IEnumerable<ulong> QueryCandidates(ulong peerId, int roomId)
        {
            var found = new HashSet<ulong>();
            if (owned.TryGetValue(peerId, out var owner)) foreach (var id in owner) if (objects[id].Room == roomId) found.Add(id);
            foreach (var id in always) if (objects[id].Room == roomId && found.Count < options.MaxCandidates) found.Add(id);
            if (!peers.TryGetValue(peerId, out var center)) return found.OrderBy(id => id).ToArray();
            var cell = Cell(center, roomId); int reach = (int)Math.Ceiling((options.Radius + options.Hysteresis) / options.CellSize);
            var nearby = new List<ulong>();
            for (int x = -reach; x <= reach; x++) for (int y = -reach; y <= reach; y++)
                for (int z = options.Dimension == NetworkSpatialDimension.XYZ ? -reach : 0; z <= (options.Dimension == NetworkSpatialDimension.XYZ ? reach : 0); z++)
                    if (cells.TryGetValue((roomId, cell.Item2 + x, cell.Item3 + y, cell.Item4 + z), out var bucket)) nearby.AddRange(bucket);
            foreach (var id in nearby.OrderBy(id => Vector3.DistanceSquared(center, objects[id].Value)).ThenBy(id => id))
            { if (found.Count >= options.MaxCandidates) break; found.Add(id); }
            return found.OrderBy(id => id).ToArray();
        }
        public bool IsVisible(ulong peerId, NetworkObject obj, bool alreadyVisible)
        {
            if (obj.OwnerId == peerId) return true;
            if (!objects.TryGetValue(obj.Id, out var record) || record.Room != obj.RoomId) return false;
            if (record.Always) return true;
            if (!peers.TryGetValue(peerId, out var point)) return false;
            float radius = options.Radius + (alreadyVisible ? options.Hysteresis : 0);
            return Vector3.DistanceSquared(point, record.Value) <= radius * radius;
        }
        private Vector3 Project(Vector3 value) => options.Dimension == NetworkSpatialDimension.XZ ? new Vector3(value.X, value.Z, 0) : options.Dimension == NetworkSpatialDimension.XY ? new Vector3(value.X, value.Y, 0) : value;
        private (int, int, int, int) Cell(Vector3 value, int room) => (room, (int)Math.Floor(value.X / options.CellSize), (int)Math.Floor(value.Y / options.CellSize), (int)Math.Floor(value.Z / options.CellSize));
        private static void Validate(Vector3 value)
        { if (!SpatialInterestOptions.Finite(value.X) || !SpatialInterestOptions.Finite(value.Y) || !SpatialInterestOptions.Finite(value.Z) || Math.Abs(value.X) > 1e9 || Math.Abs(value.Y) > 1e9 || Math.Abs(value.Z) > 1e9) throw new ArgumentOutOfRangeException(nameof(value)); }
    }
}
