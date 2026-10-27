using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Integrations;

namespace IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration;

// A Windows PC as storage: the connection advertises the capability and browsing stays inside its allowed roots.
public sealed class StorageFacts(WindowsMachineHost shared)
{
    [Fact]
    public async Task AConnectedPcIsDiscoveredAsStorage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        using var caller = host.Owner.AsCaller();

        var connections = await host.Brain.Get<IConnectionRequests>(IConnectionRequests.Key(host.Owner.BrainId)).ListConnections();

        var pc = Assert.Single(connections, item => item.Neuron == host.Key);
        Assert.Equal(["storage@1"], pc.Provides);
    }

    // 05-07: the runtime broker resolves the connected PC as storage@1 through the kernel's connections.
    [Fact]
    public async Task TheCapabilityBrokerResolvesAConnectedPcAsStorage()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        using var caller = host.Owner.AsCaller();
        var broker = host.Brain.Services.GetRequiredService<DigitalBrain.Runtime.Orleans.Capabilities.CapabilityBroker>();

        var storage = Assert.Single(await broker.Storages(new DigitalBrain.BrainId(host.Owner.BrainId), ct), item => item.ConnectionKey == host.Key);

        Assert.Equal(DigitalBrain.Capabilities.Storage.ItemKind.Folder, Assert.Single(await storage.Capability.Roots(ct)).Kind);
    }

    [Fact]
    public async Task StorageListsReadsAndNamesBreadcrumbsInsideARoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        using var caller = host.Owner.AsCaller();
        Directory.CreateDirectory(Path.Combine(host.Root, "notes"));
        File.WriteAllText(Path.Combine(host.Root, "notes", "a.txt"), "hello");
        var storage = host.Brain.Get<ILegacyStorage>(host.Key);

        var root = Assert.Single(await storage.Roots(ct));
        var folder = Assert.Single(await storage.List(root.Location, ct));
        var file = Assert.Single(await storage.List(folder.Location, ct));

        Assert.True(folder.IsFolder);
        Assert.Equal(("a.txt", false), (file.Name, file.IsFolder));
        Assert.Equal("hello", await storage.Read(file.Location, ct));
        Assert.Equal(["root", "notes", "a.txt"], (await storage.Ancestors(file.Location, ct)).Select(entry => entry.Name));
    }

    [Theory]
    [InlineData(@"..\escape.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    public async Task StorageRefusesLocationsOutsideTheRoots(string location)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        using var caller = host.Owner.AsCaller();
        var storage = host.Brain.Get<ILegacyStorage>(host.Key);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Read(location, ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.List(@"C:\Windows", ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.Ancestors(@"C:\Windows", ct));
    }

    [Fact]
    public async Task ARevokedRootIsRefusedByTheNextStorageCall()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var ct = TestContext.Current.CancellationToken;
        var host = await WindowsHost.ForPersonAsync(shared, connect: true, allowRoot: true, ct);
        using var caller = host.Owner.AsCaller();
        var storage = host.Brain.Get<ILegacyStorage>(host.Key);
        Assert.Empty(await storage.List(host.Root, ct));

        await host.Brain.Get<IWindows>(host.Key).RevokeRoot(host.Root);

        Assert.Empty(await storage.Roots(ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => storage.List(host.Root, ct));
    }
}
