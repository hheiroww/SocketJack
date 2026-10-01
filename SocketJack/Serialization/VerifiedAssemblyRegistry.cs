using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;

namespace SocketJack.Serialization {
    /// <summary>Local, administrator-supplied SHA-256 pins for assemblies containing message types.
    /// Pins are never sent to peers. Obtain pins from a trusted build/release, not from a client.</summary>
    public sealed class VerifiedAssemblyRegistry {
        private readonly ConcurrentDictionary<Assembly, string> pins = new ConcurrentDictionary<Assembly, string>();

        /// <summary>Approve a DLL only if its bytes match the trusted SHA-256 pin.
        /// This verifies the local DLL; it cannot attest what a remote process executes.</summary>
        public void Add(Assembly assembly, string expectedSha256) {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (expectedSha256 == null || expectedSha256.Length != 64 ||
                !string.Equals(Fingerprint(assembly, false), expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("DLL verification failed.");
            pins[assembly] = expectedSha256.ToLowerInvariant();
        }

        internal VerifiedAssemblyRegistry Copy() {
            var copy = new VerifiedAssemblyRegistry();
            foreach (var pin in pins) copy.pins[pin.Key] = pin.Value;
            return copy;
        }

        internal void Verify(Assembly assembly) {
            // Framework and SocketJack protocol types are part of the trusted local runtime.
            if (assembly == typeof(Wrapper).Assembly || assembly == typeof(string).Assembly ||
                assembly == typeof(ConcurrentDictionary<,>).Assembly) return;
            if (!pins.TryGetValue(assembly, out string pin) ||
                !string.Equals(pin, Fingerprint(assembly, false), StringComparison.Ordinal))
                throw new SecurityException("An application message DLL has not been approved with a trusted SHA-256 pin.");
        }

        internal static string Fingerprint(Assembly assembly, bool md5) {
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                throw new SecurityException("SafeMode requires a verifiable assembly file.");
            using (var file = new FileStream(assembly.Location, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
            using (HashAlgorithm hash = md5 ? (HashAlgorithm)MD5.Create() : SHA256.Create()) {
                byte[] digest = hash.ComputeHash(file);
                Span<char> encoded = stackalloc char[64]; const string hex = "0123456789abcdef";
                for (int i = 0; i < digest.Length; i++) { encoded[i * 2] = hex[digest[i] >> 4]; encoded[i * 2 + 1] = hex[digest[i] & 15]; }
                return new string(encoded.Slice(0, digest.Length * 2));
            }
        }
    }
}
