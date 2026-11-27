using System.Reflection;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Invocation;
using DigitalBrain.Runtime.Orleans.Access;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Grants;
using IntoChat.OS.Modules.Identity.Grants;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;
using Xunit;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class SiloAccessCallFilterFacts
{
    private static readonly Type OrleansTimerInvokerType =
        typeof(Orleans.Runtime.Silo).Assembly.GetType("Orleans.Runtime.IGrainTimerInvoker", throwOnError: true)!;

    private static readonly MethodInfo OrleansTimerInvokeMethod =
        OrleansTimerInvokerType.GetMethod("InvokeCallbackAsync", BindingFlags.Public | BindingFlags.Instance)!;

    [Fact]
    public async Task SelfTargetedOrleansGrainTimerTick_IsAdmitted_AndEntersSiloOrigin()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var grainId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var observedOrigin = false;
        var context = new CallContext(
            targetId: grainId,
            sourceId: grainId,
            grain: new DummyGrain(),
            interfaceMethod: OrleansTimerInvokeMethod,
            onInvoke: () => observedOrigin = SiloOrigin.IsCurrent);

        RequestContext.Clear();
        Assert.False(SiloOrigin.IsCurrent);

        await filter.Invoke(context);

        Assert.True(context.Invoked);
        Assert.True(observedOrigin, "SiloOrigin.IsCurrent must be true inside an admitted GrainTimer callback.");
        Assert.False(SiloOrigin.IsCurrent, "SiloOrigin must be restored after the GrainTimer callback completes.");
    }

    [Fact]
    public async Task GrainTimerSpoof_DifferentSourceId_IsRejected()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var targetId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var otherSource = GrainId.Create("client", "external-caller");
        var context = new CallContext(targetId, otherSource, new DummyGrain(), OrleansTimerInvokeMethod);

        RequestContext.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
        Assert.False(context.Invoked);
    }

    [Fact]
    public async Task GrainTimerSpoof_NullSourceId_IsRejected()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var targetId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var context = new CallContext(targetId, sourceId: null, new DummyGrain(), OrleansTimerInvokeMethod);

        RequestContext.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
        Assert.False(context.Invoked);
    }

    [Fact]
    public async Task GrainTimerSpoof_NullGrain_IsRejected()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var grainId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var context = new CallContext(grainId, sourceId: grainId, grain: null!, OrleansTimerInvokeMethod);

        RequestContext.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
        Assert.False(context.Invoked);
    }

    [Fact]
    public async Task GrainTimerSpoof_NonTimerMethodWithSelfSourceId_IsRejected()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var grainId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var nonTimerMethod = typeof(INeuronHost).GetMethod(nameof(INeuronHost.InspectState))!;
        var context = new CallContext(grainId, sourceId: grainId, new DummyGrain(), nonTimerMethod);

        RequestContext.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
        Assert.False(context.Invoked);
    }

    [Fact]
    public async Task GrainTimerSpoof_WrongMethodName_IsRejected()
    {
        var filter = new SiloAccessCallFilter([new AllowAll()], new Ambient(null));
        var grainId = GrainId.Create("neuron.host", "owner/runtime.session/watch/test");
        var wrongMethod = typeof(IFakeTimerInvoker).GetMethod(nameof(IFakeTimerInvoker.OtherMethod))!;
        var context = new CallContext(grainId, sourceId: grainId, new DummyGrain(), wrongMethod);

        RequestContext.Clear();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
        Assert.False(context.Invoked);
    }

    private sealed class DummyGrain : INeuronHost
    {
        public Task<bool> IsInterleaved(RuntimeInvocation call) => Task.FromResult(false);
        public Task<JsonElement?> Invoke(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task<JsonElement?> InvokeInterleaved(RuntimeInvocation call, CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
        public Task Post(RuntimeInvocation call) => Task.CompletedTask;
        public Task PostInterleaved(RuntimeInvocation call) => Task.CompletedTask;
        public async IAsyncEnumerable<JsonElement> Stream(RuntimeInvocation call, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<JsonElement?> InspectState(CancellationToken cancellationToken = default) => Task.FromResult<JsonElement?>(null);
    }

    private interface IFakeTimerInvoker
    {
        Task OtherMethod();
    }

    private sealed class Ambient(Caller? current) : ICallerAmbient
    {
        public Caller? Current => current;
    }

    private sealed class AllowAll : ICallFilterStage
    {
        public ValueTask<CallDecision?> EvaluateAsync(Caller? caller, CallRequest request, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallDecision?>(CallDecision.Allow());
    }

    private sealed class CallContext(
        GrainId targetId,
        GrainId? sourceId,
        object grain,
        MethodInfo interfaceMethod,
        Action? onInvoke = null) : IIncomingGrainCallContext
    {
        public bool Invoked { get; private set; }
        public GrainId TargetId { get; } = targetId;
        public GrainId? SourceId { get; } = sourceId;
        public string InterfaceName => InterfaceMethod.DeclaringType?.FullName ?? string.Empty;
        public string MethodName => InterfaceMethod.Name;
        public object Grain { get; } = grain;
        public IInvokable Request => null!;
        public object? Result { get; set; }
        public Response? Response { get; set; }
        public MethodInfo InterfaceMethod { get; } = interfaceMethod;
        public MethodInfo ImplementationMethod => InterfaceMethod;
        public IGrainContext TargetContext => null!;
        public GrainInterfaceType InterfaceType => default;

        public Task Invoke()
        {
            Invoked = true;
            onInvoke?.Invoke();
            return Task.CompletedTask;
        }
    }
}
