using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketJack.Security;

namespace SocketJack.SecurityLoader.Tests;

[TestClass]
public sealed class SecurityLoaderOperationTests {
    [TestMethod]
    public void InputsSnapshotCallerBuffersAndRoundTripAsData() {
        byte[][] buffers = { new byte[] { 1, 2 }, Array.Empty<byte>() };
        var input = new SecurityLoaderOperationInput("publisher.transform", "{\"mode\":\"fixture\"}", buffers);
        buffers[0][0] = 9; buffers[1] = new byte[] { 8 };
        var obtained = input.Inputs; obtained[0][0] = 7; obtained[1] = new byte[] { 6 };
        CollectionAssert.AreEqual(new byte[] { 1, 2 }, input.Inputs[0]);
        Assert.AreEqual(0, input.Inputs[1].Length);
        var restored = JsonSerializer.Deserialize<SecurityLoaderOperationInput>(JsonSerializer.Serialize(input))!;
        Assert.AreEqual("publisher.transform", restored.Operation);
        Assert.AreEqual(input.ArgumentsJson, restored.ArgumentsJson);
        CollectionAssert.AreEqual(input.Inputs[0], restored.Inputs[0]);
    }

    [TestMethod]
    public void ResultsSnapshotArtifactsAndRoundTripAsData() {
        byte[][] artifacts = { new byte[] { 3, 4 } };
        var result = new SecurityLoaderOperationResult("{\"ready\":true}", artifacts);
        artifacts[0][0] = 8;
        var obtained = result.Artifacts; obtained[0][0] = 7;
        CollectionAssert.AreEqual(new byte[] { 3, 4 }, result.Artifacts[0]);
        var restored = JsonSerializer.Deserialize<SecurityLoaderOperationResult>(JsonSerializer.Serialize(result))!;
        Assert.AreEqual(result.ResultJson, restored.ResultJson);
        CollectionAssert.AreEqual(result.Artifacts[0], restored.Artifacts[0]);
    }

    [TestMethod]
    public void BufferCountsAndAggregateBytesAreBoundedBeforeCopying() {
        var tooMany = Enumerable.Range(0, 17).Select(_ => Array.Empty<byte>()).ToArray();
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", inputs: tooMany));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationResult(artifacts: tooMany));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", inputs: new byte[][] { null! }));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationResult(artifacts: new byte[][] { null! }));
        var halfPlusOne = new byte[SecurityLoaderOperationInput.MaximumInputBytes / 2 + 1];
        var tooLarge = new[] { halfPlusOne, halfPlusOne };
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", inputs: tooLarge));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationResult(artifacts: tooLarge));
        Assert.AreEqual(0, new SecurityLoaderOperationInput("publisher.transform").Inputs.Length);
        Assert.AreEqual(0, new SecurityLoaderOperationResult().Artifacts.Length);
    }

    [TestMethod]
    public void OperationNamesAndStructuredDocumentsStayWithinTheContract() {
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("System.Type::Invoke"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", "[]"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", "{"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationInput("publisher.transform", "{\"text\":\"" + new string('\u00e9', 40000) + "\"}"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationResult("not JSON"));
        Assert.ThrowsException<ArgumentException>(() => new SecurityLoaderOperationResult(new string('x', SecurityLoaderJob.MaximumResultUtf8Bytes + 1)));
    }

    [TestMethod]
    public async Task ApprovedAdaptersUseTheTypedAsyncContract() {
        ISecurityLoaderOperation operation = new EchoOperation();
        var result = await operation.ExecuteAsync(new SecurityLoaderOperationInput("publisher.echo", inputs: new[] { new byte[] { 4, 5 } }));
        CollectionAssert.AreEqual(new byte[] { 4, 5 }, result.Artifacts[0]);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => operation.ExecuteAsync(new SecurityLoaderOperationInput("publisher.echo"), cancellation.Token));
    }

    [TestMethod]
    public void ArtifactCountsAreBoundedAndLegacyJobConstructionRemainsAvailable() {
        Assert.IsNotNull(typeof(SecurityLoaderJob).GetConstructor(new[] { typeof(string), typeof(SecurityLoaderJobState), typeof(string), typeof(string) }));
        Assert.AreEqual(0, new SecurityLoaderJob("fixture", SecurityLoaderJobState.Queued).ArtifactCount);
        var job = new SecurityLoaderJob("fixture", SecurityLoaderJobState.Succeeded, "{}", null!, 2);
        Assert.AreEqual(2, JsonSerializer.Deserialize<SecurityLoaderJob>(JsonSerializer.Serialize(job))!.ArtifactCount);
        Assert.AreEqual(0, JsonSerializer.Deserialize<SecurityLoaderJob>("{\"id\":\"fixture\",\"state\":0}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.ArtifactCount);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SecurityLoaderJob("fixture", SecurityLoaderJobState.Succeeded, null!, null!, -1));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => new SecurityLoaderJob("fixture", SecurityLoaderJobState.Succeeded, null!, null!, 17));
        Assert.IsFalse(typeof(ISecurityLoaderRuntime).GetInterfaces().Contains(typeof(ISecurityLoaderArtifacts)));
    }

    private sealed class EchoOperation : ISecurityLoaderOperation {
        public Task<SecurityLoaderOperationResult> ExecuteAsync(SecurityLoaderOperationInput input, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SecurityLoaderOperationResult("{\"ready\":true}", input.Inputs));
        }
    }
}
