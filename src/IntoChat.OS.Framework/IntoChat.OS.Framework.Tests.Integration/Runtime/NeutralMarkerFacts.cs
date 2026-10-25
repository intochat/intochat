using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Concurrency;
using Orleans.Serialization.Invocation;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;

namespace IntoChat.OS.Framework.Tests.Integration.Runtime;

// Clean-core PR 4: the neutral scheduling attributes on methods a grain interface inherits from an Orleans-free
// contract are honoured by Orleans, one provider fact per marker, and the inherited methods keep the invokable
// ids Orleans gives the same methods declared with its own attributes.
[Trait("host", "own")]
public sealed class NeutralMarkerFacts(NeutralMarkerFacts.MarkerHost host) : IClassFixture<NeutralMarkerFacts.MarkerHost>
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    public sealed class MarkerHost : BrainHostFixture
    {
        protected override TestBrain Configure(TestBrain brain) =>
            brain.ConfigureServices(services => services.AddSingleton<IOutgoingGrainCallFilter, NeutralCallFilter>());
    }

    // Orleans names an invokable ("inv", GrainReference, declaring interface, id) with id = [Id] ?? [Alias] ?? a hash
    // of the method's signature (Orleans.CodeGenerator GeneratedCodeUtilities.GetMethodId); scheduling attributes
    // take no part. So the neutral contract keeps today's ids provided its methods are not redeclared on the face:
    // each is generated once, under the contract, and the attributed twin with the same signatures gets
    // invokables of its own the same way. The ids are pinned so any change to them fails here.
    [Fact]
    public void InheritedMethodsKeepTheirInvokableIdsUnderTheContract()
    {
        var neutral = InvokableIds(typeof(IMarkedWork));
        var attributed = InvokableIds(typeof(Legacy.IMarkedWork));
        TestContext.Current.SendDiagnosticMessage("Invokable ids: " + string.Join(", ", neutral.OrderBy(m => m.Key).Select(m => $"{m.Key}={m.Value}")));

        Assert.Equal(typeof(IMarkedWork).GetMethods().Select(m => m.Name).Order(), neutral.Keys.Order());
        Assert.Equal(neutral.Keys.Order(), attributed.Keys.Order());
        Assert.Empty(InvokableIds(typeof(IMarkedGrain)));
        Assert.Equal(new Dictionary<string, string>
        {
            ["Count"] = "B41AA0ED",
            ["Hold"] = "3DDC29A5",
            ["Peek"] = "9A92C77C",
            ["Post"] = "102E11E3",
            ["Probe"] = "8D24D36C",
            ["ReadCount"] = "50D653B7",
            ["Stall"] = "89D0936D",
        }, neutral);
    }

    [Fact]
    public async Task AnInterleavesMethodRunsWhileATurnIsHeld()
    {
        await using var held = await Hold("interleaves");
        Assert.Equal(0, await held.Neuron.Peek().WaitAsync(held.Within, held.Ct));
        await AssertQueued(held, held.Neuron.Count());
    }

    [Fact]
    public async Task AReadOnlyCallMethodRunsWhileATurnIsHeld()
    {
        await using var held = await Hold("read-only");
        Assert.Equal(0, await held.Neuron.ReadCount().WaitAsync(held.Within, held.Ct));
        await AssertQueued(held, held.Neuron.Count());
    }

    [Fact]
    public async Task AnInterleavesWhenPredicateDecidesPerCall()
    {
        await using var held = await Hold("interleaves-when");
        Assert.Equal("fast", await held.Neuron.Probe("fast").WaitAsync(held.Within, held.Ct));
        await AssertQueued(held, held.Neuron.Probe("slow"));
    }

    [Fact]
    public async Task AOneWayCallMethodReleasesTheCallerAndStillRuns()
    {
        await using var held = await Hold("one-way");
        await held.Neuron.Post(7).WaitAsync(held.Within, held.Ct);
        held.Release();
        while (await held.Neuron.Count().WaitAsync(held.Within, held.Ct) != 7) { await Task.Delay(20, held.Ct); }
    }

    [Fact]
    public async Task ACallTimeoutMethodTimesOutAtItsOwnTimeout()
    {
        await using var held = await Hold("timeout");
        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAsync<TimeoutException>(() => held.Neuron.Stall().WaitAsync(held.Within, held.Ct));
        Assert.InRange(DateTimeOffset.UtcNow - started, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
    }

    private async Task<Held> Hold(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var key = $"markers/{name}-{Guid.NewGuid():N}";
        var neuron = host.Brain.Get<IMarkedGrain>(key);
        var gate = MarkedNeuron.Gate(key);
        var hold = neuron.Hold();
        await gate.Entered.Task.WaitAsync(host.Brain.Execution.AssertionTimeout, ct);
        return new Held(neuron, gate, hold, host.Brain.Execution.AssertionTimeout, ct);
    }

    private static async Task AssertQueued(Held held, Task call)
    {
        await Task.Delay(Blocked, held.Ct);
        Assert.False(call.IsCompleted, "An unmarked call ran while the turn was held.");
        held.Release();
        await call.WaitAsync(held.Within, held.Ct);
    }

    // Method name -> invokable id, read from the ("inv", GrainReference, interface, id) alias Orleans generates.
    private static Dictionary<string, string> InvokableIds(Type contract) => typeof(MarkedNeuron).Assembly.GetTypes()
        .Where(type => typeof(IInvokable).IsAssignableFrom(type) && !type.IsAbstract && !type.ContainsGenericParameters)
        .Select(type => (Type: type, Alias: type.GetCustomAttribute<CompoundTypeAliasAttribute>()))
        .Where(entry => entry.Alias is { Components: [_, _, Type face, string] } && face == contract)
        .ToDictionary(entry => ((IInvokable)Activator.CreateInstance(entry.Type)!).GetMethodName(), entry => (string)entry.Alias!.Components[3]);

    private sealed class Held(IMarkedGrain neuron, MarkedNeuron.Turn gate, Task hold, TimeSpan within, CancellationToken ct) : IAsyncDisposable
    {
        public IMarkedGrain Neuron { get; } = neuron;
        public TimeSpan Within { get; } = within;
        public CancellationToken Ct { get; } = ct;
        public void Release() => gate.Release.TrySetResult();
        public async ValueTask DisposeAsync()
        {
            Release();
            await hold.WaitAsync(Within, CancellationToken.None);
        }
    }
}

