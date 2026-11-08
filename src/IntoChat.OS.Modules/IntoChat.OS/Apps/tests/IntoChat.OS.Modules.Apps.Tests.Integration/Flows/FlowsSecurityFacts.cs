using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Flows;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using System.Collections.Concurrent;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.Apps.Flows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Integration.Flows;

// The agent context of the facts. The real one is in AIModule, whose "agent" neuron type collides with the facts' ScriptedAgent in this
// assembly, so this keeps entries as the real one does (trust stamped from the capability caller) and publishes its signals.
[DigitalBrain.Neuron("agent-context")]
public sealed class FactsAgentContext : DigitalBrain.Neuron<PumpState>, IAgentContext
{
    private static readonly ConcurrentDictionary<string, ContextEntry[]> Entries = new(StringComparer.Ordinal);

    private string Full => $"{Id.Brain.Value}/{Id.Key}";

    public Task Set(ContextEntry entry)
    {
        var caller = RuntimeCallerContext.Current;
        var kept = entry with { Provenance = new EntryProvenance(caller?.PrincipalId ?? "", DateTimeOffset.UtcNow, ContextTrust.Of(caller)) };
        var entries = Entries.AddOrUpdate(Full, [kept], (_, old) => [.. old.Where(item => item.Name != entry.Name), kept]);
        return Save(State with { Sent = State.Sent + 1 }, new ContextUpdated(Full, State.Sent + 1, entry.Name));
    }

    public Task Remove(string name)
    {
        Entries.AddOrUpdate(Full, [], (_, old) => [.. old.Where(item => item.Name != name)]);
        return Save(State with { Sent = State.Sent + 1 }, new ContextRemoved(Full, State.Sent + 1, name));
    }

    public Task<ContextSnapshot> Read() => Task.FromResult(new ContextSnapshot(State.Sent, Entries.GetValueOrDefault(Full, [])));
}

// A trusted security interrupt on a real silo: it refuses Ino flow starts, and with the cancel-flows tag cancels Ino flows that run.
public sealed class FlowsSecurityFacts
{
    private const string Conversation = "ino/conversations/main";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class SecurityFixture : BothRuntimesFixture
    {
        protected override IReadOnlyList<Type> Modules => [typeof(AppsModule), typeof(FlowsFactsModule)];

        public override BrainId Brain { get; } = new("security-" + Guid.NewGuid().ToString("N"));

        protected override void ConfigureSilo(IServiceCollection services)
        {
            services.AddJournalPayloads(typeof(IFlows).Assembly);
            services.AddJournalPayloads(typeof(AppsModule).Assembly);
            services.AddJournalPayloads(typeof(FlowStarted).Assembly);
            services.AddJournalPayloads(typeof(TestFlow).Assembly);
            services.AddJournalPayloads(typeof(ContextUpdated).Assembly);
            services.TryAddSingleton(TimeProvider.System);
        }
    }

    private static IFlows Flows(INeuronRuntime runtime) => runtime.Get<IFlows>(IFlows.Key(runtime.Brain.Value));

    private static IAgentContext Posture(INeuronRuntime runtime)
        => runtime.Get<IAgentContext>(new NeuronId(runtime.Brain, "agent-context", "context/security.posture"));

    private static string Inner() => JsonSerializer.Serialize(new TestInner { Wait = true }, TestFlow.Json);

