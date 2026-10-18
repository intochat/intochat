using System.Reflection;
using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Contracts;
using DigitalBrain.Framework.Modules;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Catalog;
using DigitalBrain.Microsoft.CSharp.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class CSharpFileListingFacts(CSharpHost host)
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

    [Trait("host", "own")]
    [Fact]
    public async Task AFileKeepsItsNameWhenOnlyPurposeIsOmittedAndTheKernelListsItUntilDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sessions = new FakeSessionHost();
        await using var brain = await RuntimeBrain.StartAsync(sessions, ct);
        var one = await brain.PersonAsync("one", ct);
        using var caller = one.AsCaller();
        var tools = Tools(brain, one.BrainId);

        await tools.Write("timer", "Console.WriteLine(1);", "Timer status", "Show ticks", ct);
        await tools.Write("timer", "Console.WriteLine(2);", null, null, ct);
        await brain.DeactivateAsync(brain.Get<ICSharpFile>(CSharpFileKeys.For(one.BrainId, "timer")), ct);

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
        var brain = host.Brain;
        var one = await host.PersonAsync("one", ct);
        var two = await host.PersonAsync("two", ct);
        using (one.AsCaller())
        {
            await Tools(brain, one.BrainId).Write("timer", "Console.WriteLine(1);", "Timer", "", ct);
            Assert.Single(await Tools(brain, one.BrainId).List(ct));
        }
        using (two.AsCaller()) { Assert.Empty(await Tools(brain, two.BrainId).List(ct)); }
    }

    [Fact]
    public async Task InvalidIdsOversizedDescriptionsAndAppProgramNamesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var brain = host.Brain;
        var one = await host.PersonAsync("one", ct);
        using var caller = one.AsCaller();
        var tools = Tools(brain, one.BrainId);

        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("../timer", "Console.WriteLine(1);", "Name", "", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("timer", "Console.WriteLine(1);", "Name", new string('x', 4001), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => tools.Write("app-0123456789abcdef0123456789abcdef", "Console.WriteLine(1);", "Name", "", ct));
        Assert.Empty(await tools.List(ct));
    }

    private static ScopedCSharpTools Tools(ITestBrain brain, string scope)
        => brain.Services.GetRequiredService<CSharpToolService>().ForScope(scope);

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
            new CSharpContractDiscovery(null!));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ForScope("scope").Write("timer", "Console.WriteLine(1);", new string('n', 121), null, ct));

        Assert.Empty(brain.Calls);
    }

    private sealed class RecordingBrain : IDigitalBrain
    {
        public List<string> Calls { get; } = [];

        public IBrain Self => throw new NotSupportedException();
        public IKernelView Kernel => throw new NotSupportedException();

        public T Neuron<T>(NeuronId relative) where T : INeuron => throw new NotSupportedException();

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
