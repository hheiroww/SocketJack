using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Schema;

namespace SocketJack.Net {
    public sealed partial class ApplicationWebSocket {
        /// <summary>Returns the browser client as a dependency-free ECMAScript module.</summary>
        public static string GetBrowserClientModule() {
            using var stream = typeof(ApplicationWebSocket).Assembly.GetManifestResourceStream("SocketJack.ApplicationWebSocket.mjs");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        /// <summary>Generates TypeScript declarations from the browser JSON contract.</summary>
        public string GenerateClientDeclarations() {
            var text = new StringBuilder("export interface Operations {\n");
            using var manifest = JsonDocument.Parse(GenerateManifest());
            foreach (var operation in manifest.RootElement.GetProperty("operations").EnumerateArray()) {
                text.Append("  ").Append(operation.GetProperty("name").GetRawText()).Append(": { input: ")
                    .Append(ToTypeScript(operation.GetProperty("input"))).Append("; output: ")
                    .Append(ToTypeScript(operation.GetProperty("output"))).AppendLine(" };");
            }
            text.AppendLine("  'session.describe': { input: null; output: { authenticated: boolean; subject?: string | null } };");
            text.AppendLine("  'session.signIn': { input: { username: string; password: string }; output: { authenticated: true; subject: string; token: string } };");
            text.AppendLine("  'session.resume': { input: { token: string }; output: { authenticated: true; subject: string } };");
            text.AppendLine("  'session.signOut': { input: null; output: { authenticated: false } };");
            text.AppendLine("}");
            text.AppendLine("export class SocketJackApplicationClient {");
            text.AppendLine("  constructor(endpoint: string | URL);");
            text.AppendLine("  invoke<K extends keyof Operations>(operation: K, input: Operations[K]['input'], signal?: AbortSignal): Promise<Operations[K]['output']>;");
            text.AppendLine("  invoke<K extends keyof Operations>(operation: K, input: Operations[K]['input'], signal: AbortSignal | undefined, binary: true): Promise<{ metadata: Operations[K]['output']; bytes: ArrayBuffer }>;");
            text.AppendLine("  appendMedia(id: string, offset: number, sha256: string, bytes: ArrayBuffer, signal?: AbortSignal): Promise<Operations['media.appendBinary']['output']>;");
            text.AppendLine("  close(): void;");
            text.AppendLine("}");
            return text.ToString();
        }

        private static string ToTypeScript(JsonElement schema) {
            if (schema.ValueKind != JsonValueKind.Object) return "unknown";
            if (schema.TryGetProperty("enum", out var values))
                return string.Join(" | ", values.EnumerateArray().Select(value => value.GetRawText()));
            if (schema.TryGetProperty("anyOf", out var alternatives))
                return string.Join(" | ", alternatives.EnumerateArray().Select(ToTypeScript));
            if (!schema.TryGetProperty("type", out var type)) return "unknown";
            return type.ValueKind == JsonValueKind.Array
                ? string.Join(" | ", type.EnumerateArray().Select(item => ToTypeScriptKind(schema, item.GetString())))
                : ToTypeScriptKind(schema, type.GetString());
        }

        private static string ToTypeScriptKind(JsonElement schema, string kind) {
            switch (kind) {
                case "string": case "boolean": case "null": return kind;
                case "integer": case "number": return "number";
                case "array":
                    return "Array<" + (schema.TryGetProperty("items", out var items) ? ToTypeScript(items) : "unknown") + ">";
                case "object":
                    if (!schema.TryGetProperty("properties", out var properties))
                        return schema.TryGetProperty("additionalProperties", out var additional)
                            ? "Record<string, " + ToTypeScript(additional) + ">" : "Record<string, unknown>";
                    var required = schema.TryGetProperty("required", out var names)
                        ? names.EnumerateArray().Select(name => name.GetString()).ToArray() : Array.Empty<string>();
                    return "{ " + string.Join("; ", properties.EnumerateObject().Select(property =>
                        JsonSerializer.Serialize(property.Name) + (required.Contains(property.Name) ? "" : "?") + ": " + ToTypeScript(property.Value))) + " }";
                default: return "unknown";
            }
        }
    }
}
