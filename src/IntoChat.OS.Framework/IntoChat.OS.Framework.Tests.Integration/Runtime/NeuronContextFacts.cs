using System.Collections.Concurrent;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Sdk;
using DeactivationCause = DigitalBrain.DeactivationCause;
using IDeactivatingNeuron = DigitalBrain.Spi.IDeactivatingNeuron;
using BrainId = DigitalBrain.BrainId;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime;

// Clean-core PR 4: a Neuron that is still a grain has the runtime SPI context, and every neutral member reads the
// same thing as the Orleans member it replaces.
public sealed class NeuronContextFacts(FrameworkHost host)
{
    [Fact]
    public async Task TheContextAddressIsTheGrainIdAndIdReadsTheSameString()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.PersonAsync("alice", ct);
        var key = $"{alice.BrainId}/context/{Guid.NewGuid():N}";
        var probe = host.Brain.Get<IContextProbe>(key);

        var seen = await probe.Describe();

        Assert.Equal(probe.GetGrainId().ToString(), seen.StorageKey);
        Assert.Equal(seen.GrainId, seen.StorageKey);
        Assert.Equal(seen.StorageKey, seen.Id);
        Assert.Equal(seen.PrimaryKey, seen.Key);
        Assert.Equal(key, seen.Key);
        Assert.Equal(alice.BrainId, seen.Brain);
        Assert.Equal(seen.GrainId, seen.Self);
    }

    [Fact]
    public async Task DeactivateEndsTheActivationThroughOnDeactivating()
    {
        var key = $"context/deactivate-{Guid.NewGuid():N}";
        var probe = host.Brain.Get<IContextProbe>(key);
        var first = await probe.Activation();

        await probe.Deactivate();
        var cause = await ContextProbe.Deactivated(key).Task.WaitAsync(host.Brain.Execution.AssertionTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(DeactivationCause.Requested, cause);
        Assert.NotEqual(first, await probe.Activation());
    }

    [Fact]
    public async Task OnDeactivatingRunsEvenWhenAnOverrideSkipsBase()
    {
        var key = $"context/skipping-{Guid.NewGuid():N}";
        var probe = host.Brain.Get<ISkippingProbe>(key);

        await probe.Deactivate();

        await ContextProbe.Deactivated(key).Task.WaitAsync(host.Brain.Execution.AssertionTimeout, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SuppressRunsWithoutTheCallerAndRestoresIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.PersonAsync("alice", ct);
        var probe = host.Brain.Get<IContextProbe>($"{alice.BrainId}/context/{Guid.NewGuid():N}");

        using var caller = alice.AsCaller();
        var seen = await probe.CallerAroundSuppress();
        Assert.Equal(new[] { true, false, true }, seen);
    }

    [Fact]
    public async Task TheResolverComposesTheSameKeysAsTheGrainFactory()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await host.PersonAsync("alice", ct);
        var resolver = host.Brain.Services.GetRequiredService<INeuronResolver>();
        var key = $"{alice.BrainId}/context/resolved";

        Assert.Equal(host.Brain.Get<IContextProbe>(key).GetGrainId(), resolver.Resolve<IContextProbe>(new BrainId(alice.BrainId), "context/resolved").GetGrainId());
        Assert.Equal(host.Brain.Get<IContextProbe>(key).GetGrainId(), resolver.ResolveKey<IContextProbe>(key).GetGrainId());
        Assert.Equal(key, resolver.ResolveKey<IContextProbe>(key).GetPrimaryKeyString());
    }
}

[GenerateSerializer, Alias("tests.framework.context-seen")]
public sealed record ContextSeen(
    [property: Id(0)] string StorageKey, [property: Id(1)] string Key, [property: Id(2)] string Id, [property: Id(3)] string GrainId,
    [property: Id(4)] string PrimaryKey, [property: Id(5)] string? Brain, [property: Id(6)] string Self);

[Alias("tests.framework.context-probe")]
public interface IContextProbe : INeuron
{
    Task<ContextSeen> Describe();
    Task<Guid> Activation();
    Task Deactivate();
    Task<bool[]> CallerAroundSuppress();
}

public sealed class ContextProbe : Neuron, IContextProbe, IDeactivatingNeuron
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<DeactivationCause>> Ends = new(StringComparer.Ordinal);
    private readonly Guid _activation = Guid.NewGuid();

    public static TaskCompletionSource<DeactivationCause> Deactivated(string key) =>
        Ends.GetOrAdd(key, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task<ContextSeen> Describe() => Task.FromResult(new ContextSeen(
        Context.Address.StorageKey, Context.Key, Id.Name, this.GetGrainId().ToString(), this.GetPrimaryKeyString(),
        Context.Address.Brain?.Value, Context.Self<IContextProbe>().GetGrainId().ToString()));

    public Task<Guid> Activation() => Task.FromResult(_activation);

    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }

    public Task<bool[]> CallerAroundSuppress()
    {
        var before = CallerContextStamper.TryGet(out _);
        bool during;
        using (Context.Caller.Suppress()) { during = CallerContextStamper.TryGet(out _); }
        return Task.FromResult<bool[]>([before, during, CallerContextStamper.TryGet(out _)]);
    }

    public Task OnDeactivating(DeactivationCause cause, CancellationToken cancellationToken)
    {
        Deactivated(Context.Key).TrySetResult(cause);
        return Task.CompletedTask;
    }
}

[Alias("tests.framework.skipping-probe")]
public interface ISkippingProbe : INeuron
{
    Task Deactivate();
}

// Overrides the Orleans hook without calling base; the neutral hook still runs.
public sealed class SkippingProbe : Neuron, ISkippingProbe, IDeactivatingNeuron
{
    public Task Deactivate()
    {
        Context.Deactivate();
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnDeactivating(DeactivationCause cause, CancellationToken cancellationToken)
    {
        ContextProbe.Deactivated(Context.Key).TrySetResult(cause);
        return Task.CompletedTask;
    }
}
