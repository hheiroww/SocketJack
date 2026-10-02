using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security;
using System.Security.Cryptography;

namespace SocketJack.Serialization {
    /// <summary>Local SHA-256 fingerprints for assemblies containing message types.
    /// Fingerprints are never sent to peers.</summary>
    public sealed class VerifiedAssemblyRegistry {
        private readonly ConcurrentDictionary<Assembly, string> pins = new ConcurrentDictionary<Assembly, string>();

        /// <summary>Approve an assembly and automatically SHA-256 hash its reflected DLL location.
        /// If the assembly was loaded from an embedded DLL, a matching loaded manifest resource is used.</summary>
        public void Add(Assembly assembly) {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            pins[assembly] = Fingerprint(assembly, false);
        }

        /// <summary>Approve a DLL only if its bytes match the trusted SHA-256 pin.
        /// This optional overload verifies the automatically discovered DLL against an external release pin.</summary>
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
                throw new SecurityException("An application message DLL has not been approved with a SHA-256 fingerprint.");
        }

        internal static string Fingerprint(Assembly assembly, bool md5) {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            if (assembly.IsDynamic) throw new SecurityException("SafeMode requires a verifiable assembly image.");

            string location = null;
            try { location = assembly.Location; } catch (NotSupportedException) { }
            if (!string.IsNullOrEmpty(location) && File.Exists(location)) {
                using (var file = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                    return Fingerprint(file, md5);
            }

            return FingerprintEmbeddedAssembly(assembly, md5);
        }

        private static string FingerprintEmbeddedAssembly(Assembly assembly, bool md5) {
            string fileName = assembly.GetName().Name + ".dll";
            Guid moduleVersionId = assembly.ManifestModule.ModuleVersionId;
            var fingerprints = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

            foreach (Assembly owner in AppDomain.CurrentDomain.GetAssemblies()) {
                if (owner.IsDynamic) continue;
                string[] names;
                try { names = owner.GetManifestResourceNames(); } catch (NotSupportedException) { continue; }
                foreach (string name in names.Where(candidate =>
                    string.Equals(candidate, fileName, StringComparison.OrdinalIgnoreCase) ||
                    candidate.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))) {
                    try {
                        using (Stream resource = owner.GetManifestResourceStream(name)) {
                            if (resource == null) continue;
                            Stream image = resource;
                            MemoryStream buffered = null;
                            if (!resource.CanSeek) {
                                buffered = new MemoryStream();
                                resource.CopyTo(buffered);
                                buffered.Position = 0;
                                image = buffered;
                            }
                            try {
                                using (var pe = new PEReader(image, PEStreamOptions.LeaveOpen)) {
                                    if (!pe.HasMetadata) continue;
                                    MetadataReader metadata = pe.GetMetadataReader();
                                    if (!metadata.IsAssembly || metadata.GetGuid(metadata.GetModuleDefinition().Mvid) != moduleVersionId) continue;
                                    string embeddedName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
                                    if (!string.Equals(embeddedName, assembly.GetName().Name, StringComparison.Ordinal)) continue;
                                }
                                image.Position = 0;
                                fingerprints.Add(Fingerprint(image, md5));
                            } finally { buffered?.Dispose(); }
                        }
                    } catch (BadImageFormatException) { }
                }
            }

            if (fingerprints.Count == 1) return fingerprints.First();
            if (fingerprints.Count > 1) throw new SecurityException("Multiple different embedded DLL images match the reflected assembly.");
            throw new SecurityException("SafeMode could not find the reflected assembly DLL or a matching embedded DLL resource.");
        }

        private static string Fingerprint(Stream stream, bool md5) {
            using (HashAlgorithm hash = md5 ? (HashAlgorithm)MD5.Create() : SHA256.Create()) {
                byte[] digest = hash.ComputeHash(stream);
                Span<char> encoded = stackalloc char[64]; const string hex = "0123456789abcdef";
                for (int i = 0; i < digest.Length; i++) { encoded[i * 2] = hex[digest[i] >> 4]; encoded[i * 2 + 1] = hex[digest[i] & 15]; }
                return new string(encoded.Slice(0, digest.Length * 2));
            }
        }
    }
}
