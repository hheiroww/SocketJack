using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using SocketJack.Multiplayer.Serialization;

namespace SocketJack.Multiplayer
{
    /// <summary>Authority for one configured member, independent of the object's movement authority.</summary>
    public enum NetworkMemberAuthority { Server = 0, Owner = 1 }

    /// <summary>An explicitly selected local accessor. Received packets never select a CLR type or member.</summary>
    public sealed class NetworkMemberBinding
    {
        public ushort Id { get; }
        public string Key { get; }
        public Type ValueType { get; }
        public NetworkMemberAuthority Authority { get; }
        public double SendInterval { get; }
        public bool OnlyWhenChanged { get; }
        public int MaxBytes { get; }
        /// <summary>Optional binding-specific value codec, for example a numeric precision profile.</summary>
        public INetworkSerializer ValueSerializer { get; }
        internal Func<object> GetValue { get; }
        internal Action<object> SetValue { get; }

        public NetworkMemberBinding(ushort id, string key, Type valueType, Func<object> getValue,
            Action<object> setValue, NetworkMemberAuthority authority = NetworkMemberAuthority.Server,
            double sendInterval = 0.1, bool onlyWhenChanged = true, int maxBytes = 256, INetworkSerializer valueSerializer = null)
        {
            if (id == 0 || string.IsNullOrWhiteSpace(key) || key.Length > 512 || valueType == null ||
                getValue == null || setValue == null || !Enum.IsDefined(typeof(NetworkMemberAuthority), authority) ||
                double.IsNaN(sendInterval) || double.IsInfinity(sendInterval) || sendInterval < 0.016 || sendInterval > 3600 ||
                maxBytes < 1 || maxBytes > NetworkMemberSyncOptions.AbsoluteMaxMemberBytes)
                throw new ArgumentException("Invalid network member binding or bounds.");
            Id = id; Key = key; ValueType = valueType; GetValue = getValue; SetValue = setValue;
            Authority = authority; SendInterval = sendInterval; OnlyWhenChanged = onlyWhenChanged; MaxBytes = maxBytes; ValueSerializer = valueSerializer;
        }
    }

    /// <summary>Local field/property metadata for an explicit opt-in UI. Getters may run game code.</summary>
    public sealed class NetworkMemberDescriptor
    {
        public string Key { get; }
        public string Name { get; }
        public Type ValueType { get; }
        public bool IsProperty { get; }
        private readonly FieldInfo field;
        private readonly PropertyInfo property;
        internal NetworkMemberDescriptor(FieldInfo value)
        { field = value; Name = value.Name; ValueType = value.FieldType; Key = NetworkMemberAccessors.GetTypeKey(value.DeclaringType) + "|F|" + Name; }
        internal NetworkMemberDescriptor(PropertyInfo value)
        { property = value; Name = value.Name; ValueType = value.PropertyType; IsProperty = true; Key = NetworkMemberAccessors.GetTypeKey(value.DeclaringType) + "|P|" + Name; }
        public NetworkMemberBinding Bind(object target, ushort id, NetworkMemberAuthority authority = NetworkMemberAuthority.Server,
            double sendInterval = 0.1, bool onlyWhenChanged = true, int maxBytes = 256, string schemaKey = null, INetworkSerializer valueSerializer = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            return new NetworkMemberBinding(id, schemaKey ?? Key, ValueType,
                () => field != null ? field.GetValue(target) : property.GetValue(target, null),
                value => { if (field != null) field.SetValue(target, value); else property.SetValue(target, value, null); },
                authority, sendInterval, onlyWhenChanged, maxBytes, valueSerializer);
        }
    }

