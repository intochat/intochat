using System.Diagnostics;
using System.Reflection;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Runtime.Orleans.Hosting;
using Orleans;
using Orleans.Metadata;
using Orleans.Runtime;
using Orleans.Serialization.Invocation;

namespace DigitalBrain.Runtime.Orleans.Tests.Unit.Hosting;

public sealed class DigitalBrainCallFilterFacts
{
    [Fact]
    public async Task IncomingFilter_CrossBrainViolation_ThrowsUnauthorizedAccessException()
    {
        var filter = new DigitalBrainCallFilter();
        var caller = CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User);
        using var scope = RuntimeCallerContext.StampScope(caller);

        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron", "brain-beta/apps/files"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
    }

    [Fact]
    public async Task IncomingFilter_SameBrain_Succeeds()
    {
        var filter = new DigitalBrainCallFilter();
        var caller = CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User);
        using var scope = RuntimeCallerContext.StampScope(caller);

        var invoked = false;
        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron", "brain-alpha/apps/files"),
            onInvoke: () => { invoked = true; return Task.CompletedTask; });

        await filter.Invoke(context);
        Assert.True(invoked);
    }

    [Fact]
    public async Task IncomingFilter_PlatformCaller_AllowedCrossBrain()
    {
        var filter = new DigitalBrainCallFilter();
        var caller = CapabilityCallers.Platform("kernel", "kernel", "kernel");
        using var scope = RuntimeCallerContext.StampScope(caller);

        var invoked = false;
        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron", "brain-beta/apps/files"),
            onInvoke: () => { invoked = true; return Task.CompletedTask; });

        await filter.Invoke(context);
        Assert.True(invoked);
    }

    // A call that carries a RuntimeInvocation is judged by the host grain, which knows the operation's access: it lets a
    // Shared or Public operation be called from another brain and refuses every other one. The filter leaves exactly
    // those methods to it.
    [Theory]
    [InlineData(nameof(INeuronHost.IsInterleaved))]
    [InlineData(nameof(INeuronHost.Invoke))]
    [InlineData(nameof(INeuronHost.InvokeInterleaved))]
    [InlineData(nameof(INeuronHost.Post))]
    [InlineData(nameof(INeuronHost.PostInterleaved))]
    public async Task IncomingFilter_CrossBrainCallCarryingMethodOfAHost_IsLeftToTheHost(string method)
    {
        var filter = new DigitalBrainCallFilter();
        using var scope = RuntimeCallerContext.StampScope(CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User));

        foreach (var grainType in new[] { "neuron.host", "neuron.host.reentrant", "neuron.host.stateless" })
        {
            var invoked = false;
            var context = new MockIncomingGrainCallContext(
                targetGrainId: GrainId.Create(grainType, "brain-beta/apps/files"),
                onInvoke: () => { invoked = true; return Task.CompletedTask; },
                method: typeof(INeuronHost).GetMethod(method));

            await filter.Invoke(context);
            Assert.True(invoked, grainType);
        }
    }

    [Theory]
    [InlineData(nameof(INeuronHost.Stream))]
    [InlineData(nameof(INeuronHost.InspectState))]
    public async Task IncomingFilter_CrossBrainOtherMethodOfAHost_StillThrows(string method)
    {
        var filter = new DigitalBrainCallFilter();
        using var scope = RuntimeCallerContext.StampScope(CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User));

        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron.host", "brain-beta/apps/files"),
            method: typeof(INeuronHost).GetMethod(method) ?? throw new InvalidOperationException(method));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
    }

    // A method of the same name on any other grain interface (subscriptions, catch-up, reminders) is not a neuron call.
    [Fact]
    public async Task IncomingFilter_CrossBrainMethodOfAnotherInterfaceOnAHost_StillThrows()
    {
        var filter = new DigitalBrainCallFilter();
        using var scope = RuntimeCallerContext.StampScope(CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User));

        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron.host", "brain-beta/apps/files"),
            method: typeof(ILookAlike).GetMethod(nameof(ILookAlike.Invoke)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
    }

    private interface ILookAlike
    {
        Task Invoke();
    }

    [Fact]
    public async Task IncomingFilter_CrossBrainInvokeOnAGrainThatIsNotAHost_StillThrows()
    {
        var filter = new DigitalBrainCallFilter();
        using var scope = RuntimeCallerContext.StampScope(CapabilityCallers.Of("user1", "acc1", "brain-alpha", CallerKind.User));

        var context = new MockIncomingGrainCallContext(
            targetGrainId: GrainId.Create("neuron.synapse-host", "brain-beta/apps/files"),
            method: typeof(INeuronHost).GetMethod(nameof(INeuronHost.Invoke)));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => filter.Invoke(context));
    }
    [Fact]
    public async Task OutgoingFilter_PropagatesCapabilityCallerAndActivity()
    {
        var filter = new DigitalBrainCallFilter();
        var caller = CapabilityCallers.Of("user-1", "acc-1", "brain-1", CallerKind.App, appId: "calc");
        using var scope = RuntimeCallerContext.StampScope(caller);

        using var activity = new Activity("TestActivity").Start();

        var invoked = false;
        var context = new MockOutgoingGrainCallContext(
            sourceGrainId: GrainId.Create("neuron", "brain-1/edge/http"),
            targetGrainId: GrainId.Create("neuron", "brain-1/apps/calc"),
            onInvoke: () =>
            {
                invoked = true;
                Assert.Equal("user-1", RequestContext.Get("db.cap.principal"));
                Assert.Equal("acc-1", RequestContext.Get("db.cap.account"));
                Assert.Equal("brain-1", RequestContext.Get("db.cap.brain"));
                Assert.Equal("calc", RequestContext.Get("db.cap.app"));
                Assert.NotNull(RequestContext.Get("intochatos.activity-id"));
                return Task.CompletedTask;
            });

        await filter.Invoke(context);
        Assert.True(invoked);
    }

    private sealed class MockIncomingGrainCallContext(GrainId targetGrainId, Func<Task>? onInvoke = null, MethodInfo? method = null) : IIncomingGrainCallContext
    {
        public GrainId TargetId => targetGrainId;
        public GrainId? SourceId => null;
        public Task Invoke() => onInvoke is not null ? onInvoke() : Task.CompletedTask;

        IGrainContext IIncomingGrainCallContext.TargetContext => null!;
        IInvokable IGrainCallContext.Request => null!;
        Response? IGrainCallContext.Response { get; set; }
        object? IGrainCallContext.Result { get; set; }
        object IGrainCallContext.Grain => null!;
        MethodInfo IGrainCallContext.InterfaceMethod => method!;
        MethodInfo IIncomingGrainCallContext.ImplementationMethod => null!;
        GrainInterfaceType IGrainCallContext.InterfaceType => default;
        string IGrainCallContext.InterfaceName => string.Empty;
        string IGrainCallContext.MethodName => string.Empty;
    }

    private sealed class MockOutgoingGrainCallContext(GrainId sourceGrainId, GrainId targetGrainId, Func<Task>? onInvoke = null) : IOutgoingGrainCallContext
    {
        public GrainId? SourceId => sourceGrainId;
        public GrainId TargetId => targetGrainId;
        public Task Invoke() => onInvoke is not null ? onInvoke() : Task.CompletedTask;

        IGrainContext IOutgoingGrainCallContext.SourceContext => null!;
        IInvokable IGrainCallContext.Request => null!;
        Response? IGrainCallContext.Response { get; set; }
        object? IGrainCallContext.Result { get; set; }
        object IGrainCallContext.Grain => null!;
        MethodInfo IGrainCallContext.InterfaceMethod => null!;
        GrainInterfaceType IGrainCallContext.InterfaceType => default;
        string IGrainCallContext.InterfaceName => string.Empty;
        string IGrainCallContext.MethodName => string.Empty;
    }
}
