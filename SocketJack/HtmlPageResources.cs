using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SocketJack
{
    /// <summary>
    /// Serves project HTML pages from embedded resources so runtime routes do not depend on
    /// loose files under the application directory.
    /// </summary>
    public static class HtmlPageResources
    {
        private const string ResourcePrefix = "SocketJack.Html.";
        private static readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, byte[]> BinaryCache = new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Optional development override. Return null to retain normal embedded content and caching.</summary>
        public static Func<string, byte[]> ResourceOverrideProvider { get; set; }

        /// <summary>
        /// Reads an embedded HTML page by file name and caches the decoded UTF-8 content.
        /// </summary>
        public static string GetHtml(string fileName)
        {
            string normalized = NormalizeFileName(fileName);
            byte[] overrideBytes = ResourceOverrideProvider?.Invoke(normalized);
            if (overrideBytes != null) return Encoding.UTF8.GetString(overrideBytes);
            return string.IsNullOrWhiteSpace(normalized)
                ? string.Empty
                : Cache.GetOrAdd(normalized, LoadHtml);
        }

        /// <summary>
        /// Attempts to read an embedded HTML page by file name.
        /// </summary>
        public static bool TryGetHtml(string fileName, out string html)
        {
            html = GetHtml(fileName);
            return !string.IsNullOrEmpty(html);
        }

        /// <summary>
        /// Reads an embedded binary page asset by file name and caches the bytes.
        /// </summary>
        public static byte[] GetBytes(string fileName)
        {
            string normalized = NormalizeFileName(fileName);
            byte[] overrideBytes = ResourceOverrideProvider?.Invoke(normalized);
            if (overrideBytes != null) return overrideBytes;
            return string.IsNullOrWhiteSpace(normalized)
                ? Array.Empty<byte>()
                : BinaryCache.GetOrAdd(normalized, LoadBytes);
        }

        /// <summary>
        /// Attempts to read an embedded binary page asset by file name.
        /// </summary>
        public static bool TryGetBytes(string fileName, out byte[] bytes)
        {
            bytes = GetBytes(fileName);
            return bytes.Length > 0;
        }

        private static string NormalizeFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            return Path.GetFileName(fileName.Replace('\\', '/'));
        }

        private static string LoadHtml(string fileName)
        {
            Assembly assembly = typeof(HtmlPageResources).Assembly;
            string resourceName = ResourcePrefix + fileName;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName) ?? FindResourceStream(assembly, fileName))
            {
                if (stream == null)
                    return string.Empty;

                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }

        private static byte[] LoadBytes(string fileName)
        {
            Assembly assembly = typeof(HtmlPageResources).Assembly;
            string resourceName = ResourcePrefix + fileName;
            using (Stream stream = assembly.GetManifestResourceStream(resourceName) ?? FindResourceStream(assembly, fileName))
            {
                if (stream == null)
                    return Array.Empty<byte>();

                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return memory.ToArray();
                }
            }
        }

        private static Stream FindResourceStream(Assembly assembly, string fileName)
        {
            string suffix = "." + fileName;
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(resourceName) ? null : assembly.GetManifestResourceStream(resourceName);
        }
    }
}
