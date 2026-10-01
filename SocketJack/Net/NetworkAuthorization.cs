using SocketJack.Net.P2P;
using SocketJack.Net.WebSockets;
using SocketJack.Serialization;
using System;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;

namespace SocketJack.Net {
    /// <summary>Marks a command DTO as administrative. It can never be delivered through a peer redirect.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = true)]
    public sealed class AdministrativeMessageAttribute : Attribute { }

    /// <summary>Server-owned policy. Register anonymous login DTOs explicitly; registering a callback does not grant access.</summary>
    public sealed class NetworkAuthorizationOptions {
        /// <summary>Require authentication for application messages. Default true. False allows ordinary data only;
        /// administrative commands and peer operations still require authentication and explicit authorization.</summary>
        public bool RequireAuthentication { get; set; } = true;
        /// <summary>Exact DTO types allowed before login. Keep login DTOs inert and authenticate credentials in their handler.</summary>
        public TypeList AnonymousMessageTypes { get; private set; } = new TypeList(Array.Empty<Type>());
        /// <summary>Application command types requiring the Administrator role plus AuthorizeMessage approval.</summary>
        public TypeList AdministrativeMessageTypes { get; private set; } = new TypeList(Array.Empty<Type>());
        /// <summary>Additional per-message policy. Required for administrative commands; exceptions deny access.</summary>
        public Func<NetworkAuthorizationContext, bool> AuthorizeMessage { get; set; }
        /// <summary>Explicit recipient and payload policy for peer forwarding. Null denies all redirects, including broadcasts.</summary>
        public Func<NetworkAuthorizationContext, bool> AuthorizePeerRedirect { get; set; }
        /// <summary>Explicit policy for client-originated peer identity, metadata, and connection-control messages. Null denies them.</summary>
        public Func<NetworkAuthorizationContext, bool> AuthorizePeerControl { get; set; }
        internal void CopyTo(NetworkAuthorizationOptions copy) {
            copy.RequireAuthentication = RequireAuthentication;
            copy.AnonymousMessageTypes = AnonymousMessageTypes.Copy();
            copy.AdministrativeMessageTypes = AdministrativeMessageTypes.Copy();
            copy.AuthorizeMessage = AuthorizeMessage;
            copy.AuthorizePeerRedirect = AuthorizePeerRedirect;
            copy.AuthorizePeerControl = AuthorizePeerControl;
        }
        internal bool IsAdministrative(Type type) => AdministrativeMessageTypes.Contains(type) ||
            Attribute.IsDefined(type, typeof(AdministrativeMessageAttribute), true);
    }

    /// <summary>Trusted connection context, not identity or roles supplied in a message body.</summary>
    public sealed class NetworkAuthorizationContext {
        public NetworkConnection Connection { get; }
        public ClaimsPrincipal Principal => Connection.AuthenticatedPrincipal;
        public Type MessageType { get; }
        public string Recipient { get; }
        internal NetworkAuthorizationContext(NetworkConnection connection, Type type, string recipient = null) {
            Connection = connection; MessageType = type; Recipient = recipient;
        }
    }

    public partial class NetworkConnection {
        private sealed class AuthenticationState {
            internal ClaimsPrincipal Principal;
            internal DateTimeOffset Expires;
        }
        private AuthenticationState authentication;
        /// <summary>A defensive copy of the locally verified identity. Wire metadata never sets this property.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public ClaimsPrincipal AuthenticatedPrincipal {
            get {
                var state = Volatile.Read(ref authentication);
                return state != null && state.Expires > DateTimeOffset.UtcNow && !Closed && !Closing
                    ? new ClaimsPrincipal(state.Principal.Identities.Select(i => new ClaimsIdentity(i))) : null;
            }
        }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsAuthenticated => AuthenticatedPrincipal?.Identity?.IsAuthenticated == true;
        internal bool HasAuthenticatedEncryption => SslStream?.IsAuthenticated == true && SslStream.IsEncrypted;

