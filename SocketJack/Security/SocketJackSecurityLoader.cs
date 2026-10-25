using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Security {
    /// <summary>Access to an application-installed security runtime. Runtime implementation and trust provisioning are supplied by the application publisher.</summary>
    public static class SocketJackSecurityLoader {
        private static ISecurityLoaderRuntimeProvider provider;

        /// <summary>Registers the verified bootstrap's runtime provider once for this process. Re-registering the same instance is harmless.</summary>
        public static void RegisterRuntimeProvider(ISecurityLoaderRuntimeProvider runtimeProvider) {
            if (runtimeProvider == null) throw new ArgumentNullException(nameof(runtimeProvider));
            var existing = Interlocked.CompareExchange(ref provider, runtimeProvider, null);
            if (existing != null && !ReferenceEquals(existing, runtimeProvider))
                throw new InvalidOperationException("A security runtime provider is already registered in this process.");
        }

        /// <summary>Creates an application-scoped runtime using the already registered provider. This facade never downloads or activates code.</summary>
        public static ISecurityLoaderRuntime Create(SecurityLoaderOptions options) {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var snapshot = options.ValidatedCopy();
            var current = Volatile.Read(ref provider);
            if (current == null)
                throw new InvalidOperationException("The application must initialize its verified security runtime bootstrap before using SocketJackSecurityLoader.");
            return current.Create(snapshot) ?? throw new InvalidOperationException("The security runtime provider returned no runtime.");
        }
    }

    /// <summary>The application bootstrap's factory. Implementations are distributed by the application publisher.</summary>
    public interface ISecurityLoaderRuntimeProvider {
        ISecurityLoaderRuntime Create(SecurityLoaderOptions options);
    }

    /// <summary>Application-scoped access to allowlisted remote operations and authenticated local managed components.</summary>
    public interface ISecurityLoaderRuntime : IAsyncDisposable {
        Task<SecurityLoaderJob> ExecuteAsync(SecurityLoaderRequest request, CancellationToken cancellationToken = default);
        Task<SecurityLoaderJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default);
        Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default);
        Task<ILoadedSecurityModule<T>> LoadLocalModuleAsync<T>(string componentId, CancellationToken cancellationToken = default) where T : class;
        Task InvalidateAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>A lease on a loaded component. Disposal releases the runtime's ownership; it does not promise immediate removal of all process memory.</summary>
    public interface ILoadedSecurityModule<out T> : IAsyncDisposable where T : class {
        T Instance { get; }
    }

    /// <summary>Application identity and a callback that reads the application's current account session.</summary>
    public sealed class SecurityLoaderOptions {
        public string ApplicationId { get; set; }
        public Uri ServiceUri { get; set; } = new Uri("https://socketjack.com");
        public Func<CancellationToken, Task<SecurityLoaderSession>> SessionProvider { get; set; }

        internal SecurityLoaderOptions ValidatedCopy() {
            var application = SecurityLoaderContract.Identifier(ApplicationId, nameof(ApplicationId));
            var service = ServiceUri;
            if (service == null || !service.IsAbsoluteUri || service.Scheme != Uri.UriSchemeHttps ||
                service.UserInfo.Length != 0 || service.Query.Length != 0 || service.Fragment.Length != 0)
                throw new ArgumentException("The security service requires an absolute HTTPS URI without credentials, a query or a fragment.", nameof(ServiceUri));
            var session = SessionProvider ?? throw new ArgumentException("Provide a callback for the application's current account session.", nameof(SessionProvider));
            return new SecurityLoaderOptions { ApplicationId = application, ServiceUri = service, SessionProvider = session };
        }
    }
}