    private static IDisposable As(INeuronRuntime runtime, CallerKind kind)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value, kind));

    // An agent's tool call as ContractActions.Marked stamps it: AppId "assistant" and the conversation.
    private static IDisposable AsInoTool(INeuronRuntime runtime)
        => RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "person", runtime.Brain.Value, CallerKind.Agent, conversationId: Conversation));

    private static bool IsRefusal(Exception error) => error.ToString().Contains("security interrupt", StringComparison.Ordinal);

    private static async Task<string> StartInoFlow(INeuronRuntime runtime, string title)
    {
        string id;
        using (AsInoTool(runtime)) { id = await Flows(runtime).Start(TestFlow.Alias, title, null, Inner(), Ct); }
        await Eventually(runtime, id, view => view.Status == FlowStatus.Waiting);
        return id;
    }

    private static async Task<FlowView> Eventually(INeuronRuntime runtime, string flowId, Func<FlowView, bool> done)
    {
        FlowView? view = null;
        for (var i = 0; i < 200; i++)
        {
            view = await Flows(runtime).Read(flowId, Ct);
            if (view is not null && done(view)) { return view; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail($"The flow {flowId} did not reach the expected view; last: {view}");
        return view!;
    }

    // Tries an Ino start until the index has folded the interrupt, and returns the refusal.
    private static async Task<Exception> RefusedEventually(INeuronRuntime runtime)
    {
        for (var i = 0; i < 200; i++)
        {
            try
            {
                string id;
                using (AsInoTool(runtime)) { id = await Flows(runtime).Start(TestFlow.Alias, "Probe", null, Inner(), Ct); }
                await Flows(runtime).Cancel(id, "probe", Ct);
            }
            catch (Exception error) when (IsRefusal(error)) { return error; }
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The Ino start was not refused.");
        return null!;
    }

    [Fact]
    public async Task StartRefusedUnderTrustedInterrupt()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Hold everything.", [], [ContextTags.Interrupt])); }

        var refusal = await RefusedEventually(runtime);
        Assert.Contains("blocked by security interrupt: Hold everything.", refusal.ToString(), StringComparison.Ordinal);

        // A person's or the system's start is not refused.
        using (As(runtime, CallerKind.User)) { Assert.NotNull(await Flows(runtime).Start(TestFlow.Alias, "By person", null, Inner(), Ct)); }
        Assert.NotNull(await Flows(runtime).Start(TestFlow.Alias, "By system", null, Inner(), Ct));

        // Removing the interrupt lifts the refusal.
        await Posture(runtime).Remove("stop");
        for (var i = 0; i < 200; i++)
        {
            try
            {
                using (AsInoTool(runtime)) { await Flows(runtime).Start(TestFlow.Alias, "After", null, Inner(), Ct); }
                return;
            }
            catch (Exception error) when (IsRefusal(error)) { await Task.Delay(50, Ct); }
        }
        Assert.Fail("The interrupt was removed but Ino starts stay refused.");
    }

    [Fact]
    public async Task ModelTrustInterruptDoesNotRefuse()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var running = await StartInoFlow(runtime, "Running");

        using (As(runtime, CallerKind.Agent)) { await Posture(runtime).Set(new ContextEntry("stop", "Model says stop.", [], [ContextTags.Interrupt, "cancel-flows"])); }
        await Task.Delay(500, Ct);

        var started = await StartInoFlow(runtime, "Still allowed");
        Assert.NotEqual(running, started);
        Assert.Equal(FlowStatus.Waiting, (await Flows(runtime).Read(running, Ct))!.Status);
    }

    [Fact]
    public async Task CancelFlowsTagCancelsRunningInoFlows()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var ino = await StartInoFlow(runtime, "Ino flow");
        string person;
        using (As(runtime, CallerKind.User)) { person = await Flows(runtime).Start(TestFlow.Alias, "Person flow", null, Inner(), Ct); }
        await Eventually(runtime, person, view => view.Status == FlowStatus.Waiting);

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Stop and cancel.", [], [ContextTags.Interrupt, "cancel-flows"])); }

        var cancelled = await Eventually(runtime, ino, view => view.Status == FlowStatus.Cancelled);
        Assert.Equal("security interrupt", cancelled.Detail);
        Assert.Equal(FlowStatus.Waiting, (await Flows(runtime).Read(person, Ct))!.Status);
    }

    [Fact]
    public async Task PlainInterruptLeavesRunningFlowsToPauseAtNextWait()
    {
        await using var fixture = new SecurityFixture();
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, Ct);
        await Flows(runtime).List(new FlowQuery(), Ct);
        var ino = await StartInoFlow(runtime, "Ino flow");

        using (As(runtime, CallerKind.User)) { await Posture(runtime).Set(new ContextEntry("stop", "Just pause.", [], [ContextTags.Interrupt])); }
        await RefusedEventually(runtime);
        await Task.Delay(500, Ct);

        var view = (await Flows(runtime).Read(ino, Ct))!;
        Assert.Equal(FlowStatus.Waiting, view.Status);
        Assert.Null(view.Detail);
    }
}
