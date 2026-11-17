using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Testing;
using IntoChat.OS.Authority;
using IntoChat.OS.Kernel.Composition.Capabilities;

namespace IntoChat.OS.Tests.Unit.Kernel;

// A bound connection's storage never runs a provider call with no caller, and never as an implicit kernel.
public sealed class ConnectionStorageCallerFacts
{
    private const string Key = "alice/drive/c1";

    [Fact]
    public async Task ACallWithNoCallerIsRefused()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new LocalCloudStorage(), Key, edge: os.Edge);

        Assert.Null(os.Callers.Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Roots(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ABoundConnectionWithoutAnEdgeFails()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new LocalCloudStorage(), Key);

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice"))))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => storage.Roots(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task APersonCallsTheProviderAsThemselves()
    {
        await using var os = TestOS.Local();
        var storage = new ConnectionStorage(new LocalCloudStorage(), Key, edge: os.Edge);

        using (os.Edge.Enter(os.Edge.Person("alice", "alice", new BrainId("alice"))))
        {
            Assert.NotNull(await storage.Roots(TestContext.Current.CancellationToken));
        }
    }
}
