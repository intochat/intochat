using System.Collections.Concurrent;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Kernel.Composition.Tests.Integration.SideBySide;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Kernel.Composition.Tests.Integration;

[Trait("host", "own")]
public sealed class HostDispatchAccessFacts
{
    [Fact(Timeout = 120_000)]
    public async Task OrdinaryDispatchStillEnforcesTheStampedBrainAndEnvelopeTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        var key = "owner/probe.counted/access";
        var host = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<INeuronHost>(GrainId.Create("neuron.host", key));
        var call = new RuntimeInvocation("forged/runtime", PlainCounted.Type, key, nameof(IPlainCounted.Add), []);

        using (brain.Owner.AsCaller()) { await host.Invoke(call, ct); }
        using (brain.Owner.AsCallerIn("foreign"))
        { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Invoke(call, ct)); }
        using (brain.Owner.AsCaller())
        { await Assert.ThrowsAsync<BindingException>(() => host.Invoke(call with { Key = "owner/probe.counted/other" }, ct)); }
        using (brain.Owner.AsCaller())
        { await Assert.ThrowsAsync<BindingException>(() => host.InvokeInterleaved(call, ct)); }

        Assert.Equal(1, await brain.Get<IPlainCounted>("owner/access").Count());
        Assert.Equal(0, await brain.Get<IPlainCounted>("owner/other").Count());
    }

    [Fact(Timeout = 120_000)]
    public async Task APolicyQueryDoesNotWriteStateOrRestoreAnInstanceDirectoryEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>().StartAsync(ct);
        var counted = brain.Get<IPlainCounted>("owner/query-only");
        await counted.Add();
        await brain.DeactivateAsync(counted, ct);
        var grains = brain.Services.GetRequiredService<IGrainFactory>();
        // Remove only the projection, retaining the saved neuron to expose any query-triggered registration.
        var contract = typeof(CompiledTypes).Assembly.GetType("IntoChat.OS.Framework.IInstanceDirectory", throwOnError: true)!;
        var get = typeof(IGrainFactory).GetMethods().Single(method => method.Name == nameof(IGrainFactory.GetGrain)
            && method.IsGenericMethodDefinition && method.GetParameters() is [{ ParameterType: var type }] && type == typeof(GrainId));
        var directory = get.MakeGenericMethod(contract).Invoke(grains, [GrainId.Create("kernel.instances", "owner/" + PlainCounted.Type)])!;
        await (Task)contract.GetMethod("Retire")!.Invoke(directory, ["owner/query-only"])!;
        Assert.Empty(await (Task<Instance[]>)contract.GetMethod("List")!.Invoke(directory, [])!);

        var key = "owner/probe.counted/query-only";
        var host = grains.GetGrain<INeuronHost>(GrainId.Create("neuron.host", key));
        var call = new RuntimeInvocation("forged/runtime", PlainCounted.Type, key, nameof(IPlainCounted.Add), []);
        using (brain.Owner.AsCaller()) { Assert.False(await host.IsInterleaved(call)); }

        Assert.Equal(1, await counted.Count());
        Assert.Empty(await (Task<Instance[]>)contract.GetMethod("List")!.Invoke(directory, [])!);
    }

    [Fact(Timeout = 120_000)]
    public async Task QueuedOrdinaryCallsKeepTheirCallerWhenAnotherPrincipalInterleaves()
    {
        var ct = TestContext.Current.CancellationToken;
        var key = "owner/probe.plain/queued-caller";
        var queued = new QueuedCalls(key);
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>()
            .ConfigureServices(services => services.AddSingleton<IIncomingGrainCallFilter>(queued)).StartAsync(ct);
        using var other = await brain.Owner.AddAccountMemberAsync(brain, "another-member");
        var firstNeuronCaller = "owner/test.edge/first";
        var first = NeuronProxy.Create<IPlainProbe>(DigitalBrain.NeuronId.Parse(key), KernelInvocationSink.For(brain.Services, firstNeuronCaller));
        var second = NeuronProxy.Create<IPlainProbe>(DigitalBrain.NeuronId.Parse(key), KernelInvocationSink.For(brain.Services, "owner/test.edge/second"));
        var host = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<INeuronHost>(GrainId.Create("neuron.host", key));
        var control = new RuntimeInvocation(firstNeuronCaller, NeuronProxy.ContractName(typeof(IPlainProbe)), key, nameof(IPlainProbe.Holding), []);
        Task? held = null;
        try
        {
            Task<CallerContext> snapshot;
            Task<string> echoed;
            CallerContext expected;
            using (brain.Owner.AsCaller())
            {
                expected = CallerContextStamper.Require();
                held = first.Hold();
                await host.InvokeInterleaved(control, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
                snapshot = first.CallerSnapshot();
                echoed = first.Echo("value");
            }
            await queued.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.False(snapshot.IsCompleted);
            Assert.False(echoed.IsCompleted);
            using (other.AsCaller())
            {
                var interleaved = await second.Release().WaitAsync(TimeSpan.FromSeconds(10), ct);
                Assert.Equal(CallerContextStamper.Require(), interleaved);
                Assert.NotEqual(expected.PrincipalId, interleaved.PrincipalId);
            }
            await held.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Assert.Equal(expected, await snapshot.WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.Equal("value|" + firstNeuronCaller, await echoed.WaitAsync(TimeSpan.FromSeconds(10), ct));
            Assert.Equal(4, queued.Callers.Count);
            Assert.All(queued.Callers, actual =>
            {
                Assert.Equal(expected, actual.Caller);
                Assert.Equal(firstNeuronCaller, actual.NeuronCaller);
                Assert.Equal(firstNeuronCaller, actual.Behavior);
            });
        }
        finally
        {
            using var caller = brain.Owner.AsCaller();
            await host.InvokeInterleaved(control with { Method = nameof(IPlainProbe.Release) }, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (held is not null) { await held.WaitAsync(TimeSpan.FromSeconds(10), ct); }
        }
    }

    [Theory(Timeout = 120_000)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AnActorKeepsItsPhysicalSourceOnThePolicyQueryAndActualDispatch(bool callerless, bool post)
    {
        var ct = TestContext.Current.CancellationToken;
        const string target = "owner/probe.plain/physical-target";
        const string source = "owner/probe.plain/physical-source";
        var calls = new QueuedCalls(target);
        await using var brain = await TestBrain.Create().WithNeuronModule<PlainProbeModule>()
            .ConfigureServices(services => services.AddSingleton<IIncomingGrainCallFilter>(calls)).StartAsync(ct);
        var relay = NeuronProxy.Create<IPlainProbe>(DigitalBrain.NeuronId.Parse(source), KernelInvocationSink.For(brain.Services, "owner/test.edge/entry"));
        using var caller = brain.Owner.AsCaller();
        await relay.Relay(target, callerless, post);
        await calls.Dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.Equal(2, calls.Callers.Count);
        Assert.All(calls.Callers, actual =>
        {
            Assert.Equal(GrainId.Create("neuron.host", source), actual.Source);
            Assert.Equal(CallerContextStamper.Require(), actual.Caller);
        });
    }

    private sealed class QueuedCalls(string key) : IIncomingGrainCallFilter
    {
        private int _arrived;
        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Dispatched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<(CallerContext Caller, string? NeuronCaller, string Behavior, GrainId? Source)> Callers { get; } = new();

        public async Task Invoke(IIncomingGrainCallContext context)
        {
            var observed = false;
            if (context.Request.GetArgumentCount() > 0 && context.Request.GetArgument(0) is RuntimeInvocation call
                && call.Key == key && call.Method is nameof(IPlainProbe.CallerSnapshot) or nameof(IPlainProbe.Echo))
            {
                observed = true;
                Callers.Enqueue((CallerContextStamper.Require(), Orleans.Runtime.RequestContext.Get(KernelInvocationSink.CallerKey) as string, call.Behavior, context.SourceId));
                if (context.InterfaceMethod.DeclaringType == typeof(INeuronHost)
                    && context.InterfaceMethod.Name == nameof(INeuronHost.IsInterleaved)
                    && Interlocked.Increment(ref _arrived) == 2) { Arrived.TrySetResult(); }
            }
            await context.Invoke();
            if (observed && context.InterfaceMethod.Name != nameof(INeuronHost.IsInterleaved)) { Dispatched.TrySetResult(); }
        }
    }
}
