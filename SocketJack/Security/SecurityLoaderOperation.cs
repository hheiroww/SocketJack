using System;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Security {
    /// <summary>The shared entry contract for an approved managed operation running in the publisher's isolated execution service.</summary>
    public interface ISecurityLoaderOperation {
        Task<SecurityLoaderOperationResult> ExecuteAsync(SecurityLoaderOperationInput input, CancellationToken cancellationToken = default);
    }

    /// <summary>Optional artifact access implemented by runtimes that support bounded job artifacts.</summary>
    public interface ISecurityLoaderArtifacts {
        Task<byte[]> GetArtifactAsync(string jobId, int artifactIndex, CancellationToken cancellationToken = default);
    }

    /// <summary>Resolved operation data supplied by the service. Object authorization and execution isolation remain service responsibilities.</summary>
    public sealed class SecurityLoaderOperationInput {
        public const int MaximumInputs = 16;
        public const int MaximumInputBytes = 64 * 1024 * 1024;
        private readonly byte[][] inputs;

        [JsonConstructor]
        public SecurityLoaderOperationInput(string operation, string argumentsJson = "{}", byte[][] inputs = null) {
            Operation = SecurityLoaderContract.Identifier(operation, nameof(operation));
            ArgumentsJson = SecurityLoaderContract.Json(argumentsJson, SecurityLoaderRequest.MaximumArgumentsUtf8Bytes, nameof(argumentsJson), requireObject: true);
            this.inputs = SecurityLoaderOperationBuffers.Copy(inputs, MaximumInputs, MaximumInputBytes, nameof(inputs));
        }

        public string Operation { get; }
        public string ArgumentsJson { get; }
        public byte[][] Inputs => SecurityLoaderOperationBuffers.Copy(inputs, MaximumInputs, MaximumInputBytes, nameof(Inputs));
    }

    /// <summary>A structured operation result and optional byte artifacts. Construction and reads defensively copy buffers.</summary>
    public sealed class SecurityLoaderOperationResult {
        public const int MaximumArtifacts = 16;
        public const int MaximumArtifactBytes = 64 * 1024 * 1024;
        private readonly byte[][] artifacts;

        [JsonConstructor]
        public SecurityLoaderOperationResult(string resultJson = "{}", byte[][] artifacts = null) {
            ResultJson = SecurityLoaderContract.Json(resultJson, SecurityLoaderJob.MaximumResultUtf8Bytes, nameof(resultJson), requireObject: false);
            this.artifacts = SecurityLoaderOperationBuffers.Copy(artifacts, MaximumArtifacts, MaximumArtifactBytes, nameof(artifacts));
        }

        public string ResultJson { get; }
        public byte[][] Artifacts => SecurityLoaderOperationBuffers.Copy(artifacts, MaximumArtifacts, MaximumArtifactBytes, nameof(Artifacts));
    }

    internal static class SecurityLoaderOperationBuffers {
        internal static byte[][] Copy(byte[][] values, int maximumCount, int maximumBytes, string parameter) {
            if (values == null) return Array.Empty<byte[]>();
            if (values.Length > maximumCount) throw new ArgumentException("The operation exceeds its supported buffer count.", parameter);
            var snapshot = (byte[][])values.Clone();
            int total = 0;
            foreach (var value in snapshot) {
                if (value == null || value.Length > maximumBytes - total)
                    throw new ArgumentException("Operation buffers must be non-null and stay within their aggregate byte limit.", parameter);
                total += value.Length;
            }
            var result = new byte[snapshot.Length][];
            for (int index = 0; index < snapshot.Length; index++) result[index] = (byte[])snapshot[index].Clone();
            return result;
        }
    }
}
