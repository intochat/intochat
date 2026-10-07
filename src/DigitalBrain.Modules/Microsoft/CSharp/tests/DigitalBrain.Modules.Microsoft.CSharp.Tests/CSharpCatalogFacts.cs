using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpCatalogFacts
{
    [Theory]
    [InlineData("../other")]
    [InlineData("other/program")]
    [InlineData("C:\\private")]
    public void ModelCannotSelectAnExternalScopeOrPath(string id)
        => Assert.Throws<ArgumentException>(() => CSharpFileKeys.For("workspace-trusted", id));

    [Fact]
    public void WorkspacesReceiveDifferentNeuronKeys()
        => Assert.NotEqual(CSharpFileKeys.For("workspace-a", "invoice"), CSharpFileKeys.For("workspace-b", "invoice"));

    [Fact]
    public async Task CatalogKeepsTheNameWhenOnlyPurposeIsOmittedAndSurvivesDeactivation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().StartAsync(ct);
        var store = new CSharpCatalogStore(brain.SiloServices.GetRequiredService<IDigitalBrain>());

        await store.Describe("workspace-one", "timer", "Timer status", "Show ticks", ct);
        await store.Describe("workspace-one", "timer", null, null, ct);
        await brain.DeactivateAsync(brain.Get<ICSharpCatalog>(ICSharpCatalog.Key("workspace-one")), ct);

        var described = Assert.Single(await store.List("workspace-one", ct));
        Assert.Equal(("Timer status", "Show ticks"), (described.Name, described.Purpose));
        await store.Remove("workspace-one", "timer", ct);
        Assert.Empty(await store.List("workspace-one", ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.Read("workspace-one", "timer", ct));
    }

    [Fact]
    public async Task WorkspacesKeepSeparateCatalogs()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().StartAsync(ct);
        var store = new CSharpCatalogStore(brain.SiloServices.GetRequiredService<IDigitalBrain>());

        await store.Describe("workspace-one", "timer", "Timer", "", ct);

        Assert.Single(await store.List("workspace-one", ct));
        Assert.Empty(await store.List("workspace-two", ct));
    }

    [Fact]
    public async Task CatalogRejectsInvalidIdsAndOversizedDescriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().StartAsync(ct);
        var store = new CSharpCatalogStore(brain.SiloServices.GetRequiredService<IDigitalBrain>());

        await Assert.ThrowsAsync<ArgumentException>(() => store.Describe("workspace-one", "../timer", "Name", "", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => store.Describe("workspace-one", "timer", "Name", new string('x', 4001), ct));
        Assert.Empty(await store.List("workspace-one", ct));
    }

    [Fact]
    public async Task AHostWithoutTheCSharpModuleRefusesToRunFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new CSharpToolService(null!, new CSharpCatalogStore(null!));

        Assert.False(service.CanRun);
        var tools = service.ForScope("scope");
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Write("timer", "Console.WriteLine(1);", null, null, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tools.Contracts([], ct));
    }

    [Fact]
    public async Task ARejectedDescriptionStoresNoSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = new RecordingBrain();
        var service = new CSharpToolService(brain, new CSharpCatalogStore(brain),
            new CSharpContractDiscovery(new ContractCatalog(new ModuleInventory([])), new ModuleInventory([]), new CSharpDirectives(Options.Create(new CSharpOptions()))));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ForScope("scope").Write("timer", "Console.WriteLine(1);", new string('n', 121), null, ct));

        Assert.Empty(brain.Calls);
    }

    private sealed class RecordingBrain : IDigitalBrain
    {
        public List<string> Calls { get; } = [];

        public IKernel Self => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron
        {
            var proxy = DispatchProxy.Create<T, CallRecorder>();
            ((CallRecorder)(object)proxy).Calls = Calls;
            return proxy;
        }

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
    }

    public class CallRecorder : DispatchProxy
    {
        internal List<string> Calls { get; set; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Calls.Add(targetMethod!.Name);
            return Task.CompletedTask;
        }
    }
}
