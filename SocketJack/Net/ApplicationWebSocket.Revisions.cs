using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net {
    /// <summary>A route that calls an existing typed operation, or a host-isolated JavaScript worker.</summary>
    public sealed class ApplicationRouteDefinition {
        public string Name { get; set; }
        public string Operation { get; set; }
        public string Script { get; set; }
        public string InputProperty { get; set; }
        public string RequiredBooleanProperty { get; set; }
    }

    /// <summary>A published application revision and the identity of its prepared frontend.</summary>
    public sealed class ApplicationRevision {
        public string Id { get; set; }
        public string FrontendSha256 { get; set; }
        public ApplicationRouteDefinition[] Routes { get; set; } = Array.Empty<ApplicationRouteDefinition>();
    }

    /// <summary>Host contract for a separately configured, operating-system-isolated script worker.</summary>
    public interface IApplicationScriptWorker {
        bool Ready { get; }
        ValueTask ValidateAsync(string script, CancellationToken cancellation);
        ValueTask<JsonElement> RunAsync(string script, JsonElement input, CancellationToken cancellation);
    }

    public sealed partial class ApplicationWebSocket {
        private const int MaximumRoutes = 64;
        private readonly Dictionary<string, ApplicationRouteDefinition> publishedRoutes = new Dictionary<string, ApplicationRouteDefinition>(StringComparer.Ordinal);
        private IApplicationScriptWorker scriptWorker;
        private ApplicationRevision revision;
        private string RevisionPath => Path.Combine(root, "application-revisions.json");

        private sealed class RevisionStore {
            public ApplicationRevision Current { get; set; }
            public ApplicationRevision Previous { get; set; }
            public ApplicationRevision Draft { get; set; }
        }

        /// <summary>Sets the host-provisioned script worker. The host owns the process and its isolation.</summary>
        public void SetScriptWorker(IApplicationScriptWorker worker) {
            lock (gate) scriptWorker = worker;
        }

        /// <summary>Preflights and durably saves a private draft. Call only from an authorized management surface.</summary>
        public async Task<string> SaveDraftAsync(ApplicationRevision draft, CancellationToken cancellation = default) {
            if (draft == null || draft.FrontendSha256?.Length != 64 || !draft.FrontendSha256.All(Uri.IsHexDigit) ||
                draft.Routes == null || draft.Routes.Length > MaximumRoutes)
                throw new ArgumentException("Invalid application draft.", nameof(draft));
            var copy = JsonSerializer.Deserialize<ApplicationRevision>(JsonSerializer.Serialize(draft, Json), Json);
            copy.Id = Guid.NewGuid().ToString("N");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var route in copy.Routes) {
                cancellation.ThrowIfCancellationRequested();
                if (route == null || !IsName(route.Name) || !names.Add(route.Name) || !route.Name.StartsWith("app.", StringComparison.Ordinal) ||
                    route.InputProperty != null && !IsName(route.InputProperty) ||
                    route.RequiredBooleanProperty != null && !IsName(route.RequiredBooleanProperty))
                    throw new ArgumentException("Invalid application route.", nameof(draft));
                IApplicationScriptWorker worker;
                lock (gate) {
                    if (operations.ContainsKey(route.Name) ||
                        (route.Script == null ? route.Operation == null || !operations.ContainsKey(route.Operation) : route.Operation != null))
                        throw new ArgumentException("Route references an unavailable operation.", nameof(draft));
                    worker = scriptWorker;
                }
                if (route.Script != null) {
                    if (worker?.Ready != true || route.Script.Length > 32 * 1024)
                        throw new InvalidOperationException("The configured isolated script worker is unavailable.");
                    await worker.ValidateAsync(route.Script, cancellation).ConfigureAwait(false);
                }
            }
            lock (gate) {
                cancellation.ThrowIfCancellationRequested();
                var stored = ReadRevisions();
                stored.Draft = copy;
                Save(RevisionPath, stored);
                return copy.Id;
            }
        }

        /// <summary>Activates a reviewed draft when the published revision and prepared frontend still match.</summary>
        public void ActivateDraft(string expectedRevision, string draftId, string preparedFrontendSha256) {
            lock (gate) {
                var store = ReadRevisions();
                if (store.Current?.Id != expectedRevision || store.Draft == null || store.Draft.Id != draftId ||
                    !string.Equals(store.Draft.FrontendSha256, preparedFrontendSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The application draft or published revision changed.");
                if (store.Draft.Routes.Any(route => route.Script != null) && scriptWorker?.Ready != true)
                    throw new InvalidOperationException("The configured isolated script worker is unavailable.");
                store.Previous = store.Current;
                store.Current = store.Draft;
                store.Draft = null;
                Save(RevisionPath, store);
                LoadRevision(store.Current);
            }
        }

        /// <summary>Restores the previous application revision under an optimistic version check.</summary>
        public void Rollback(string expectedRevision) {
            lock (gate) {
                var store = ReadRevisions();
                if (store.Current?.Id != expectedRevision || store.Previous == null)
                    throw new InvalidOperationException("The rollback revision is unavailable.");
                var previous = store.Previous;
                if (previous.Routes.Any(route => route.Script != null) && scriptWorker?.Ready != true)
                    throw new InvalidOperationException("The configured isolated script worker is unavailable.");
                store.Previous = store.Current;
                store.Current = previous;
                Save(RevisionPath, store);
                LoadRevision(store.Current);
            }
        }

        private RevisionStore ReadRevisions() => File.Exists(RevisionPath)
            ? JsonSerializer.Deserialize<RevisionStore>(File.ReadAllBytes(RevisionPath), Json) : new RevisionStore();

        private void LoadRevision(ApplicationRevision current) {
            revision = current;
            publishedRoutes.Clear();
            foreach (var route in current?.Routes ?? Array.Empty<ApplicationRouteDefinition>())
                publishedRoutes.Add(route.Name, route);
        }

        private async ValueTask<object> InvokeAsync(ApplicationCaller caller, string name, JsonElement input, CancellationToken cancellation) {
            Operation operation;
            ApplicationRouteDefinition route;
            IApplicationScriptWorker worker;
            lock (gate) {
                publishedRoutes.TryGetValue(name, out route);
                operations.TryGetValue(route?.Operation ?? name, out operation);
                worker = scriptWorker;
            }
            if (route != null) {
                if (route.RequiredBooleanProperty != null && (input.ValueKind != JsonValueKind.Object ||
                    !input.TryGetProperty(route.RequiredBooleanProperty, out var condition) || condition.ValueKind != JsonValueKind.True))
                    throw new ApplicationError("conditionFailed", "The route condition was not met.");
                if (route.InputProperty != null && (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(route.InputProperty, out input)))
                    throw new ApplicationError("invalidInput", "The route input is missing.");
                if (route.Script != null) {
                    if (worker?.Ready != true) throw new ApplicationError("unavailable", "Script execution is unavailable.");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    return await worker.RunAsync(route.Script, input, deadline.Token).ConfigureAwait(false);
                }
            }
            if (operation == null) throw new ApplicationError("notFound", "Operation not found.");
            return await operation.Invoke(caller, input, cancellation).ConfigureAwait(false);
        }
    }
}