    public static class NetworkMemberAccessors
    {
        public static string GetTypeKey(Type type) => SocketJackBinaryFormatter.StableTypeName(type ?? throw new ArgumentNullException(nameof(type)));
        /// <summary>Lists writable instance fields and read/write, non-indexed properties, including nonpublic ones.
        /// Static, readonly, literal and generated backing fields are excluded. Selection remains opt-in.</summary>
        public static IReadOnlyList<NetworkMemberDescriptor> GetEligibleMembers(Type type)
            => GetEligibleMembers(type, null);
        private static IReadOnlyList<NetworkMemberDescriptor> GetEligibleMembers(Type type, string explicitlySelectedKey)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var result = new List<NetworkMemberDescriptor>();
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var field in current.GetFields(flags))
                    if (!field.IsStatic && !field.IsInitOnly && !field.IsLiteral && !field.IsDefined(typeof(CompilerGeneratedAttribute), false) &&
                        !field.FieldType.IsPointer && !field.FieldType.IsByRef)
                        result.Add(new NetworkMemberDescriptor(field));
                foreach (var property in current.GetProperties(flags))
                {
                    var getter = property.GetGetMethod(true); var setter = property.GetSetMethod(true);
                    if (getter != null && setter != null && !getter.IsStatic && !setter.IsStatic && property.GetIndexParameters().Length == 0 &&
                        !property.PropertyType.IsPointer && !property.PropertyType.IsByRef &&
                        !IsInitOnly(setter, new NetworkMemberDescriptor(property).Key == explicitlySelectedKey))
                        result.Add(new NetworkMemberDescriptor(property));
                }
            }
            return result.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();
        }
        private static bool IsInitOnly(MethodInfo setter, bool explicitLocalSelection)
        {
            try { return setter.ReturnParameter.GetRequiredCustomModifiers().Any(x => x.FullName == "System.Runtime.CompilerServices.IsExternalInit"); }
            // IL2CPP cannot inspect custom modifiers. Discovery fails closed. A full local key
            // selected/validated in the Editor remains usable; remote data never selects a key.
            catch (NotSupportedException) { return !explicitLocalSelection; }
            catch (NotImplementedException) { return !explicitLocalSelection; }
        }

        /// <summary>Resolves a configured local key (or unambiguous member name); no network input is used here.</summary>
        public static NetworkMemberDescriptor Find(Type type, string key)
        {
            var matches = GetEligibleMembers(type, key).Where(x => x.Key == key || x.Name == key).ToArray();
            if (matches.Length != 1) throw new ArgumentException("Member is missing, ambiguous or not a writable instance field/property: " + key);
            return matches[0];
        }
    }

    /// <summary>Immutable, bounded prefab schema. Both ends must use identical bindings and serializer protocols.</summary>
    public sealed class NetworkMemberSchema
    {
        public INetworkSerializer Serializer { get; }
        public IReadOnlyList<NetworkMemberBinding> Members { get; }
        public ulong Fingerprint { get; }
        private readonly Dictionary<ushort, int> indices = new Dictionary<ushort, int>();
        public NetworkMemberSchema(INetworkSerializer serializer, IEnumerable<NetworkMemberBinding> members)
        {
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            var list = (members ?? throw new ArgumentNullException(nameof(members))).OrderBy(x => x.Id).ToArray();
            if (list.Length == 0 || list.Length > NetworkMemberSyncOptions.AbsoluteMaxMembers)
                throw new ArgumentOutOfRangeException(nameof(members), "A schema needs 1..64 enabled members.");
            string protocol = serializer.ProtocolId;
            if (string.IsNullOrWhiteSpace(protocol) || protocol.Length > 4096) throw new ArgumentException("Serializer needs a stable bounded ProtocolId.");
            ulong hash = 14695981039346656037UL;
            Action<string> add = text => { foreach (byte value in Encoding.UTF8.GetBytes(text)) { hash ^= value; hash *= 1099511628211UL; } hash ^= 0; hash *= 1099511628211UL; };
            add("SocketJack.MemberSync/1"); add(protocol);
            for (int i = 0; i < list.Length; i++)
            {
                var member = list[i] ?? throw new ArgumentException("Null member.");
                var codec = member.ValueSerializer ?? serializer;
                if (!codec.CanSerialize(member.ValueType) || string.IsNullOrWhiteSpace(codec.ProtocolId) || codec.ProtocolId.Length > 4096)
                    throw new NotSupportedException("Serializer does not support member type or has invalid protocol ID: " + member.ValueType.FullName);
                if (indices.ContainsKey(member.Id)) throw new ArgumentException("Duplicate member ID.");
                indices.Add(member.Id, i);
                add(member.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); add(member.Key); add(SocketJackBinaryFormatter.StableTypeName(member.ValueType));
                add(((int)member.Authority).ToString()); add(member.MaxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (member.ValueSerializer != null) add(member.ValueSerializer.ProtocolId);
                // Scheduling choices do not change accepted wire data and can differ between Unity float settings and .NET doubles.
            }
            Members = Array.AsReadOnly(list); Fingerprint = hash;
        }
        internal bool TryIndex(ushort memberId, out int index) => indices.TryGetValue(memberId, out index);
        internal byte[] Capture(int index)
        {
            var member = Members[index];
            var bytes = (member.ValueSerializer ?? Serializer).Serialize(member.GetValue(), member.ValueType, member.MaxBytes);
            if (bytes == null || bytes.Length > member.MaxBytes) throw new InvalidOperationException("Serializer exceeded configured member bounds.");
            return (byte[])bytes.Clone();
        }
        internal object Decode(int index, byte[] bytes)
        {
            var member = Members[index];
            if (bytes == null || bytes.Length > member.MaxBytes) throw new ArgumentException("Member value exceeds configured bounds.");
            var value = (member.ValueSerializer ?? Serializer).Deserialize(bytes, member.ValueType, member.MaxBytes);
            if (value == null ? member.ValueType.IsValueType && Nullable.GetUnderlyingType(member.ValueType) == null : !member.ValueType.IsInstanceOfType(value))
                throw new InvalidOperationException("Serializer returned a value incompatible with the configured local member type.");
            return value;
        }
    }

    public sealed class NetworkMemberSyncOptions
    {
        public const int AbsoluteMaxMembers = 64;
        public const int AbsoluteMaxMemberBytes = 8192;
        public int MaxObjects { get; set; } = 16384;
        public int MaxSubscriptions { get; set; } = 65536;
        public int MaxSubscriptionsPerPeer { get; set; } = 4096;
        public int MaxCanonicalBytes { get; set; } = 16 * 1024 * 1024;
        public int MaxSendsPerUpdate { get; set; } = 256;
        public int MaxScansPerUpdate { get; set; } = 4096;
        public int MaxCaptureChecksPerUpdate { get; set; } = 4096;
        public int MaxClientMessagesPerSecond { get; set; } = 120;
        public int ClientBurstMessages { get; set; } = 32;
        internal NetworkMemberSyncOptions Snapshot()
        {
            if (MaxObjects < 1 || MaxSubscriptions < 1 || MaxSubscriptionsPerPeer < 1 || MaxSubscriptionsPerPeer > MaxSubscriptions ||
                MaxCanonicalBytes < AbsoluteMaxMemberBytes || MaxSendsPerUpdate < 1 || MaxSendsPerUpdate > 65536 ||
                MaxScansPerUpdate < 1 || MaxCaptureChecksPerUpdate < 1 ||
                MaxClientMessagesPerSecond < 1 || MaxClientMessagesPerSecond > 65535 || ClientBurstMessages < 1 || ClientBurstMessages > 65535)
                throw new ArgumentOutOfRangeException(nameof(NetworkMemberSyncOptions));
            return (NetworkMemberSyncOptions)MemberwiseClone();
        }
    }
}
