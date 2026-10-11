using System.Collections.Concurrent;
using System.Diagnostics;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Links;
using DigitalBrain.Framework.Parts;
using DigitalBrain.Framework.Platform.Identity;
using DigitalBrain.Platform.Identity.Configuration;
using DigitalBrain.Platform.Parts;
using DigitalBrain.Platform.Trust;
using global::Microsoft.Extensions.DependencyInjection;
using global::Microsoft.Extensions.Hosting;
using Orleans.Serialization;
using Orleans.Serialization.Invocation;
using static DigitalBrain.Platform.Tests.Unit.Links.LinkTestbed;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public interface ICrossCounter : INeuron { Task<int> Add(int n); Task<int> Value(); Task Touch(); }

public interface INoteRelay : INeuron { Task<int> Bump(string counter, int n); Task Start(string echo); Task Record(); }

public interface IEcho : INeuron { [Orleans.Concurrency.OneWay] Task Poke(string back); Task Touch(); }

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
public sealed class NoteRelayNeuron([PersistentState("notes", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state)
    : Neuron<PartCounterState>(state), INoteRelay
{
    public static readonly ConcurrentDictionary<string, TaskCompletionSource> Recorded = new(StringComparer.Ordinal);

    public Task<int> Bump(string counter, int n) => GrainFactory.GetGrain<ICrossCounter>(counter).Add(n);

    // Non-reentrant: awaiting the one-way Poke until the echo's Record came back here would deadlock (R103).
    public Task Start(string echo) => GrainFactory.GetGrain<IEcho>(echo).Poke(this.GetPrimaryKeyString());

    public Task Record()
    {
        Recorded.GetOrAdd(this.GetPrimaryKeyString(), _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return Task.CompletedTask;
    }
}

[GrainType("test.echo")]
public sealed class EchoNeuron([PersistentState("echo", DigitalBrainNames.ClusterGrainStorage)] IPersistentState<PartCounterState> state)
    : Neuron<PartCounterState>(state), IEcho
{
    public Task Poke(string back) => GrainFactory.GetGrain<INoteRelay>(back).Record();

    public Task Touch() => Task.CompletedTask;
}

// Which machine ran an activation handler of a counter: a stand-in runs none (R101).
internal sealed class ActivationRecord(DigitalBrain.Platform.Sequencer.MachineIdentity machine) : IActivationHandler<CrossCounterNeuron>
{
    public static readonly ConcurrentBag<(string Machine, string Key)> Seen = [];

    public Task Activated(CrossCounterNeuron neuron, CancellationToken cancellationToken)
    {
        Seen.Add((machine.MachineId, neuron.GetPrimaryKeyString()));
        return Task.CompletedTask;
    }
}

// What a machine's incoming calls carried: the request bytes, the RequestContext keys and the caller, per target and method.
internal sealed class RequestCapture(Serializer serializer) : IIncomingGrainCallFilter
{
    public static readonly ConcurrentDictionary<string, byte[]> Requests = new(StringComparer.Ordinal);
    public static readonly ConcurrentDictionary<string, (string[] Keys, CallerContext? Caller)> Contexts = new(StringComparer.Ordinal);

    public Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is not IGrainBase) { return context.Invoke(); }
        var key = $"{context.TargetId}#{context.MethodName}";
        Requests[key] = serializer.SerializeToArray(context.Request);
        CallerContextStamper.TryGet(out var caller);
        Contexts[key] = ([.. RequestContext.Keys], caller);
        return context.Invoke();
    }
}

// Task 22 (T20-9…T20-12, R100–R104): a call for a part held by another machine runs at that holder over the machines'
// link, under one trace; a part nobody may serve here is 503.
[System.Runtime.Versioning.SupportedOSPlatform("windows"), System.Runtime.Versioning.SupportedOSPlatform("linux"), System.Runtime.Versioning.SupportedOSPlatform("macos")]
public sealed class CrossPartCallFacts
{
    private static string Scope => BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;

    private static string NewCounter() => $"{Scope}/apps/counter/c{Guid.NewGuid():N}";

    private static TestBrain Captured(TestBrain brain)
        // First of the incoming filters: it sees the RequestContext as the call arrived, before NeuronCallFilter.
        => brain.ConfigureSilo(silo =>
        {
            silo.Services.Insert(0, ServiceDescriptor.Singleton<IIncomingGrainCallFilter>(provider =>
                new RequestCapture(provider.GetRequiredService<Serializer>())));
            silo.Services.AddSingleton<IActivationHandler<CrossCounterNeuron>, ActivationRecord>();
        });

    private static async Task<(TrustHost Trust, ITestBrain One, ITestBrain Two)> Machines(CancellationToken ct)
    {
        var trust = await TrustHost.StartAsync(ct);
        var one = await Captured(TestBrain.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration($"{PartsOptions.Key}:{nameof(PartsOptions.CoreHolder)}", "true")).StartAsync(ct);
        await one.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        var two = await Captured(TestBrain.Create().WithTrust(trust).WithPartLog()).StartAsync(ct);
        await two.SiloServices.GetRequiredService<OwnerBirth>().Born.WaitAsync(ct);
        return (trust, one, two);
    }

    private static async Task Stop(TrustHost trust, params ITestBrain[] machines)
    {
        foreach (var machine in machines) { await machine.DisposeAsync(); }
        await trust.DisposeAsync();
    }

    private static string Id(ITestBrain machine) => machine.SiloServices.GetRequiredService<IMachineTransport>().LocalMachineId;

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

    // A raw grain-call over from's link to `to`, bypassing from's filter.
    private static async Task<ForwardedOutcome> Send(ITestBrain from, ITestBrain to, ForwardedCall call)
    {
        var serializer = from.SiloServices.GetRequiredService<Serializer>();
        var link = await from.SiloServices.GetRequiredService<IMachineTransport>().Connect(Id(to), CancellationToken.None);
        var reply = await link.Send(new LinkRequest(PartCallFilter.Operation, serializer.SerializeToArray(call), null, null, Guid.NewGuid()), CancellationToken.None);
        return serializer.Deserialize<ForwardedOutcome>(reply.Payload!)!;
    }

    private static IInvokable Captured(ITestBrain on, string key)
        => on.SiloServices.GetRequiredService<Serializer>().Deserialize<IInvokable>(RequestCapture.Requests[key])!;

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
        var cloud = await TestBrain.Create().WithTrust(trust).WithPartLog()
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

    // R100: an unstamped forwarded call is kernel work, admitted only from the brain owner's machines; a member's
    // machine that sends one is refused before anything executes.
    [Fact(Timeout = 180_000)]
    public async Task AnUnstampedForwardedCallIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        var bob = await MemberMachine(trust, one, ct);
        try
        {
            var counter = NewCounter();
            await one.Grains.GetGrain<ICrossCounter>(counter).Add(1);
            var target = GrainId.Create("test.cross-counter", counter);

            var outcome = await Send(bob, one, new ForwardedCall(target, Captured(one, $"{target}#Add"), null, null));

            var refused = Assert.IsType<UnauthorizedAccessException>(outcome.Error);
            Assert.Contains("unstamped", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, CrossCounterNeuron.Adds[counter]);
        }
        finally { await Stop(trust, bob, two, one); }
    }

    // A Secured host bound to account bob, a member of the owner's brain: a member's machine.
    private static async Task<ITestBrain> MemberMachine(TrustHost trust, ITestBrain owner, CancellationToken ct)
    {
        var bob = await TestBrain.Create().WithTrust(trust).WithPartLog()
            .WithConfiguration(AuthOptions.PostureKey, nameof(IdentityPosture.Secured))
            .StartAsync(ct);
        // Without a bootstrap credential the Secured host registers nothing at start; it serves bob's account only.
        await bob.SiloServices.GetRequiredService<MachineRegistrar>().Bind("bob");
        await bob.SiloServices.GetRequiredService<IIdentityClient>().EnsureAccount(new Account
        { AccountId = "bob", OwnerPrincipalId = "bob", Name = "bob", CreatedAt = DateTimeOffset.UtcNow });
        await owner.SiloServices.GetRequiredService<IIdentityClient>().EnsureMember(new Member
        { PrincipalId = "bob", AccountId = AccountSession.DefaultLogin, BrainId = Scope, Role = MemberRole.Member, DisplayName = "Bob" });
        return bob;
    }

    // R100: only the caller stamp and the Trace cross; the holder rebuilds the RequestContext and re-stamps the caller as
    // forwarded by the calling machine, so a forged trusted-module flag (or any other entry) never arrives.
    [Fact(Timeout = 180_000)]
    public async Task AForgedTrustedFlagIsIgnoredAtTheHolder()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await two.Grains.GetGrain<ICrossCounter>(counter).Touch();
            var owner = AccountSession.DefaultLogin;
            RequestContext.Set(CallerContextStamper.RequestContextKey, new CallerContext
            {
                PrincipalId = owner, AccountId = owner, BrainId = Scope, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp,
            });
            RequestContext.Set("digitalbrain.trusted-module-call", true);
            RequestContext.Set("test.forged", "forged");
            try { await one.Grains.GetGrain<ICrossCounter>(counter).Value(); }
            finally { RequestContext.Clear(); }

            var (keys, caller) = RequestCapture.Contexts[$"{GrainId.Create("test.cross-counter", counter)}#Value"];
            Assert.DoesNotContain("test.forged", keys);
            Assert.DoesNotContain("digitalbrain.trusted-module-call", keys);
            Assert.Equal(Id(one), caller?.ForwardedBy);
            Assert.Equal(owner, caller?.PrincipalId);
        }
        finally { await Stop(trust, two, one); }
    }

    // R100: kernel and other cluster-storage grains have no policy of their own; a member's machine reaches only neuron contracts.
    [Fact(Timeout = 180_000)]
    public async Task AMemberMachineCannotForwardToAKernelGrain()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        var bob = await MemberMachine(trust, one, ct);
        try
        {
            var counter = NewCounter();
            await one.Grains.GetGrain<ICrossCounter>(counter).Add(1);
            var directory = RequestCapture.Requests.Keys.First(key => key.StartsWith($"kernel.instances/{Scope}/test.cross-counter#Register", StringComparison.Ordinal));
            var target = GrainId.Parse(directory[..directory.IndexOf('#', StringComparison.Ordinal)]);
            var bobCaller = new CallerContext { PrincipalId = "bob", AccountId = "bob", BrainId = Scope, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

            var outcome = await Send(bob, one, new ForwardedCall(target, Captured(one, directory), bobCaller, null));

            var refused = Assert.IsType<UnauthorizedAccessException>(outcome.Error);
            Assert.Contains("only the owner's machines", refused.Message, StringComparison.Ordinal);
        }
        finally { await Stop(trust, bob, two, one); }
    }

    // R101: a call through the gateway (an MCP's Orleans client) to a part another machine holds runs at that holder.
    [Fact(Timeout = 180_000)]
    public async Task AGatewayCallForAPartHeldElsewhereRunsAtItsHolder()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = NewCounter();
            await one.Grains.GetGrain<ICrossCounter>(counter).Touch();
            var cluster = two.SiloServices.GetRequiredService<global::Microsoft.Extensions.Options.IOptions<Orleans.Configuration.ClusterOptions>>().Value;
            var gateway = two.SiloServices.GetRequiredService<ILocalSiloDetails>().GatewayAddress.Endpoint.Port;
            var builder = global::Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new global::Microsoft.Extensions.Hosting.HostApplicationBuilderSettings { DisableDefaults = true });
            builder.UseOrleansClient(client => client.UseLocalhostClustering(gateway, cluster.ServiceId, cluster.ClusterId));
            using var host = builder.Build();
            await host.StartAsync(ct);
            try
            {
                var client = host.Services.GetRequiredService<IClusterClient>();

                Assert.Equal(5, await client.GetGrain<ICrossCounter>(counter).Add(5));

                Assert.Equal(1, CrossCounterNeuron.Adds[counter]);
                Assert.Equal(5, await one.Grains.GetGrain<ICrossCounter>(counter).Value());
                Assert.DoesNotContain(PartId.Of(counter), two.SiloServices.GetRequiredService<PartLogs>().Opened);
                // The stand-in on two read no state (the part never opened there), wrote nothing and ran no activation
                // handler (BrainActivation's Activated, DeploymentReconcile and the like are handlers too).
                Assert.DoesNotContain((Id(two), counter), ActivationRecord.Seen);
                Assert.Contains((Id(one), counter), ActivationRecord.Seen);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally { await Stop(trust, two, one); }
    }

    // R103: a one-way call crosses without its caller waiting: the echo's call back into the non-reentrant caller lands.
    [Fact(Timeout = 180_000)]
    public async Task AOneWayCallAcrossMachinesDoesNotWaitForItsOutcome()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var echo = $"{Scope}/apps/counter/e{Guid.NewGuid():N}";
            var notes = $"{Scope}/apps/notes/n{Guid.NewGuid():N}";
            await two.Grains.GetGrain<IEcho>(echo).Touch();
            var recorded = NoteRelayNeuron.Recorded.GetOrAdd(notes, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

            await one.Grains.GetGrain<INoteRelay>(notes).Start(echo);

            await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        finally { await Stop(trust, two, one); }
    }

    // R107: a member's machine speaks only for users and apps; a forged Platform stamp for its own account is refused.
    [Fact(Timeout = 180_000)]
    public async Task AForgedPlatformStampFromAMemberMachineIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        var bob = await MemberMachine(trust, one, ct);
        try
        {
            var counter = NewCounter();
            await one.Grains.GetGrain<ICrossCounter>(counter).Add(1);
            var target = GrainId.Create("test.cross-counter", counter);
            var forged = new CallerContext { PrincipalId = "bob", AccountId = "bob", BrainId = Scope, Kind = CallerKind.Platform, StampedBy = TrustedEdge.Platform };

            var outcome = await Send(bob, one, new ForwardedCall(target, Captured(one, $"{target}#Add"), forged, null));

            var refused = Assert.IsType<UnauthorizedAccessException>(outcome.Error);
            Assert.Contains("Platform caller", refused.Message, StringComparison.Ordinal);
            Assert.Equal(1, CrossCounterNeuron.Adds[counter]);
        }
        finally { await Stop(trust, bob, two, one); }
    }

    // R107: a member's machine cannot stamp the owner's principal on its own account; its own principal still passes this gate.
    [Fact(Timeout = 180_000)]
    public async Task AForgedOwnerPrincipalFromAMemberMachineIsRefused()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        var bob = await MemberMachine(trust, one, ct);
        try
        {
            var counter = NewCounter();
            await one.Grains.GetGrain<ICrossCounter>(counter).Add(1);
            var target = GrainId.Create("test.cross-counter", counter);
            static CallerContext As(string principal) => new() { PrincipalId = principal, AccountId = "bob", BrainId = Scope, Kind = CallerKind.User, StampedBy = TrustedEdge.AuthenticatedHttp };

            var forged = await Send(bob, one, new ForwardedCall(target, Captured(one, $"{target}#Add"), As(AccountSession.DefaultLogin), null));
            var own = await Send(bob, one, new ForwardedCall(target, Captured(one, $"{target}#Add"), As("bob"), null));

            var refused = Assert.IsType<UnauthorizedAccessException>(forged.Error);
            Assert.Contains("does not belong to account bob", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("does not belong", own.Error?.Message ?? "", StringComparison.Ordinal);
        }
        finally { await Stop(trust, bob, two, one); }
    }

    // A forwarded call publishes as the neuron that ran it: the holder's log carries the counter as publisher.
    [Fact(Timeout = 180_000)]
    public async Task AForwardedCallsSignalKeepsItsNeuronAsPublisher()
    {
        SkipWithoutQuic();
        var ct = TestContext.Current.CancellationToken;
        var (trust, one, two) = await Machines(ct);
        try
        {
            var counter = $"{Scope}/apps/counter/s{Guid.NewGuid():N}";
            await two.Grains.GetGrain<ISignalCounter>(counter).Change(1);

            await one.Grains.GetGrain<ISignalCounter>(counter).Change(5);

            var log = await two.SiloServices.GetRequiredService<PartLogs>().For(PartId.Of(counter));
            await log.Flushed();
            var signal = Assert.IsType<Changed>(Assert.Single(log.SignalsSince(0)[^1].Signals));
            Assert.Equal(5, signal.Value);
            Assert.Equal($"test.signal-counter/{counter}", signal.Publisher.Name);
        }
        finally { await Stop(trust, two, one); }
    }
}
