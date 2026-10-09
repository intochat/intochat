using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Testing.Module;
using DigitalBrain.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpFileListingFacts
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
    public async Task AFileKeepsItsNameWhenOnlyPurposeIsOmittedAndTheKernelListsItUntilDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessions = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(sessions, ct);
        await As(brain, "one");
        var tools = Tools(brain, "one");

        await tools.Write("timer", "Console.WriteLine(1);", "Timer status", "Show ticks", ct);
        await tools.Write("timer", "Console.WriteLine(2);", null, null, ct);
        await brain.DeactivateAsync(brain.Get<ICSharpFile>(CSharpFileKeys.For("one", "timer")), ct);

        var listed = Assert.Single(await tools.List(ct));
        Assert.Equal(("timer", "Timer status", "Show ticks"), (listed.Description.Id, listed.Description.Name, listed.Description.Purpose));
        await tools.Delete("timer", ct);
        Assert.Empty(await tools.List(ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => tools.Read("timer", ct));
    }

    [Fact]
    public async Task EachBrainListsOnlyItsOwnFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().StartAsync(ct);
        await As(brain, "one");
        await Tools(brain, "one").Write("timer", "Console.WriteLine(1);", "Timer", "", ct);

        Assert.Single(await Tools(brain, "one").List(ct));
        await As(brain, "two");
        Assert.Empty(await Tools(brain, "two").List(ct));
    }

    [Fact]
    public async Task InvalidIdsOversizedDescriptionsAndAppProgramNamesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await ModuleTest.Create().WithModule<CSharpModule>().StartAsync(ct);
        await As(brain, "one");
        var tools = Tools(brain, "one");

        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("../timer", "Console.WriteLine(1);", "Name", "", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("timer", "Console.WriteLine(1);", "Name", new string('x', 4001), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("app-0123456789abcdef0123456789abcdef", "Console.WriteLine(1);", "Name", "", ct));
        Assert.Empty(await tools.List(ct));
    }

    private static ScopedCSharpTools Tools(ModuleBrain brain, string scope)
        => brain.SiloServices.GetRequiredService<CSharpToolService>().ForScope(scope);

    private static Task As(ModuleBrain brain, string id)
    {
        CallerContextStamper.Stamp(new()
        { PrincipalId = "alice", AccountId = "alice", BrainId = id, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp });
        return brain.AuthorizeCallerAsync();
    }

    [Fact]
    public async Task AHostWithoutTheCSharpModuleRefusesToRunFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new CSharpToolService(null!, null!);

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
        var service = new CSharpToolService(brain, null!,
            new CSharpContractDiscovery(new ContractCatalog(new ModuleInventory([]))));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ForScope("scope").Write("timer", "Console.WriteLine(1);", new string('n', 121), null, ct));

        Assert.Empty(brain.Calls);
    }

    private sealed class RecordingBrain : IDigitalBrain
    {
        public List<string> Calls { get; } = [];

        public IBrain Self => throw new NotSupportedException();
        public IKernelView Kernel => throw new NotSupportedException();

        public T Get<T>(NeuronId id) where T : INeuron
        {
            var proxy = DispatchProxy.Create<T, CallRecorder>();
            ((CallRecorder)(object)proxy).Calls = Calls;
            return proxy;
        }

        public IAsyncEnumerable<T> On<T>(INeuron source, CancellationToken cancellationToken = default) where T : Signal
            => throw new NotSupportedException();
        public IAsyncEnumerable<T> On<T>(INeuron source, Func<Task> onSubscribed, CancellationToken cancellationToken = default) where T : Signal
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
