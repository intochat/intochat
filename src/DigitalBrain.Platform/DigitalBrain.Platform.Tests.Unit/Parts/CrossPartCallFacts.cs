using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using Microsoft.Extensions.DependencyInjection;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public interface ICrossCounter : INeuron { Task<int> Add(int n); Task<int> Value(); Task Touch(); }

public interface INoteRelay : INeuron { Task<int> Bump(string counter, int n); }

[GrainType("test.cross-counter")]
public sealed class CrossCounterNeuron([PersistentState("counter", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state)
    : Neuron<PartCounterState>(state), ICrossCounter
{
    public static readonly ConcurrentDictionary<string, int> Adds = new(StringComparer.Ordinal);

    public async Task<int> Add(int n)
    {
        Adds.AddOrUpdate(this.GetPrimaryKeyString(), 1, (_, count) => count + 1);
        await SaveQuietly(new PartCounterState { Value = Snapshot.Value + n });
        return Snapshot.Value;
    }

    public Task<int> Value() => Task.FromResult(Snapshot.Value);

    public Task Touch() => Task.CompletedTask;
}

[GrainType("test.note-relay")]
public sealed class NoteRelayNeuron : Neuron, INoteRelay
{
    public Task<int> Bump(string counter, int n) => GrainFactory.GetGrain<ICrossCounter>(counter).Add(n);
}

// Task 22 (T20-9…T20-12): a call for a part held by another machine runs at that holder over the machines' link,
// under one trace; a part nobody may serve here is 503.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class CrossPartCallFacts
{
    private static string Scope => BrainScope.Create(ModuleTest.DefaultAccount, ModuleTest.DefaultAccount).Id;

    private static string NewCounter() => $"{Scope}/apps/counter/c{Guid.NewGuid():N}";

    private static async Task<(TrustHost Trust, ModuleBrain One, ModuleBrain Two)> Machines(CancellationToken ct)
    {
        var trust = await TrustHost.StartAsync(ct);
        var one = await ModuleTest.Create().WithTrust(trust).WithPartLog().WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.CoreHolder)}", "true").StartAsync(ct);
        await one.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var two = await ModuleTest.Create().WithTrust(trust).WithPartLog().StartAsync(ct);
        await two.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        return (trust, one, two);
    }

    private static async Task Stop(TrustHost trust, params ModuleBrain[] machines)
    {
        foreach (var machine in machines) { await machine.DisposeAsync(); }
        await trust.DisposeAsync();
    }

    private static string Id(ModuleBrain machine) => machine.SiloServices.GetRequiredService<IMachineTransport>().LocalMachineId;

    private static ActivityListener Listen(ConcurrentBag<Activity> seen)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PartCallFilter.SourceName,
            Sample = (ref _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = seen.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    [Fact(Timeout = 180_000)]
    public async Task ANeuronOnOneMachineCallsANeuronOfAPartHeldByAnother()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await two.Grains.GetGrain<ICrossCounter>(counter).Touch();

            Assert.Equal(1, await one.Grains.GetGrain<INoteRelay>($"{Scope}/apps/notes/n1").Bump(counter, 1));
            // R83: a void-returning call forwards through InvokeMethodAsync<object> as well.
            await one.Grains.GetGrain<ICrossCounter>(counter).Touch();

            Assert.Equal(1, await two.Grains.GetGrain<ICrossCounter>(counter).Value());
            Assert.Equal(1, CrossCounterNeuron.Adds[counter]);
            Assert.DoesNotContain(PartId.Of(counter), one.SiloServices.GetRequiredService<PartLogs>().Opened);
            Assert.Contains(PartId.Of(counter), two.SiloServices.GetRequiredService<PartLogs>().Opened);
        }
        finally { await Stop(trust, two, one); }
    }

    // "B unleased → 503": the core is the only private part in this release (R82); a Cloud machine may not lease it.
    [Fact(Timeout = 180_000)]
    public async Task ACallToAPartNobodyMayServeHereIs503()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var trust = await TrustHost.StartAsync(ct);
        var cloud = await ModuleTest.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.Role)}", nameof(MachineRole.Cloud)).StartAsync(ct);
        try
        {
            await Assert.ThrowsAsync<ServiceUnavailableException>(() => cloud.Grains.GetGrain<IBrain>(Scope).Read());
            Assert.DoesNotContain(PartId.Core(Scope), cloud.SiloServices.GetRequiredService<PartLogs>().Opened);
        }
        finally { await Stop(trust, cloud); }
    }

    [Fact(Timeout = 180_000)]
    public async Task OneTraceIdSpansBothMachines()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var seen = new ConcurrentBag<Activity>();
        using var listener = Listen(seen);
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await two.Grains.GetGrain<ICrossCounter>(counter).Touch();

            await one.Grains.GetGrain<ICrossCounter>(counter).Add(2);

            var client = Assert.Single(seen, activity => activity.Kind == ActivityKind.Client
                && activity.GetTagItem(PartCallFilter.MachineTag) as string == Id(one)
                && activity.GetTagItem(PartCallFilter.GrainTag) as string == $"test.cross-counter/{counter}");
            Assert.Contains(seen, activity => activity.Kind == ActivityKind.Server
                && activity.GetTagItem(PartCallFilter.MachineTag) as string == Id(two) && activity.TraceId == client.TraceId);
        }
        finally { await Stop(trust, two, one); }
    }

    [Fact(Timeout = 180_000)]
    public async Task AForwardedCallFollowsTheHolderOnceWhenItMoved()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await two.Grains.GetGrain<ICrossCounter>(counter).Touch();
            // Resolved and cached on machine one: two holds the counter.
            Assert.Equal(0, await one.Grains.GetGrain<ICrossCounter>(counter).Value());

            await two.SiloServices.GetRequiredService<PartActivations>().Release(PartId.Of(counter));

            Assert.Equal(1, await one.Grains.GetGrain<ICrossCounter>(counter).Add(1));
            Assert.Equal(1, CrossCounterNeuron.Adds[counter]);
            Assert.Contains(PartId.Of(counter), one.SiloServices.GetRequiredService<PartLogs>().Opened);
        }
        finally { await Stop(trust, two, one); }
    }

    [Fact(Timeout = 180_000)]
    public async Task AForwardedCallFromAMachineNotBoundToTheCallersAccountIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await two.Grains.GetGrain<ICrossCounter>(counter).Touch();

            RequestContext.Set(CallerContextStamper.RequestContextKey, new CallerContext
            {
                PrincipalId = "eve", AccountId = "eve", BrainId = Scope, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
            });
            try
            {
                var refused = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => one.Grains.GetGrain<ICrossCounter>(counter).Add(1));
                Assert.Contains("forwarded", refused.Message, StringComparison.Ordinal);
            }
            finally { RequestContext.Remove(CallerContextStamper.RequestContextKey); }

            Assert.False(CrossCounterNeuron.Adds.ContainsKey(counter));
        }
        finally { await Stop(trust, two, one); }
    }

    // R73/R76 closed by routing (T20-11): machine two is not the core holder; its neuron's first write registers the
    // instance in the core's directory, which machine one serves.
    [Fact(Timeout = 180_000)]
    public async Task ANonHolderReadsTheCoresKernelGrainsFromTheHolder()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var seen = new ConcurrentBag<Activity>();
        using var listener = Listen(seen);
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();

            Assert.Equal(3, await two.Grains.GetGrain<ICrossCounter>(counter).Add(3));

            Assert.DoesNotContain(PartId.Core(Scope), two.SiloServices.GetRequiredService<PartLogs>().Opened);
            Assert.Contains(seen, activity => activity.Kind == ActivityKind.Server
                && activity.GetTagItem(PartCallFilter.MachineTag) as string == Id(one)
                && (activity.GetTagItem(PartCallFilter.GrainTag) as string ?? "").StartsWith("kernel.instances/", StringComparison.Ordinal));
        }
        finally { await Stop(trust, two, one); }
    }
}
