using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    // V2 exchanges schemas; only legacy clients send fingerprints. Neither authenticates or attests a peer.
    internal static class SafeModeHandshake {
        internal const int MaximumBytes = 60000;
        internal const string HeaderName = "X-SocketJack-Client-Dlls";
        internal static readonly byte[] Accepted = Encoding.ASCII.GetBytes("SJOK1");
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("SJSM1");
        static readonly byte[] CompatibilityMagic = Encoding.ASCII.GetBytes("SJSM2");
        internal const string CompatibilityHeader = "X-SocketJack-Compatibility";
        internal static bool IsCompatibility(byte[] data) => data != null && data.Length >= 5 && data.Take(5).SequenceEqual(CompatibilityMagic);
        private static byte[] EncodeManifest(NetworkOptions options, NetworkConnection connection = null, bool snapshot = false) {
            var manifest = WireContracts.Create(options);
            if (manifest.BinaryCompression && connection?.Compatibility != null)
                manifest.BinaryCompressionPerClient = connection.Compatibility.BinaryCompressionPerClient;
            if (snapshot && connection?.Compatibility?.BinaryCompression == true)
                manifest.BinaryDictionary = BinaryNetworkCompression.Snapshot(connection.Parent, connection);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (json.Length + 5 > MaximumBytes && manifest.BinaryDictionary != null) {
                manifest.BinaryDictionary = null;
                json = JsonSerializer.SerializeToUtf8Bytes(manifest);
            }
            if (json.Length + 5 > MaximumBytes) throw new SecurityException("SocketJack compatibility manifest is too large.");
            return CompatibilityMagic.Concat(json).ToArray();
        }
        internal static byte[] Reply(NetworkOptions options, NetworkConnection connection) => connection?.Compatibility == null ? Accepted : EncodeManifest(options, connection, true);
        internal static string ReplyHeader(NetworkOptions options, NetworkConnection connection) => connection?.Compatibility == null ? "" :
            CompatibilityHeader + ": " + Convert.ToBase64String(EncodeManifest(options, connection)) + "\r\n";
        internal static void AcceptReply(byte[] data, NetworkOptions options, NetworkConnection connection) {
            if (IsCompatibility(data) && options.EnableCompatibilityNegotiation) { Validate(data, options, connection); return; }
            if (!options.EnableCompatibilityNegotiation && data.SequenceEqual(Accepted)) { MarkVerified(connection, options); return; }
            throw new SecurityException("The server does not support SocketJack compatibility negotiation. Update the server; no unsafe fallback was attempted.");
        }
        internal static void AcceptHttpReply(string response, NetworkOptions options, NetworkConnection connection) {
            if (!options.SafeMode) return;
            if (!options.EnableCompatibilityNegotiation) { MarkVerified(connection, options); return; }
            var lines = response.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => l.StartsWith(CompatibilityHeader + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (lines.Length != 1 || lines[0].Length > MaximumBytes * 2) throw new SecurityException("The server does not support SocketJack compatibility negotiation. Update the server.");
            AcceptReply(Convert.FromBase64String(lines[0].Substring(CompatibilityHeader.Length + 1).Trim()), options, connection);
        }
        internal sealed class Claim { public string Type { get; set; } public string Md5 { get; set; } public string Sha256 { get; set; } }
        static Dictionary<string, Claim> Local(NetworkOptions options) {
            var result = new Dictionary<string, Claim>(StringComparer.Ordinal);
            var hashes = new Dictionary<System.Reflection.Assembly, (string md5, string sha)>();
            foreach (string name in options.Whitelist.ToArray()) {
                Type type = options.Whitelist.Resolve(name);
                if (type == null || options.Blacklist.Contains(type)) throw new SecurityException("Invalid local SafeMode allowlist.");
                options.VerifiedAssemblies.Verify(type.Assembly);
                if (!hashes.TryGetValue(type.Assembly, out var pair)) {
                    pair = (VerifiedAssemblyRegistry.Fingerprint(type.Assembly, true), VerifiedAssemblyRegistry.Fingerprint(type.Assembly, false));
                    hashes.Add(type.Assembly, pair);
                }
                result[type.FullName] = new Claim { Type = type.FullName, Md5 = pair.md5, Sha256 = pair.sha };
            }
            if (result.Count == 0 || result.Count > 256) throw new SecurityException("Invalid local SafeMode allowlist size.");
            return result;
        }
        internal static byte[] Create(NetworkOptions options) {
            if (options.EnableCompatibilityNegotiation) return EncodeManifest(options);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(Local(options).Values.ToArray());
            if (json.Length + Magic.Length > MaximumBytes) throw new SecurityException("SafeMode handshake is too large.");
            byte[] data = new byte[json.Length + Magic.Length];
            Buffer.BlockCopy(Magic, 0, data, 0, Magic.Length);
            Buffer.BlockCopy(json, 0, data, Magic.Length, json.Length);
            return data;
        }
        internal static bool HasMagic(byte[] data) => IsCompatibility(data) || (data != null && data.Length >= Magic.Length && data.Take(Magic.Length).SequenceEqual(Magic));
        internal static void Validate(byte[] data, NetworkOptions options, NetworkConnection connection) {
            // Bounded, inert JSON strings only; never use the application deserializer for a handshake.
            if (!HasMagic(data) || data.Length > MaximumBytes) throw Rejected();
            if (IsCompatibility(data)) {
                if (!options.EnableCompatibilityNegotiation || connection == null) throw Rejected();
                using (var schema = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, 5, data.Length - 5), new JsonDocumentOptions { MaxDepth = 8 })) {
                    RejectDuplicateFields(schema.RootElement);
                    var peer = JsonSerializer.Deserialize<WireContracts.Manifest>(schema.RootElement.GetRawText());
                    connection.Compatibility = WireContracts.Compare(WireContracts.Create(options), peer);
                    if (connection.Parent is UdpServer udp && connection.EndPoint != null)
                        udp.ValidateCompressionScope(connection.EndPoint, connection.Compatibility);
                    if (connection.Compatibility.BinaryCompression && peer.BinaryDictionary != null)
                        BinaryNetworkCompression.ImportSnapshot(connection.Parent, peer.BinaryDictionary, connection);
                }
                MarkVerified(connection, options);
                return;
            }
            var expected = Local(options);
            using (var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(data, Magic.Length, data.Length - Magic.Length), new JsonDocumentOptions { MaxDepth = 4 })) {
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != expected.Count) throw Rejected();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in document.RootElement.EnumerateArray()) {
                    if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3) throw Rejected();
                    string name = entry.GetProperty("Type").GetString();
                    if (name == null || !seen.Add(name) || !expected.TryGetValue(name, out var approved) ||
                        !string.Equals(entry.GetProperty("Md5").GetString(), approved.Md5, StringComparison.Ordinal) ||
                        !string.Equals(entry.GetProperty("Sha256").GetString(), approved.Sha256, StringComparison.Ordinal)) throw Rejected();
                }
            }
            if (connection != null) MarkVerified(connection, options);
        }
        private static void RejectDuplicateFields(JsonElement value) {
            if (value.ValueKind == JsonValueKind.Object) {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var member in value.EnumerateObject()) {
                    if (!names.Add(member.Name)) throw Rejected();
                    RejectDuplicateFields(member.Value);
                }
            } else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateFields(item);
        }
        internal static void MarkVerified(NetworkConnection connection, NetworkOptions options) {
            connection.SafeModeAllowedTypes = options.Whitelist.ToArray();
            connection.SafeModeVerified = true;
        }
        internal static void RequireVerified(NetworkConnection connection, NetworkOptions options) {
            if (options.SafeMode && (connection == null || !connection.SafeModeVerified ||
                connection.SafeModeAllowedTypes == null || !connection.SafeModeAllowedTypes.SequenceEqual(options.Whitelist))) throw Rejected();
        }
        internal static SecurityException Rejected() => new SecurityException("SocketJack SafeMode handshake rejected.");
        internal static byte[] Frame(byte[] data) {
            byte[] frame = new byte[15 + data.Length];
            byte[] length = Encoding.ASCII.GetBytes(data.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Buffer.BlockCopy(length, 0, frame, 15 - length.Length, length.Length);
            Buffer.BlockCopy(data, 0, frame, 15, data.Length);
            return frame;
        }
        static async Task ReadExact(Stream stream, byte[] data, CancellationToken token) {
            int offset = 0;
            while (offset < data.Length) {
                int n = await stream.ReadAsync(data, offset, data.Length - offset, token).ConfigureAwait(false);
                if (n == 0) throw Rejected();
                offset += n;
            }
        }
        internal static async Task Exchange(NetworkConnection connection, NetworkOptions options, bool server) {
            if (!options.SafeMode) return; // WARNING: explicit unsafe legacy compatibility; never negotiate a downgrade.
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            using (timeout.Token.Register(() => { try { connection.Socket?.Close(); } catch { } })) {
                Stream stream = connection.Stream;
                if (server) {
                    byte[] header = new byte[15];
                    await ReadExact(stream, header, timeout.Token).ConfigureAwait(false);
                    if (!int.TryParse(Encoding.ASCII.GetString(header).Trim('\0'), out int size) || size < 5 || size > MaximumBytes) throw Rejected();
                    byte[] body = new byte[size];
                    await ReadExact(stream, body, timeout.Token).ConfigureAwait(false);
                    Validate(body, options, connection);
                    byte[] response = Reply(options, connection);
                    if (connection.Compatibility != null) response = Frame(response);
                    await stream.WriteAsync(response, 0, response.Length, timeout.Token).ConfigureAwait(false);
                } else {
                    byte[] request = Frame(Create(options));
                    await stream.WriteAsync(request, 0, request.Length, timeout.Token).ConfigureAwait(false);
                    byte[] reply = new byte[options.EnableCompatibilityNegotiation ? 15 : Accepted.Length];
                    await ReadExact(stream, reply, timeout.Token).ConfigureAwait(false);
                    if (options.EnableCompatibilityNegotiation) {
                        if (!int.TryParse(Encoding.ASCII.GetString(reply).Trim('\0'), out int size) || size < 5 || size > MaximumBytes) throw Rejected();
                        reply = new byte[size]; await ReadExact(stream, reply, timeout.Token).ConfigureAwait(false);
                    }
                    AcceptReply(reply, options, connection);
                }
            }
            BinaryNetworkCompression.Synchronize(connection.Parent, connection);
        }
        internal static bool LooksLikeWrapper(byte[] body) {
            if (body == null || body.Length == 0) return false;
            if (body.Length >= 3 && body[0] == 'S' && body[1] == 'B') return true;
            try {
                using (var json = JsonDocument.Parse(body))
                    return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Type", out _) && json.RootElement.TryGetProperty("value", out _);
            } catch (JsonException) { return false; }
        }
        internal static void ValidateHttp(string request, NetworkOptions options, NetworkConnection connection) {
            if (!options.SafeMode) return;
            var headers = request.Split(new[] { "\r\n" }, StringSplitOptions.None).Where(l => l.StartsWith(HeaderName + ":", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (headers.Length != 1 || headers[0].Length > MaximumBytes * 2) throw Rejected();
            Validate(Convert.FromBase64String(headers[0].Substring(HeaderName.Length + 1).Trim()), options, connection);
        }
    }
}
