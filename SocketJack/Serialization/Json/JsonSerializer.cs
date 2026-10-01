using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Serialization.Json.Converters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json {
    public class JsonSerializer : ISerializer {

#pragma warning disable CS8632 // Nullable annotation in non-nullable context
        public event Action<Exception>? DeserializationError;
#pragma warning restore CS8632

        public JsonSerializer() {
            JsonOptions.Converters.Add(new TypeConverter());
            JsonOptions.Converters.Add(new ByteArrayConverter());
#if NET6_0_OR_GREATER
            JsonOptions.Converters.Add(new BitmapConverter());
#endif
        }

        public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions() {
            DefaultBufferSize = 1048576,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 0,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        public bool HasConverter(Type type) {
            foreach (var converter in JsonOptions.Converters) {
                if (converter.CanConvert(type))
                    return true;
            }
            return false;
        }

        public byte[] Serialize(object Obj) {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Obj, JsonOptions);
        }

        public Wrapper Deserialize(byte[] bytes) {
            try {
                string json = Encoding.UTF8.GetString(bytes);
                return (Wrapper)System.Text.Json.JsonSerializer.Deserialize(json, typeof(Wrapper), JsonOptions);
            } catch (Exception ex) {
                DeserializationError?.Invoke(ex);
                return null;
            }
        }

        public PeerRedirect DeserializeRedirect(ISocket Target, byte[] bytes) {
            using (var document = JsonDocument.Parse(bytes)) {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) throw InboundMessageSecurity.Denied();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var field in root.EnumerateObject()) if (!names.Add(field.Name)) throw InboundMessageSecurity.Denied();
                string typeName = root.GetProperty("Type").GetString();
                var payload = root.GetProperty("Value");
                Wrapper wrapper;
                if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("Type", out var nested)) {
                    var declared = Target.Options.Whitelist.Resolve(typeName);
                    if (declared == null || declared != Target.Options.Whitelist.Resolve(nested.GetString()))
                        throw new TypeNotAllowedException(typeName);
                    wrapper = new Wrapper { Type = nested.GetString(), value = payload.GetProperty("value").Clone() };
                } else wrapper = new Wrapper { Type = typeName, value = payload.Clone() };
                // Check the route and payload type before any payload constructors/setters can execute.
                wrapper.GetValueType(Target);
                InboundMessageSecurity.RequireRedirect(Target, typeName, root.TryGetProperty("Recipient", out var recipient) ? recipient.GetString() : null);
                object value = wrapper.Unwrap(Target);
                return new PeerRedirect {
                    Type = typeName, Value = value,
                    Sender = root.TryGetProperty("Sender", out var from) ? from.GetString() : null,
                    Recipient = root.TryGetProperty("Recipient", out var to) ? to.GetString() : null
                };
            }
        }

        public object GetPropertyValue(PropertyValueArgs e) {
            if (e == null || e.Value is null)
                return null;
            JsonElement jsonElement = (JsonElement)e.Value;
            if (jsonElement.TryGetProperty(e.Name, out jsonElement)) {
                var v = GetValue(jsonElement, e.Reference.Info.PropertyType, true);
                return v;
            }

            return default;
        }

        private JsonElement ParseBytes(byte[] bytes) {
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement;
        }

        public object GetValue(object source, Type T, bool Parse) {
            JsonElement jsonElement = Parse && source.GetType() == typeof(byte[]) ? ParseBytes((byte[])source) : (JsonElement)source;
            switch (jsonElement.ValueKind) {
                case JsonValueKind.Null: {
                        return null;
                    }
                case JsonValueKind.String: {
                        if (T == typeof(DateTime)) {
                            return jsonElement.GetDateTime();
                        } else if (T == typeof(Guid)) {
                            return jsonElement.GetGuid();
                        } else if (T == typeof(byte[])) {
                            return jsonElement.GetBytesFromBase64();
                        } else {
                            return jsonElement.GetString();
                        }
                    }
                case JsonValueKind.Number: {
                        if (T.IsEnum) {
                            // Idk why i was jumping through hoops here, maybe it was for a reason.
                            // string PropertyTypeName = e.Reference.Info.PropertyType.FullName;
                            // var enumType = e.Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                            return Enum.ToObject(T, jsonElement.GetInt32());
                        } else {
                            return GetJsonNumericValue(jsonElement, T);
                        }
                    }
                case JsonValueKind.True:
                case JsonValueKind.False: {
                        return jsonElement.GetBoolean();
                    }
                case JsonValueKind.Object: {
                        string jsonTxt = jsonElement.GetRawText();
                        object obj = System.Text.Json.JsonSerializer.Deserialize(jsonTxt, typeof(Wrapper), JsonOptions);
                        Type objType = obj.GetType();
                        if (((Wrapper)obj).Type == null) {
#if !NETSTANDARD1_6_OR_GREATER
                            return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
                            //foreach (var converterType in ConverterElementTypes) {
                            //    if (converterType == T) {

                            //    }
                            //}
                            //return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
#else
                            return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
#endif
                        } else {
                            return obj;
                        }
                    }
                case JsonValueKind.Array: {
                        return System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), T, JsonOptions);
                    }
            }
            return default;
        }

        private static object GetJsonNumericValue(JsonElement JsonObject, Type Type) {
            if (Type == typeof(int)) {
                return JsonObject.GetInt32();
            } else if (Type == typeof(long)) {
                return JsonObject.GetInt64();
            } else if (Type == typeof(double)) {
                return JsonObject.GetDouble();
            } else if (Type == typeof(decimal)) {
                return JsonObject.GetDecimal();
            } else if (Type == typeof(byte)) {
                return JsonObject.GetByte();
            } else if (Type == typeof(sbyte)) {
                return JsonObject.GetSByte();
            } else if (Type == typeof(short)) {
                return JsonObject.GetInt16();
            } else if (Type == typeof(float)) {
                return JsonObject.GetSingle();
            } else if (Type == typeof(ushort)) {
                return JsonObject.GetUInt16();
            } else if (Type == typeof(uint)) {
                return JsonObject.GetUInt32();
            } else if (Type == typeof(ulong)) {
                return JsonObject.GetUInt64();
            }

            return default;
        }
    }

}