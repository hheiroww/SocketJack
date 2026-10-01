using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SocketJack.Serialization {
    internal static class DeserializationTypePolicy {
        internal static void Validate(Type type, ISocket socket) => Visit(type, socket, new HashSet<Type>());
        private static void Visit(Type type, ISocket socket, HashSet<Type> seen) {
            if (type == null || typeof(Type).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || socket.Options.Blacklist.Contains(type) || !socket.Options.Whitelist.Contains(type))
                throw new TypeNotAllowedException(type?.FullName ?? "null");
            InboundMessageSecurity.ValidateGraphType(socket, type, seen.Count == 0);
            if (typeof(ISocket).IsAssignableFrom(type) || typeof(NetworkConnection).IsAssignableFrom(type) ||
                typeof(System.Reflection.MemberInfo).IsAssignableFrom(type) || typeof(System.Reflection.Assembly).IsAssignableFrom(type) ||
                typeof(System.IO.Stream).IsAssignableFrom(type) || typeof(System.Diagnostics.Process).IsAssignableFrom(type) ||
                typeof(System.Security.Principal.IPrincipal).IsAssignableFrom(type) || typeof(System.Security.Principal.IIdentity).IsAssignableFrom(type) ||
                type == typeof(NetworkOptions) || type == typeof(NetworkAuthorizationOptions) || type == typeof(VerifiedAssemblyRegistry))
                throw new TypeNotAllowedException(type.FullName);
            if (!seen.Add(type)) return;
            if (seen.Count > 128) throw new TypeNotAllowedException("Object graph is too complex.");
            if (socket.Options.SafeMode) socket.Options.VerifiedAssemblies.Verify(type.Assembly);
            if (type.IsArray) { Visit(type.GetElementType(), socket, seen); return; }
            if (type.IsGenericType) foreach (Type argument in type.GetGenericArguments()) Visit(argument, socket, seen);
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(DateTime) || type == typeof(Guid) || type == typeof(TimeSpan)) return;
            // Wrappers/redirects carry inert metadata and have an explicit checked payload decoding path.
            if (type == typeof(Wrapper) || type == typeof(PeerRedirect)) return;
            foreach (var attribute in type.GetCustomAttributes<System.Text.Json.Serialization.JsonDerivedTypeAttribute>())
                Visit(attribute.DerivedType, socket, seen);
            if (typeof(IEnumerable).IsAssignableFrom(type)) return;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                if (field.IsDefined(typeof(System.Text.Json.Serialization.JsonIncludeAttribute), true)) Visit(field.FieldType, socket, seen);
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (property.GetIndexParameters().Length != 0 ||
                    property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>()?.Condition == System.Text.Json.Serialization.JsonIgnoreCondition.Always) continue;
                Visit(property.PropertyType, socket, seen);
            }
        }
    }
}
