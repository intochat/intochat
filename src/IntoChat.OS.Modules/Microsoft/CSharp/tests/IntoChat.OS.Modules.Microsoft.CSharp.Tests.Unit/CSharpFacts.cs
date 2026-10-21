using IntoChat.OS;
using IntoChat.OS.Framework;
using IntoChat.OS.Microsoft.CSharp.Authoring;
using IntoChat.OS.Microsoft.CSharp.Catalog;
using IntoChat.OS.Microsoft.CSharp.Files;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Unit;

public sealed class CSharpFacts
{
    private static TypeEntry Neuron(string provider, string name) => new(name, provider + "." + name, TypeKind.Neuron, provider, "does " + name, ["Task Run()"], []);

    private static TypeEntry Signal(string provider, string name) => new(name, provider + "." + name, TypeKind.Signal, provider, "", ["int Count"], []);

    private static CSharpContractDiscovery Discovery(params TypeEntry[] types) => new(new FakeKernel(types));

    [Fact]
    public async Task WithNoModulesSelectedTheCatalogListsInstalledModuleIdsInOrderAndNoContracts()
    {
        var snapshot = await Discovery(Neuron("time", "Timer"), Neuron("ai", "Chat"), Signal("time", "Tick")).Read([], TestContext.Current.CancellationToken);

        Assert.Equal(["ai", "time"], snapshot.Modules.Select(module => module.Id));
        Assert.Empty(snapshot.Contracts);
        Assert.Equal(CSharpContractDiscovery.Example, snapshot.Example);
        Assert.False(snapshot.Truncated);
    }

    [Fact]
    public async Task SelectingAModuleListsItsNeuronsThenItsSignalsByName()
    {
        var snapshot = await Discovery(Neuron("time", "Timer"), Neuron("time", "Alarm"), Signal("time", "Tick"), Neuron("ai", "Chat"))
            .Read(["TIME"], TestContext.Current.CancellationToken);

        Assert.Equal(3, snapshot.Contracts.Count);
        Assert.StartsWith("Alarm [neuron id: time.Alarm/<key>]", snapshot.Contracts[0], StringComparison.Ordinal);
        Assert.StartsWith("Timer [neuron id: time.Timer/<key>]", snapshot.Contracts[1], StringComparison.Ordinal);
        Assert.Equal("Tick { int Count }", snapshot.Contracts[2]);
    }

    [Fact]
    public async Task ConnectorsAreNotListedAsContracts()
    {
        var connector = new TypeEntry("Gmail", "gmail", TypeKind.Connector, "gmail", "", [], []);

        var snapshot = await Discovery(Neuron("time", "Timer"), connector).Read([], TestContext.Current.CancellationToken);

        Assert.Equal(["time"], snapshot.Modules.Select(module => module.Id));
    }

    [Fact]
    public async Task AnUnknownModuleIdIsRefusedNamingTheInstalledOnes()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Discovery(Neuron("time", "Timer")).Read(["nope"], TestContext.Current.CancellationToken));

        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
        Assert.Contains("time", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedCatalogDropsContractsFromTheEndAndSaysSo()
    {
        var neurons = Enumerable.Range(0, 400).Select(index => Neuron("big", "Neuron" + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var snapshot = await Discovery(neurons).Read(["big"], TestContext.Current.CancellationToken);

        Assert.True(snapshot.Truncated);
        Assert.InRange(snapshot.Contracts.Count, 1, 399);
        Assert.StartsWith("Neuron000", snapshot.Contracts[0], StringComparison.Ordinal);
        Assert.True(System.Text.Json.JsonSerializer.Serialize(snapshot).Length <= 24000);
    }

    [Fact]
    public async Task ANullModuleListIsRefused()
        => await Assert.ThrowsAsync<ArgumentNullException>(() => Discovery().Read(null!, TestContext.Current.CancellationToken));

    [Fact]
    public void AFileViewCarriesTheFilesShortIdNamePurposeAndCreationTime()
    {
        var created = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
        var file = new CSharpFileSnapshot("long-id", "source", new Dictionary<string, string>(), null, "ready", null, true, "Tea timer", "Brews tea", created);

        var view = CSharpFileView.Of("t1", file, "log line");

        Assert.Equal(new CSharpDescription("t1", "Tea timer", "Brews tea", created), view.Description);
        Assert.Same(file, view.File);
        Assert.Equal("log line", view.Logs);
    }

    [Fact]
    public void AFileWithNoCreationTimeIsDescribedWithTheDefault()
    {
        var file = new CSharpFileSnapshot("id", "", new Dictionary<string, string>(), null, "new", null, false, "n", "p", null);

        Assert.Equal(default, CSharpFileView.Of("x", file).Description.CreatedAt);
        Assert.Null(CSharpFileView.Of("x", file).Logs);
    }

    private sealed class FakeKernel(TypeEntry[] types) : IKernelView
    {
        public IAsyncEnumerable<Instance> Instances<T>(CancellationToken cancellationToken = default) where T : INeuron => throw new NotSupportedException();
        public IAsyncEnumerable<Instance> Instances(string? type, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TypeEntry[]> Types(CancellationToken cancellationToken = default) => Task.FromResult(types);
        public Task<string[]> Brains(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Instance[]> Running(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
