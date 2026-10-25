using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Security {
    /// <summary>A snapshot supplied by the application's existing account system. Credentials are omitted from JSON and diagnostic text.</summary>
    public sealed class SecurityLoaderSession {
        public SecurityLoaderSession(string bearerToken = null, string sessionCookie = null, long revision = 0) {
            if (revision < 0) throw new ArgumentOutOfRangeException(nameof(revision));
            SecurityLoaderContract.Credential(bearerToken, nameof(bearerToken));
            SecurityLoaderContract.Credential(sessionCookie, nameof(sessionCookie));
            BearerToken = bearerToken;
            SessionCookie = sessionCookie;
            Revision = revision;
        }

        [JsonIgnore] public string BearerToken { get; }
        [JsonIgnore] public string SessionCookie { get; }
        public long Revision { get; }
        public override string ToString() => "SecurityLoaderSession (credentials redacted)";
    }

    /// <summary>An allowlisted operation and references to existing application objects. It never names a CLR type or method.</summary>
    public sealed class SecurityLoaderRequest {
        public const int MaximumArgumentsUtf8Bytes = 65536;
        public const int MaximumObjects = 16;

        public SecurityLoaderRequest(string operation, IEnumerable<SecurityLoaderObjectReference> objects = null, string argumentsJson = "{}") {
            Operation = SecurityLoaderContract.Identifier(operation, nameof(operation));
            var references = new List<SecurityLoaderObjectReference>();
            if (objects != null) {
                foreach (var reference in objects) {
                    if (reference == null) throw new ArgumentException("Object references cannot contain null entries.", nameof(objects));
                    if (references.Count >= MaximumObjects) throw new ArgumentException("An operation may reference at most 16 objects.", nameof(objects));
                    references.Add(reference);
                }
            }
            Objects = new ReadOnlyCollection<SecurityLoaderObjectReference>(references);
            ArgumentsJson = SecurityLoaderContract.Json(argumentsJson, MaximumArgumentsUtf8Bytes, nameof(argumentsJson), requireObject: true);
        }

        public string Operation { get; }
        public IReadOnlyList<SecurityLoaderObjectReference> Objects { get; }
        public string ArgumentsJson { get; }
    }

    /// <summary>A logical object identity resolved and authorized by the server, rather than a client-selected path or URL.</summary>
    public sealed class SecurityLoaderObjectReference {
        public SecurityLoaderObjectReference(string kind, string id, string version = null) {
            Kind = SecurityLoaderContract.Identifier(kind, nameof(kind));
            Id = SecurityLoaderContract.Identifier(id, nameof(id));
            Version = version == null ? null : SecurityLoaderContract.Identifier(version, nameof(version));
        }

        public string Kind { get; }
        public string Id { get; }
        public string Version { get; }
    }

    public enum SecurityLoaderJobState {
        Queued,
        Running,
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>A bounded result document. Error codes are logical identifiers, not worker output or exception stacks.</summary>
    public sealed class SecurityLoaderJob {
        public const int MaximumResultUtf8Bytes = 1048576;

        public SecurityLoaderJob(string id, SecurityLoaderJobState state, string resultJson = null, string errorCode = null)
            : this(id, state, resultJson, errorCode, 0) { }

        [JsonConstructor]
        public SecurityLoaderJob(string id, SecurityLoaderJobState state, string resultJson, string errorCode, int artifactCount = 0) {
            Id = SecurityLoaderContract.Identifier(id, nameof(id));
            if (!Enum.IsDefined(typeof(SecurityLoaderJobState), state)) throw new ArgumentOutOfRangeException(nameof(state));
            State = state;
            ResultJson = resultJson == null ? null : SecurityLoaderContract.Json(resultJson, MaximumResultUtf8Bytes, nameof(resultJson), requireObject: false);
            ErrorCode = errorCode == null ? null : SecurityLoaderContract.Identifier(errorCode, nameof(errorCode));
            if (artifactCount < 0 || artifactCount > SecurityLoaderOperationResult.MaximumArtifacts) throw new ArgumentOutOfRangeException(nameof(artifactCount));
            ArtifactCount = artifactCount;
        }

        public string Id { get; }
        public SecurityLoaderJobState State { get; }
        public string ResultJson { get; }
        public string ErrorCode { get; }
        public int ArtifactCount { get; }
    }

    internal static class SecurityLoaderContract {
        internal static string Identifier(string value, string parameter) {
            if (string.IsNullOrEmpty(value) || value.Length > 128 || value == "." || value == ".." ||
                value.Any(c => !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '.' && c != '-' && c != '_'))
                throw new ArgumentException("Use a logical identifier of at most 128 letters, digits, dots, hyphens or underscores.", parameter);
            return value;
        }

        internal static void Credential(string value, string parameter) {
            if (value != null && (value.Length > 8192 || value.Any(c => char.IsControl(c))))
                throw new ArgumentException("The account session contains invalid credential header data.", parameter);
        }

        internal static string Json(string value, int maximum, string parameter, bool requireObject) {
            if (value == null || value.Length > maximum || Encoding.UTF8.GetByteCount(value) > maximum)
                throw new ArgumentException("The JSON document exceeds the supported size.", parameter);
            try {
                using (var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 32 })) {
                    if (requireObject && document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("Operation arguments must be a JSON object.", parameter);
                }
            } catch (JsonException) {
                throw new ArgumentException("Provide a valid JSON document with at most 32 levels of nesting.", parameter);
            }
            return value;
        }
    }
}
