using System;

namespace SocketJack.Net {
    /// <summary>Fetch destination for a document's critical resource.</summary>
    public enum HttpPreloadDestination {
        Style,
        Script,
        Image,
        Font
    }

    /// <summary>Creates HTTP Link values for resources a browser can discover before parsing the document.</summary>
    public static class HttpResourceHints {
        /// <summary>Creates a preload value for an already escaped root-relative or HTTP(S) resource URL.</summary>
        /// <param name="url">The same URL used by the document, including any version query.</param>
        /// <param name="destination">The resource type used by its consuming element.</param>
        /// <param name="anonymousCors">Use anonymous CORS when the consuming element does. Font preloads always use anonymous CORS.</param>
        public static string Preload(string url, HttpPreloadDestination destination, bool anonymousCors = false) {
            string token = destination switch {
                HttpPreloadDestination.Style => "style",
                HttpPreloadDestination.Script => "script",
                HttpPreloadDestination.Image => "image",
                HttpPreloadDestination.Font => "font",
                _ => throw new ArgumentOutOfRangeException(nameof(destination))
            };
            ValidateUrl(url);
            string cors = anonymousCors || destination == HttpPreloadDestination.Font ? "; crossorigin=anonymous" : "";
            return $"<{url}>; rel=preload; as={token}{cors}";
        }

        /// <summary>Creates a modulepreload value for the exact URL of an ES module.</summary>
        public static string ModulePreload(string url) {
            ValidateUrl(url);
            return $"<{url}>; rel=modulepreload";
        }

        private static void ValidateUrl(string url) {
            if (string.IsNullOrEmpty(url))
                throw new ArgumentException("A preload resource URL is required.", nameof(url));
            foreach (char character in url) {
                if (character <= ' ' || character >= '\u007f' || character == '<' || character == '>' ||
                    character == '"' || character == '\\' || character == ',' || character == '#')
                    throw new ArgumentException("Use an escaped URL without a fragment for a preload resource.", nameof(url));
            }
            if (url[0] == '/' && (url.Length == 1 || url[1] != '/')) return;
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Host.Length > 0 &&
                absolute.UserInfo.Length == 0 && (absolute.Scheme == "http" || absolute.Scheme == "https")) return;
            throw new ArgumentException("Use a root-relative URL or an HTTP(S) URL without credentials.", nameof(url));
        }
    }
}