        /// <summary>Call only after the server has verified credentials or a client certificate. Requires encrypted TLS.
        /// Never copy roles or an authentication flag from a DTO, peer metadata, or DLL claims. UDP has no authenticated channel.</summary>
        public void SetAuthenticatedPrincipal(ClaimsPrincipal principal, DateTimeOffset expiresUtc) {
            if (principal?.Identity?.IsAuthenticated != true || expiresUtc <= DateTimeOffset.UtcNow ||
                Closed || Closing || !HasAuthenticatedEncryption)
                throw new SecurityException("Authentication requires a verified identity, a future expiry, and an encrypted TLS connection.");
            Interlocked.Exchange(ref authentication, new AuthenticationState {
                Principal = new ClaimsPrincipal(principal.Identities.Select(i => new ClaimsIdentity(i))), Expires = expiresUtc
            });
        }
        /// <summary>Revoke local authentication on logout or credential invalidation.</summary>
        public void ClearAuthentication() => Interlocked.Exchange(ref authentication, null);
    }

    internal sealed class InboundMessageSecurity : IDisposable {
        private static readonly AsyncLocal<InboundMessageSecurity> current = new AsyncLocal<InboundMessageSecurity>();
        private readonly InboundMessageSecurity previous;
        private readonly ISocket socket;
        private readonly NetworkConnection connection;
        private bool redirect;
        private InboundMessageSecurity(ISocket socket, NetworkConnection connection) {
            this.socket = socket; this.connection = connection; previous = current.Value; current.Value = this;
        }
        internal static IDisposable Enter(ISocket socket, NetworkConnection connection) => new InboundMessageSecurity(socket, connection);
        public void Dispose() => current.Value = previous;
        internal static SecurityException Denied() => new SecurityException("Remote message authorization denied.");
        private static bool IsServer(ISocket socket) => socket is TcpServer || socket is UdpServer || socket is WebSocketServer;
        private static bool Control(Type type) => type == typeof(Identifier) || type == typeof(Identifier[]) ||
            type == typeof(PeerServer) || type == typeof(MetadataKeyValue) || type == typeof(PeerAction);
        private static bool Permit(Func<NetworkAuthorizationContext, bool> policy, NetworkAuthorizationContext context) {
            try { return policy?.Invoke(context) == true; } catch { return false; }
        }
        private static void Authenticated(NetworkConnection connection) {
            if (connection?.IsAuthenticated != true || !connection.HasAuthenticatedEncryption) throw Denied();
        }
        internal static void RequireType(ISocket socket, NetworkConnection connection, Type type) {
            if (type == null || type == typeof(Wrapper)) throw Denied();
            var policy = socket.Options.Authorization;
            if (!IsServer(socket)) return;
            if (connection == null) throw Denied();
            // These envelopes have fixed inert fields; their reassembled/forwarded payload is separately authorized.
            if (type == typeof(Segment) || type == typeof(Ping) || type == typeof(Pong)) return;
            if (type == typeof(PeerRedirect)) {
                Authenticated(connection);
                if (!socket.Options.UsePeerToPeer || policy.AuthorizePeerRedirect == null) throw Denied();
                return;
            }
            var context = new NetworkAuthorizationContext(connection, type);
            if (Control(type)) {
                Authenticated(connection);
                if (!socket.Options.UsePeerToPeer || !Permit(policy.AuthorizePeerControl, context)) throw Denied();
                return;
            }
            if (policy.IsAdministrative(type)) {
                Authenticated(connection);
                if (!connection.AuthenticatedPrincipal.IsInRole("Administrator") || !Permit(policy.AuthorizeMessage, context)) throw Denied();
            } else {
                if (policy.RequireAuthentication && !policy.AnonymousMessageTypes.Contains(type)) Authenticated(connection);
                if (policy.AuthorizeMessage != null && !Permit(policy.AuthorizeMessage, context)) throw Denied();
            }
        }
        internal static void BeforeMaterialization(ISocket socket, Type type) {
            var scope = current.Value;
            if (scope == null || !ReferenceEquals(scope.socket, socket)) return;
            RequireType(socket, scope.connection, type);
        }
        internal static void ValidateGraphType(ISocket socket, Type type, bool root) {
            var scope = current.Value;
            // PeerAction is an inert enum inside the authorized Identifier envelope. Treating
            // that field as another control message rejects even a server's initial identity.
            if (!root && (type == typeof(Wrapper) || type == typeof(PeerRedirect) || type == typeof(Segment) || (Control(type) && type != typeof(PeerAction)))) throw Denied();
            if (scope == null || !ReferenceEquals(scope.socket, socket)) return;
            if (scope.redirect && (socket.Options.Authorization.IsAdministrative(type) || Control(type) || type == typeof(Segment))) throw Denied();
            if (socket.Options.Authorization.IsAdministrative(type)) RequireType(socket, scope.connection, type);
        }
        internal static void RequireRedirect(ISocket socket, string typeName, string recipient) {
            var scope = current.Value;
            var type = socket.Options.Whitelist.Resolve(typeName);
            if (type == null || type == typeof(PeerRedirect) || type == typeof(Wrapper) || type == typeof(Segment) ||
                Control(type) || socket.Options.Authorization.IsAdministrative(type)) throw Denied();
            // Deserializing a redirect without connection context must never grant a route to remote execution.
            if (scope == null || !ReferenceEquals(scope.socket, socket)) throw Denied();
            if (scope.redirect) throw Denied();
            if (IsServer(socket)) {
                RequireType(socket, scope.connection, typeof(PeerRedirect));
                if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 256 ||
                    !Permit(socket.Options.Authorization.AuthorizePeerRedirect,
                        new NetworkAuthorizationContext(scope.connection, type, recipient))) throw Denied();
            }
            scope.redirect = true;
        }
        internal static void BeforeDispatch(ISocket socket, NetworkConnection connection, object message, Type type) {
            if (message == null || message.GetType() != type) throw Denied();
            RequireType(socket, connection, type);
            if (message is PeerRedirect peer) {
                // Repeat recipient authorization after deserialization and before callbacks/forwarding.
                using (Enter(socket, connection)) RequireRedirect(socket, peer.Type, peer.Recipient);
                if (socket.Options.Whitelist.Resolve(peer.Type) != peer.Value?.GetType()) throw Denied();
                if (IsServer(socket)) peer.Sender = connection.ID.ToString();
            }
        }
    }
}
