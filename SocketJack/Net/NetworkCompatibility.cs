using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Net {
    /// <summary>An attempted operation that the connected peer cannot safely decode.</summary>
    public sealed class FeatureDisabledEventArgs : EventArgs {
        public NetworkConnection Connection { get; }
        public string Feature { get; }
        public Type MessageType { get; }
        public string Reason { get; }
        internal FeatureDisabledEventArgs(NetworkConnection connection, string feature, Type type, string reason) {
            Connection = connection; Feature = feature; MessageType = type; Reason = reason;
        }
    }

    /// <summary>Immutable compatibility decisions for one peer, independent of authentication.</summary>
    public sealed class NetworkCompatibility {
        private readonly HashSet<string> supported;
        private readonly Dictionary<string, HashSet<string>> enumValues;
        public IReadOnlyDictionary<string, string> DisabledTypes { get; }
        /// <summary>Whether both peers support adaptive compression of SocketJack binary objects.</summary>
        public bool BinaryCompression { get; internal set; }
        /// <summary>Definitions and learned compression state are isolated to this connection.</summary>
        public bool BinaryCompressionPerClient { get; internal set; }
        internal NetworkCompatibility(IEnumerable<string> allowed, IDictionary<string, string> disabled, Dictionary<string, HashSet<string>> enums = null) {
            supported = new HashSet<string>(allowed, StringComparer.Ordinal);
            enumValues = enums ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            DisabledTypes = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(disabled));
        }
        public bool Supports(Type type) => type != null && (supported.Contains(WireContracts.Name(type)) ||
            (type.IsArray && Supports(type.GetElementType())));
        internal string Reason(Type type) => DisabledTypes.TryGetValue(WireContracts.Name(type), out var reason)
            ? reason : "The peer does not support this message type.";
        public bool SupportsEnum(Enum value) => value != null && enumValues.TryGetValue(WireContracts.Name(value.GetType()), out var values) &&
            values.Contains(Enum.Format(value.GetType(), value, "D"));
        internal Type UnsupportedEnum(object value, HashSet<object> seen, int depth = 0, bool includeFields = false) {
            if (value == null) return null;
            Type type = value.GetType();
            if (value is Enum e) return SupportsEnum(e) ? null : type;
            if (type.IsPrimitive || value is string || value is byte[] || value is decimal || value is DateTime || value is Guid || value is TimeSpan) return null;
            if (type.IsArray && type.GetElementType().IsPrimitive) return null;
            if (depth > 32 || seen.Count > 1024) return type;
            if (!seen.Add(value)) return null;
            if (value is System.Collections.IEnumerable sequence) {
                foreach (object item in sequence) { var bad = UnsupportedEnum(item, seen, depth + 1, includeFields); if (bad != null) return bad; }
                return null;
            }
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)) {
                if (property.GetIndexParameters().Length != 0 || property.GetMethod == null || (!property.GetMethod.IsPublic && !property.IsDefined(typeof(JsonIncludeAttribute))) ||
                    property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                var bad = UnsupportedEnum(property.GetValue(value), seen, depth + 1, includeFields); if (bad != null) return bad;
            }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
                if ((!field.IsDefined(typeof(JsonIncludeAttribute)) && !(includeFields && field.IsPublic)) || field.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                var bad = UnsupportedEnum(field.GetValue(value), seen, depth + 1, includeFields); if (bad != null) return bad;
            }
            return null;
        }
    }

    // Only locally reflected types create schemas. Remote schemas are bounded inert data;
    // they never cause type loading, activation, allowlist expansion, or authorization.
    internal static class WireContracts {
        internal sealed class Manifest {
            public string Codec { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool BinaryCompression { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool BinaryCompressionIsolation { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
            public bool BinaryCompressionPerClient { get; set; }
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public byte[] BinaryDictionary { get; set; }
            public string[] Roots { get; set; }
            public Dictionary<string, Shape> Types { get; set; }
        }
        internal sealed class Shape {
            public string Kind { get; set; }
            public Dictionary<string, string> Members { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
            public string[] Required { get; set; } = Array.Empty<string>();
            public string[] Arguments { get; set; } = Array.Empty<string>();
        }
        internal static string Name(Type type) {
            if (type.IsArray) return Name(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            return type.IsGenericType ? type.GetGenericTypeDefinition().FullName + "<" +
                string.Join(",", type.GetGenericArguments().Select(Name)) + ">" : type.FullName;
        }
        internal static Manifest Create(NetworkOptions options) {
            var json = (options.Serializer as Serialization.Json.JsonSerializer)?.JsonOptions ?? new JsonSerializerOptions();
            string codec = options.Serializer is BinarySerializer ? "SB1" :
                options.Serializer?.GetType() == typeof(Serialization.Json.JsonSerializer) ? "JSON1" : null;
            if (codec == null) throw new SecurityException("Custom serializers require an explicitly defined compatibility protocol or strict SafeMode.");
            codec += ":" + options.UseCompression + ":" + (options.UseCompression ? options.CompressionAlgorithm.GetType().FullName : "") +
                ":" + options.EnablePatternCache + ":" + json.NumberHandling + ":" + json.UnmappedMemberHandling;
            var manifest = new Manifest { Codec = codec, Types = new Dictionary<string, Shape>(StringComparer.Ordinal),
                BinaryCompression = options.Serializer is BinarySerializer binary && binary.EnableCompression };
            manifest.BinaryCompressionIsolation = manifest.BinaryCompression;
            manifest.BinaryCompressionPerClient = manifest.BinaryCompression && !((BinarySerializer)options.Serializer).ShareCompressionDictionary;
            var roots = new List<string>();
            foreach (string entry in options.Whitelist.ToArray()) {
                Type type = options.Whitelist.Resolve(entry);
                if (type == null || options.Blacklist.Contains(type)) throw SafeModeHandshake.Rejected();
                options.VerifiedAssemblies.Verify(type.Assembly);
                roots.Add(Name(type));
                Add(type, manifest.Types, json, 0);
            }
            manifest.Roots = roots.Distinct(StringComparer.Ordinal).ToArray();
            // Reliable stream transfers have their own versioned binary frames, not CLR DTO serialization.
            string transfer = Name(typeof(UdpTransferRequest));
            manifest.Types[transfer] = new Shape { Kind = "udp-transfer:1" };
            manifest.Roots = manifest.Roots.Concat(new[] { transfer }).Distinct(StringComparer.Ordinal).ToArray();
            return manifest;
        }
        private static void Add(Type type, Dictionary<string, Shape> shapes, JsonSerializerOptions json, int depth) {
            string name = Name(type);
            if (shapes.ContainsKey(name)) return;
            if (depth > 32 || shapes.Count >= 512) throw new SecurityException("Compatibility schema is too complex.");
            var shape = new Shape(); shapes.Add(name, shape);
            if (type.GetCustomAttribute<JsonConverterAttribute>() != null || type.GetCustomAttribute<JsonNumberHandlingAttribute>() != null ||
                json.Converters.Any(c => c.CanConvert(type) && c.GetType() != typeof(Serialization.Json.Converters.ByteArrayConverter))) {
                shape.Kind = "opaque"; return;
            }
            // Payloads of these inert protocol envelopes are independently checked.
            if (type == typeof(Wrapper) || type == typeof(P2P.PeerRedirect)) { shape.Kind = "envelope:1:" + name; return; }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) ||
                type == typeof(DateTimeOffset) || type == typeof(Guid) || type == typeof(TimeSpan) || type == typeof(byte[])) {
                shape.Kind = "scalar:" + name; return;
            }
            if (type.IsEnum) {
                shape.Kind = "enum:" + Name(Enum.GetUnderlyingType(type));
                foreach (string value in Enum.GetNames(type)) shape.Members[value] = Enum.Format(type, Enum.Parse(type, value), "D");
                return;
            }
            if (type.IsArray || (type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Nullable<>) ||
                typeof(System.Collections.IEnumerable).IsAssignableFrom(type)))) {
                shape.Kind = type.IsArray ? "array:" + type.GetArrayRank() : "collection:" + type.GetGenericTypeDefinition().FullName;
                Type[] args = type.IsArray ? new[] { type.GetElementType() } : type.GetGenericArguments();
                shape.Arguments = args.Select(Name).ToArray();
                foreach (var arg in args) Add(arg, shapes, json, depth + 1);
                return;
            }
            // Arbitrary converters/polymorphic payloads cannot be inferred from CLR properties.
            if (type == typeof(object) || type.GetCustomAttribute<JsonConverterAttribute>() != null ||
                type.GetCustomAttributes<JsonDerivedTypeAttribute>().Any() || type.GetCustomAttribute<JsonPolymorphicAttribute>() != null ||
                json.Converters.Any(c => c.CanConvert(type))) { shape.Kind = "opaque"; return; }
            var unmapped = type.GetCustomAttribute<JsonUnmappedMemberHandlingAttribute>()?.UnmappedMemberHandling ?? json.UnmappedMemberHandling;
            shape.Kind = unmapped == JsonUnmappedMemberHandling.Disallow ? "object-strict" : "object";
            var required = new List<string>();
            foreach (var member in type.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .OrderBy(m => m.Name, StringComparer.Ordinal)) {
                Type memberType;
                if (member is PropertyInfo p && p.GetIndexParameters().Length == 0 &&
                    (p.GetMethod?.IsPublic == true || p.IsDefined(typeof(JsonIncludeAttribute)))) memberType = p.PropertyType;
                else if (member is FieldInfo f && !f.IsStatic && (f.IsDefined(typeof(JsonIncludeAttribute)) || (json.IncludeFields && f.IsPublic))) memberType = f.FieldType;
                else continue;
                if (member.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                if (member.IsDefined(typeof(JsonConverterAttribute)) || member.IsDefined(typeof(JsonExtensionDataAttribute)) || member.IsDefined(typeof(JsonNumberHandlingAttribute))) { shape.Kind = "opaque"; return; }
                string key = member.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? json.PropertyNamingPolicy?.ConvertName(member.Name) ?? member.Name;
                if (!shape.Members.TryAdd(key, Name(memberType))) { shape.Kind = "opaque"; return; }
                if (member.IsDefined(typeof(JsonRequiredAttribute)) || member.CustomAttributes.Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute")) required.Add(key);
                Add(memberType, shapes, json, depth + 1);
            }
            shape.Required = required.ToArray();
        }
        internal static NetworkCompatibility Compare(Manifest local, Manifest peer) {
            Validate(peer);
            if (local.Codec != peer.Codec) throw new SecurityException("SocketJack wire serializer/compression settings are incompatible.");
            var allowed = new List<string>(); var disabled = new Dictionary<string, string>(StringComparer.Ordinal);
            var peerRoots = new HashSet<string>(peer.Roots, StringComparer.Ordinal);
            foreach (string name in local.Roots) {
                if (!peerRoots.Contains(name)) disabled[name] = "This feature is unavailable in the connected peer's version.";
                else if (!Compatible(name, local.Types, peer.Types, new HashSet<string>(StringComparer.Ordinal)))
                    disabled[name] = "This feature uses a message contract incompatible with the connected peer's version.";
                else allowed.Add(name);
            }
            var enums = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var entry in local.Types.Where(p => p.Value.Kind.StartsWith("enum:", StringComparison.Ordinal))) {
                if (peer.Types.TryGetValue(entry.Key, out var other) && other.Kind == entry.Value.Kind)
                    enums[entry.Key] = new HashSet<string>(entry.Value.Members.Where(p => other.Members.TryGetValue(p.Key, out var v) && v == p.Value).Select(p => p.Value), StringComparer.Ordinal);
            }
            bool compressed = local.BinaryCompression && peer.BinaryCompression;
            bool perClient = local.BinaryCompressionPerClient || peer.BinaryCompressionPerClient;
            // An older implementation can ignore a private-cache request while still
            // enabling shared compression. Reject that ambiguous negotiation explicitly.
            if (compressed && perClient && (!local.BinaryCompressionIsolation || !peer.BinaryCompressionIsolation))
                throw new SecurityException("The peer must support per-client binary compression isolation, or disable binary compression before reconnecting.");
            return new NetworkCompatibility(allowed, disabled, enums) { BinaryCompression = compressed, BinaryCompressionPerClient = perClient };
        }
        private static bool Compatible(string name, Dictionary<string, Shape> left, Dictionary<string, Shape> right, HashSet<string> seen) {
            if (!left.TryGetValue(name, out var a) || !right.TryGetValue(name, out var b) || a.Kind != b.Kind || a.Kind == "opaque") return false;
            if (!seen.Add(name)) return true;
            if (!a.Arguments.SequenceEqual(b.Arguments)) return false;
            foreach (string arg in a.Arguments) if (!Compatible(arg, left, right, seen)) return false;
            if (a.Kind.StartsWith("enum:", StringComparison.Ordinal)) return a.Members.Any(p => b.Members.TryGetValue(p.Key, out var v) && v == p.Value);
            if (a.Kind == "object-strict" && (a.Members.Count != b.Members.Count || a.Members.Keys.Any(k => !b.Members.ContainsKey(k)))) return false;
            // Added optional fields are compatible. Required fields must exist on both sides.
            if (a.Required.Any(k => !b.Members.ContainsKey(k)) || b.Required.Any(k => !a.Members.ContainsKey(k))) return false;
            foreach (var member in a.Members) if (b.Members.TryGetValue(member.Key, out var other) &&
                (member.Value != other || !Compatible(member.Value, left, right, seen))) return false;
            return true;
        }
        private static void Validate(Manifest manifest) {
            if (manifest?.BinaryCompressionPerClient == true && (!manifest.BinaryCompression || !manifest.BinaryCompressionIsolation)) throw SafeModeHandshake.Rejected();
            if (manifest?.BinaryDictionary != null && (!manifest.BinaryCompression || manifest.BinaryDictionary.Length > 4096)) throw SafeModeHandshake.Rejected();
            if (manifest?.Codec == null || manifest.Codec.Length > 1024 || manifest.Roots == null || manifest.Roots.Length == 0 ||
                manifest.Roots.Length > 256 || manifest.Roots.Distinct(StringComparer.Ordinal).Count() != manifest.Roots.Length ||
                manifest.Types == null || manifest.Types.Count > 512) throw SafeModeHandshake.Rejected();
            foreach (var pair in manifest.Types) {
                var s = pair.Value;
                if (string.IsNullOrEmpty(pair.Key) || pair.Key.Length > 2048 || s?.Kind == null || s.Kind.Length > 2048 ||
                    s.Members == null || s.Members.Count > 256 || s.Required == null || s.Arguments == null || s.Arguments.Length > 32 ||
                    s.Required.Any(k => k == null || !s.Members.ContainsKey(k)) ||
                    s.Members.Any(m => string.IsNullOrEmpty(m.Key) || m.Key.Length > 2048 || m.Value == null || m.Value.Length > 2048) ||
                    s.Arguments.Any(a => a == null || !manifest.Types.ContainsKey(a))) throw SafeModeHandshake.Rejected();
            }
            if (manifest.Roots.Any(r => r == null || !manifest.Types.ContainsKey(r))) throw SafeModeHandshake.Rejected();
        }
    }

    public partial class NetworkConnection {
        public NetworkCompatibility Compatibility { get; internal set; }
        /// <summary>Checks an application feature before starting work; raises FeatureDisabled on the socket options when unavailable.</summary>
        public bool TryUseFeature(string feature, params Type[] messageTypes) {
            foreach (Type type in messageTypes) {
                if (Compatibility == null || Compatibility.Supports(type)) continue;
                Parent.Options.RaiseFeatureDisabled(new FeatureDisabledEventArgs(this, feature, type, Compatibility.Reason(type)));
                return false;
            }
            return true;
        }
        internal bool CanSend(object value) {
            if (value is BinaryCompressionControl) return BinaryNetworkCompression.Enabled(Parent, this);
            if (value == null) return true;
            if (!TryUseFeature(value.GetType().Name, value.GetType())) return false;
            if (value is P2P.PeerRedirect redirect) return CanSend(redirect.Value);
            if (Compatibility != null) {
                var unsupported = Compatibility.UnsupportedEnum(value, new HashSet<object>(), includeFields: (Parent.Options.Serializer as Serialization.Json.JsonSerializer)?.JsonOptions.IncludeFields == true);
                if (unsupported != null) {
                    Parent.Options.RaiseFeatureDisabled(new FeatureDisabledEventArgs(this, value.GetType().Name, unsupported, "This operation is unavailable in the connected peer's version."));
                    return false;
                }
            }
            return true;
        }
        public bool TryUseFeatureValue(string feature, Enum value) {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (Compatibility == null || Compatibility.SupportsEnum(value)) return true;
            Parent.Options.RaiseFeatureDisabled(new FeatureDisabledEventArgs(this, feature, value.GetType(), "This operation is unavailable in the connected peer's version."));
            return false;
        }
    }
}
