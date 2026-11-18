using DigitalBrain.Rules;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents.Context;

using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// An interrupt entry a user or the platform wrote is cleared by their trust level only.
public sealed class AgentContextFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static async Task<IAgentContext> Held(INeuronRuntime runtime, string name, CancellationToken ct)
    {
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/" + name));
        await Task.Run(async () =>
        {
            using var _ = runtime.As(Who.Person, "person", "acc");
            await context.Set(new("stop", "halt now", [], [ContextTags.Interrupt]));
        }, ct);
        return context;
    }

    private static Task As(Who kind, INeuronRuntime runtime, Func<Task> act, CancellationToken ct) => Task.Run(async () =>
    {
        using var _ = runtime.As(kind, "who", "acc");
        await act();
    }, ct);

    [Fact]
    public async Task ModelCannotClearATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);
        var context = await Held(runtime, "model-clear", ct);

        foreach (var kind in new[] { Who.Agent, Who.App })
        {
            var overwrite = await Assert.ThrowsAsync<RuleViolationException>(() => As(kind, runtime, () => context.Set(new("stop", "all clear", [], [])), ct));
            Assert.Contains("interrupt entries are cleared by their trust level only", overwrite.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<RuleViolationException>(() => As(kind, runtime, () => context.Remove("stop"), ct));
        }

        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal("halt now", entry.Text);
        Assert.Contains(ContextTags.Interrupt, entry.EffectiveTags);
    }

    // The race the capabilities writer had: its read saw no interrupt, then one was stamped. The writer's Upsert runs under
    // platform trust, yet the neuron's own turn refuses it, so the interrupt written in between is never dropped.
    [Fact]
    public async Task AProducerUpsertNeverDropsAnInterruptWrittenBeforeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/upsert-race"));

        await As(Who.Kernel, runtime, () => context.Set(new("notes", "halt now", [], [ContextTags.Interrupt])), ct);
        var written = true;
        var withdrawn = true;
        await As(Who.Kernel, runtime, async () => written = await context.Upsert(new("notes", "notes.add", [], [])), ct);
        await As(Who.App, runtime, async () => withdrawn = await context.Withdraw("notes"), ct);
        await Assert.ThrowsAsync<RuleViolationException>(() => As(Who.App, runtime, () => context.Set(new("notes", "notes.add", [], [])), ct));

        Assert.False(written);
        Assert.False(withdrawn);
        var entry = Assert.Single((await context.Read()).Entries, item => item.Name == "notes");
        Assert.Equal("halt now", entry.Text);
        Assert.Contains(ContextTags.Interrupt, entry.EffectiveTags);
    }

    [Fact]
    public async Task UserCanClearATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        using var person = runtime.As(Who.Person);
        var context = await Held(runtime, "user-clear", ct);

        await As(Who.Kernel, runtime, () => context.Set(new("stop", "platform overwrite", [], [ContextTags.Interrupt])), ct);
        Assert.Equal("platform overwrite", Assert.Single((await context.Read()).Entries).Text);

        await As(Who.Person, runtime, () => context.Remove("stop"), ct);
        Assert.Empty((await context.Read()).Entries);
    }

    // A call with no caller cannot clear a trusted interrupt; the platform can. It is refused at the gate now, not read as
    // app trust (task 1: AccessGate admits no unstamped call).
    [Fact]
    public async Task UnstampedCallerCannotClearATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        var context = await Held(runtime, "unstamped-clear", ct);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => context.Remove("stop"), ct));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => context.Set(new("stop", "all clear", [], [])), ct));
        using (runtime.As(Who.Person)) { Assert.Equal("halt now", Assert.Single((await context.Read()).Entries).Text); }

        await As(Who.Kernel, runtime, () => context.Remove("stop"), ct);
        using (runtime.As(Who.Person)) { Assert.Empty((await context.Read()).Entries); }
    }
}