// An Orleans-free contract: neutral attributes only, no grain base.
public interface IMarkedWork
{
    Task Hold();
    Task<int> Count();
    [Interleaves] Task<int> Peek();
    [ReadOnlyCall] Task<int> ReadCount();
    Task<string> Probe(string mode);
    [OneWayCall] Task Post(int count);
    [CallTimeout("00:00:00.500")] Task Stall();
}

// The provider's grain face: one line, nothing redeclared.
[Alias("tests.framework.marked")]
public interface IMarkedGrain : IMarkedWork, INeuron;

[MayInterleave(nameof(MayInterleave)), InterleavesWhen(nameof(Fast))]
public sealed class MarkedNeuron : Neuron, IMarkedGrain
{
    private static readonly ConcurrentDictionary<string, Turn> Gates = new(StringComparer.Ordinal);
    private int _count;

    public sealed class Turn
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public static Turn Gate(string key) => Gates.GetOrAdd(key, _ => new());

    public static bool MayInterleave(IInvokable request) => NeutralCallMapping.MayInterleave(typeof(MarkedNeuron), request);

    private static bool Fast(MethodInfo method, IReadOnlyList<object?> arguments) =>
        method.Name == nameof(Probe) && arguments is ["fast"];

    private Turn Mine => Gate(Context.Key);

    public async Task Hold()
    {
        Mine.Entered.TrySetResult();
        await Mine.Release.Task;
    }

    public Task<int> Count() => Task.FromResult(_count);

    public Task<int> Peek() => Task.FromResult(_count);

    public Task<int> ReadCount() => Task.FromResult(_count);

    public Task<string> Probe(string mode) => Task.FromResult(mode);

    public Task Post(int count)
    {
        _count = count;
        return Task.CompletedTask;
    }

    public Task Stall() => Mine.Release.Task;
}
