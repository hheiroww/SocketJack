using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Security;

namespace SocketJack.SecurityLoader.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SecurityLoaderFacadeTests {
    [TestMethod]
    public async Task RuntimeRequiresExplicitBootstrapAndUsesAnApplicationSnapshot() {
        var session = new SecurityLoaderSession("fixture-bearer", revision: 7);
        var options = new SecurityLoaderOptions { ApplicationId = "fixture-app", SessionProvider = _ => Task.FromResult(session) };
        Assert.ThrowsException<InvalidOperationException>(() => SocketJackSecurityLoader.Create(options));
        Assert.ThrowsException<ArgumentNullException>(() => SocketJackSecurityLoader.RegisterRuntimeProvider(null!));

        var provider = new FixtureProvider();
        SocketJackSecurityLoader.RegisterRuntimeProvider(provider);
        SocketJackSecurityLoader.RegisterRuntimeProvider(provider);
        Assert.ThrowsException<InvalidOperationException>(() => SocketJackSecurityLoader.RegisterRuntimeProvider(new FixtureProvider()));
        await using var runtime = SocketJackSecurityLoader.Create(options);
        options.ApplicationId = "later-mutation";
        Assert.AreEqual("fixture-app", provider.Options!.ApplicationId);
        Assert.AreSame(session, await provider.Options.SessionProvider(CancellationToken.None));

        var request = new SecurityLoaderRequest("fixture.normalize", new[] { new SecurityLoaderObjectReference("upload", "fixture-id", "3") });
        using var cancellation = new CancellationTokenSource();
        var job = await runtime.ExecuteAsync(request, cancellation.Token);
        Assert.AreEqual(SecurityLoaderJobState.Succeeded, job.State);
        Assert.AreSame(request, provider.Runtime.Request);
        Assert.AreEqual(cancellation.Token, provider.Runtime.Token);
        await runtime.InvalidateAsync();
        Assert.IsTrue(provider.Runtime.Invalidated);
        provider.ReturnNull = true;
        Assert.ThrowsException<InvalidOperationException>(() => SocketJackSecurityLoader.Create(new SecurityLoaderOptions {
            ApplicationId = "fixture-app", SessionProvider = _ => Task.FromResult(session)
        }));
    }

    [TestMethod]
    public void InvalidConfigurationDoesNotRequireOrInvokeARuntime() {
        Assert.ThrowsException<ArgumentNullException>(() => SocketJackSecurityLoader.Create(null!));
        foreach (var service in new[] { "http://socketjack.com", "https://user:password@socketjack.com", "https://socketjack.com/?token=x", "https://socketjack.com/#fragment" }) {
            Assert.ThrowsException<ArgumentException>(() => SocketJackSecurityLoader.Create(new SecurityLoaderOptions {
                ApplicationId = "fixture-app", ServiceUri = new Uri(service), SessionProvider = _ => Task.FromResult(new SecurityLoaderSession())
            }));
        }
        Assert.ThrowsException<ArgumentException>(() => SocketJackSecurityLoader.Create(new SecurityLoaderOptions { ApplicationId = "fixture-app" }));
    }

    [TestMethod]
    public void CredentialsAreAbsentFromDiagnosticsAndJson() {
        var session = new SecurityLoaderSession("fixture-bearer", "fixture-cookie", 9);
        var diagnostic = session.ToString();
        var serialized = JsonSerializer.Serialize(session);
        foreach (var secret in new[] { "fixture-bearer", "fixture-cookie" }) {
            Assert.IsFalse(diagnostic.Contains(secret, StringComparison.Ordinal));
            Assert.IsFalse(serialized.Contains(secret, StringComparison.Ordinal));
        }
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderSession("fixture\r\nheader"));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SecurityLoaderSession(revision: -1));
    }

    [TestMethod]
    public void RequestsUseBoundedLogicalReferencesAndImmutableInventory() {
        var inventory = new List<SecurityLoaderObjectReference> { new("upload", "fixture-id") };
        var request = new SecurityLoaderRequest("fixture.normalize", inventory);
        inventory.Clear();
        Assert.AreEqual(1, request.Objects.Count);
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderObjectReference("upload", "https://example.test/file"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderObjectReference("upload", "../file"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("System.Type::Method"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("fixture.normalize", Enumerable.Repeat(new SecurityLoaderObjectReference("upload", "id"), 17)));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("fixture.normalize", argumentsJson: "[]"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("fixture.normalize", argumentsJson: "{"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("fixture.normalize", argumentsJson: "{\"text\":\"" + new string('\u00e9', 40000) + "\"}"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderRequest("fixture.normalize", argumentsJson: new string('[', 34) + "0" + new string(']', 34)));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderJob("job", SecurityLoaderJobState.Succeeded, new string('x', SecurityLoaderJob.MaximumResultUtf8Bytes + 1)));
    }

    private sealed class FixtureProvider : ISecurityLoaderRuntimeProvider {
        internal SecurityLoaderOptions? Options;
        internal readonly FixtureRuntime Runtime = new();
        internal bool ReturnNull;
        public ISecurityLoaderRuntime Create(SecurityLoaderOptions options) { Options = options; return ReturnNull ? null! : Runtime; }
    }

    private sealed class FixtureRuntime : ISecurityLoaderRuntime {
        internal SecurityLoaderRequest? Request;
        internal CancellationToken Token;
        internal bool Invalidated;
        public Task<SecurityLoaderJob> ExecuteAsync(SecurityLoaderRequest request, CancellationToken cancellationToken = default) {
            Request = request; Token = cancellationToken;
            return Task.FromResult(new SecurityLoaderJob("fixture-job", SecurityLoaderJobState.Succeeded, "{\"ready\":true}"));
        }
        public Task<SecurityLoaderJob> GetJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult(new SecurityLoaderJob(jobId, SecurityLoaderJobState.Queued));
        public Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ILoadedSecurityModule<T>> LoadLocalModuleAsync<T>(string componentId, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task InvalidateAsync(CancellationToken cancellationToken = default) { Invalidated = true; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => default;
    }
}
