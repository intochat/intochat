using System.Collections.Concurrent;
using DigitalBrain.Components;
using DigitalBrain.Contracts;
using DigitalBrain.Modules;
using DigitalBrain.Neurons;
using DigitalBrain.Testing;
using DigitalBrain.Units;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Runtime.Orleans.Tests.Integration.Components;

[DefaultNeuronType("facts.bg.target")]
public interface IBindingGateTarget
{
    Task<string> Ping();
}

[Neuron("facts.bg.target")]
public sealed class BindingGateTargetNeuron : Neuron, IBindingGateTarget
{
    public Task<string> Ping() => Task.FromResult(Id.ToString());
}

[DefaultNeuronType("facts.bg.caller")]
public interface IBindingGateCaller
{
    Task<string> CallDeclaredSlot();
    Task<string> CallUndeclaredTarget(NeuronId target);
}

[Neuron("facts.bg.caller")]
public sealed partial class BindingGateCallerNeuron : Neuron, IBindingGateCaller
{
    [Slot]
    private partial IBindingGateTarget Declared { get; }

    public Task<string> CallDeclaredSlot() => Declared.Ping();

    public Task<string> CallUndeclaredTarget(NeuronId target)
        => Context.Get<IBindingGateTarget>(target).Ping();
}

[DefaultNeuronType("facts.bg.other")]
public interface IBindingGateOtherTarget
{
    Task<string> Ping();
}

[Neuron("facts.bg.other")]
public sealed class BindingGateOtherTargetNeuron : Neuron, IBindingGateOtherTarget
{
    public Task<string> Ping() => Task.FromResult(Id.ToString());
}

[DefaultNeuronType("facts.bg.collection-caller")]
public interface IBindingGateCollectionCaller
{
    Task<string> CallDynamicKey(string key);
    Task<string> CallUnboundOther(NeuronId target);
}

[Neuron("facts.bg.collection-caller")]
public sealed partial class BindingGateCollectionCallerNeuron : Neuron, IBindingGateCollectionCaller
{
    [Slot]
    private partial SlotCollection<IBindingGateTarget> Targets { get; }

    public Task<string> CallDynamicKey(string key) => Targets[key].Ping();

    public Task<string> CallUnboundOther(NeuronId target)
        => Context.Get<IBindingGateOtherTarget>(target).Ping();
}

[Module("facts-binding-gate")]
public sealed class BindingGateFactsModule : Module
{
    public static ModuleManifest Manifest { get; } = new([], [], [], [], []);
}

public sealed class BindingGateFacts
{
    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task AuditMode_LogsExactlyOneMissingBindingEventAndAllowsCall(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<MissingBindingEvent>();
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(BindingGateFactsModule)],
            fakes => fakes.Configure(services => services.AddSingleton(new BindingGateOptions
            {
                Mode = BindingEnforcementMode.Audit,
                OnMissingBinding = events.Enqueue,
            })),
            ct: ct);

        var ada = os.Person("ada");
        var declaredTarget = new NeuronId(ada.Brain, "facts.bg.target", "declared-1");
        var undeclaredTarget = new NeuronId(ada.Brain, "facts.bg.target", "undeclared-1");

        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("facts.bg.caller"),
            [new Binding("declared", new BindingTarget.Local(declaredTarget))],
            ct);

        var caller = ada.Get<IBindingGateCaller>(new NeuronId(ada.Brain, "facts.bg.caller", "c1"));
        var reached = await caller.CallUndeclaredTarget(undeclaredTarget);

        Assert.Equal(undeclaredTarget.ToString(), reached);
        var ev = Assert.Single(events);
        Assert.Equal(ada.Brain, ev.Brain);
        Assert.Equal(new ComponentId("facts.bg.caller"), ev.Component);
        Assert.Equal(undeclaredTarget, ev.Target);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task EnforceMode_RefusesUndeclaredCallWithBindingMissingCode(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<MissingBindingEvent>();
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(BindingGateFactsModule)],
            fakes => fakes.Configure(services => services.AddSingleton(new BindingGateOptions
            {
                Mode = BindingEnforcementMode.Enforce,
                OnMissingBinding = events.Enqueue,
            })),
            ct: ct);

        var ada = os.Person("ada");
        var declaredTarget = new NeuronId(ada.Brain, "facts.bg.target", "declared-2");
        var undeclaredTarget = new NeuronId(ada.Brain, "facts.bg.target", "undeclared-2");

        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("facts.bg.caller"),
            [new Binding("declared", new BindingTarget.Local(declaredTarget))],
            ct);

        var caller = ada.Get<IBindingGateCaller>(new NeuronId(ada.Brain, "facts.bg.caller", "c2"));
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.CallUndeclaredTarget(undeclaredTarget));

        Assert.Contains("binding.missing", error.Message, StringComparison.Ordinal);
        var ev = Assert.Single(events);
        Assert.Equal(new ComponentId("facts.bg.caller"), ev.Component);
        Assert.Equal(undeclaredTarget, ev.Target);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task EnforceMode_AdmitsDeclaredBoundSlotCall(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<MissingBindingEvent>();
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(BindingGateFactsModule)],
            fakes => fakes.Configure(services => services.AddSingleton(new BindingGateOptions
            {
                Mode = BindingEnforcementMode.Enforce,
                OnMissingBinding = events.Enqueue,
            })),
            ct: ct);

        var ada = os.Person("ada");
        var declaredTarget = new NeuronId(ada.Brain, "facts.bg.target", "declared-3");

        var installs = ada.Get<IInstalls>(new NeuronId(ada.Brain, "synapse.installs", "default"));
        await installs.BindComponent(
            new ComponentId("facts.bg.caller"),
            [new Binding("declared", new BindingTarget.Local(declaredTarget))],
            ct);

        var caller = ada.Get<IBindingGateCaller>(new NeuronId(ada.Brain, "facts.bg.caller", "c3"));
        var reached = await caller.CallDeclaredSlot();

        Assert.Equal(declaredTarget.ToString(), reached);
        Assert.Empty(events);
    }

    [Theory]
    [MemberData(nameof(NeuronRuntimeKinds.Both), MemberType = typeof(NeuronRuntimeKinds))]
    public async Task EnforceMode_AdmitsDynamicKeyCallThroughBoundSlotCollectionAndRefusesUnboundType(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var events = new ConcurrentQueue<MissingBindingEvent>();
        await using var os = await JourneyOS.Start(
            kind,
            [typeof(BindingGateFactsModule)],
            fakes => fakes.Configure(services => services.AddSingleton(new BindingGateOptions
            {
                Mode = BindingEnforcementMode.Enforce,
                OnMissingBinding = events.Enqueue,
            })),
            ct: ct);

        var ada = os.Person("ada");
        var caller = ada.Get<IBindingGateCollectionCaller>(new NeuronId(ada.Brain, "facts.bg.collection-caller", "cc1"));

        var first = await caller.CallDynamicKey("alpha");
        var second = await caller.CallDynamicKey("beta");
        Assert.Equal(new NeuronId(ada.Brain, "facts.bg.target", "alpha").ToString(), first);
        Assert.Equal(new NeuronId(ada.Brain, "facts.bg.target", "beta").ToString(), second);
        Assert.Empty(events);

        var unboundOther = new NeuronId(ada.Brain, "facts.bg.other", "gamma");
        var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => caller.CallUnboundOther(unboundOther));
        Assert.Contains("binding.missing", error.Message, StringComparison.Ordinal);
        var ev = Assert.Single(events);
        Assert.Equal(new ComponentId("facts.bg.collection-caller"), ev.Component);
        Assert.Equal(unboundOther, ev.Target);
    }
}
