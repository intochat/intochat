using DigitalBrain.Capabilities;
using DigitalBrain.Rules;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents.Context;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

// An interrupt entry a user or the platform wrote is cleared by their trust level only.
public sealed class AgentContextFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static async Task<IAgentContext> Held(INeuronRuntime runtime, string name, CancellationToken ct)
    {
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "context/" + name));
        await Task.Run(async () =>
        {
            using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Of("person", "acc", runtime.Brain.Value, CallerKind.User));
            await context.Set(new("stop", "halt now", [], [ContextTags.Interrupt]));
        }, ct);
        return context;
    }

    private static Task As(CallerKind kind, INeuronRuntime runtime, Func<Task> act, CancellationToken ct) => Task.Run(async () =>
    {
        using var _ = RuntimeCallerContext.Stamp(CapabilityCallers.Of("who", "acc", runtime.Brain.Value, kind));
        await act();
    }, ct);

    [Fact]
    public async Task ModelCannotClearATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        var context = await Held(runtime, "model-clear", ct);

        foreach (var kind in new[] { CallerKind.Agent, CallerKind.App })
        {
            var overwrite = await Assert.ThrowsAsync<RuleViolationException>(() => As(kind, runtime, () => context.Set(new("stop", "all clear", [], [])), ct));
            Assert.Contains("interrupt entries are cleared by their trust level only", overwrite.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<RuleViolationException>(() => As(kind, runtime, () => context.Remove("stop"), ct));
        }

        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal("halt now", entry.Text);
        Assert.Contains(ContextTags.Interrupt, entry.EffectiveTags);
    }

    [Fact]
    public async Task UserCanClearATrustedInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        var context = await Held(runtime, "user-clear", ct);

        await As(CallerKind.Platform, runtime, () => context.Set(new("stop", "platform overwrite", [], [ContextTags.Interrupt])), ct);
        Assert.Equal("platform overwrite", Assert.Single((await context.Read()).Entries).Text);

        await As(CallerKind.User, runtime, () => context.Remove("stop"), ct);
        Assert.Empty((await context.Read()).Entries);
    }

    // Unstamped writers are platform code (the audit in the Task 20 report); the rule names that exception.
    [Fact]
    public async Task UnstampedPlatformCallerMayClearAnInterrupt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(NeuronRuntimeKind.Silo, ct);
        var context = await Held(runtime, "unstamped-clear", ct);
        Assert.Single((await context.Read()).Entries);

        await Task.Run(() => context.Remove("stop"), ct);

        Assert.Empty((await context.Read()).Entries);
    }
}
