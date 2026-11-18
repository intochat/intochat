using DigitalBrain.Capabilities;
using DigitalBrain.Testing;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.AI.Agents.Context;
using IntoChat.OS.Modules.AI.Agents.Context.Signals;
using IntoChat.OS.Modules.AI.Agents.Signals;
using IntoChat.OS.Modules.AI.Embeddings;
using IntoChat.OS.Modules.AI.Embeddings.Signals;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Memory;
using IntoChat.OS.Modules.AI.Metering;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Models.Signals;

using IntoChat.OS.Tests;

namespace IntoChat.OS.Modules.AI.Tests;

// The plain AI neurons (06-05), one fact body per behaviour. The Unit tier runs each on TestOS; the Integration tier compiles
// this same file and runs each on TestOS and on SiloHost.Local, so the same fact holds on both runtimes (R4-05).
public sealed class AiNeuronFacts(AiFixture fixture) : IClassFixture<AiFixture>
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(20);

    public static TheoryData<NeuronRuntimeKind> Kinds => AiFixture.Kinds;

    [Theory, MemberData(nameof(Kinds))]
    public async Task AContextEntryIsReplacedByNameAndRemoved(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent-context", "memory/context-" + kind);
        var context = runtime.Get<IAgentContext>(id);

        await context.Set(new("working", "first", []));
        await context.Set(new("working", "second", ["notes"]));
        Assert.Equal("second", Assert.Single((await context.Read()).Entries).Text);
        await context.Remove("working");

        Assert.Empty((await context.Read()).Entries);
        Assert.Equal(2, (await runtime.WaitForSignals<ContextUpdated>(id, 2, Within, ct)).Count);
        Assert.Equal(3, Assert.Single(await runtime.WaitForSignals<ContextRemoved>(id, 1, Within, ct)).Revision);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnEntryIsStampedWithWriterAndTrust(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent-context", "memory/context-stamp-" + kind);
        var context = runtime.Get<IAgentContext>(id);

        await WrittenBy(runtime, Who.Person, () => context.Set(new("working", "x", [])));

        var provenance = Assert.Single((await context.Read()).Entries).Provenance;
        Assert.Equal(("p1", "user"), (provenance!.Writer, provenance.Trust));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnUnstampedWriterIsRefused(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "memory/context-unstamped-" + kind));

        // An unstamped write was taken at app trust; the gate now refuses a call with no caller (task 1).
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => context.Set(new("working", "x", [])));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AModelWriterCannotInterrupt(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent-context", "memory/context-interrupt-" + kind);
        var context = runtime.Get<IAgentContext>(id);

        await WrittenBy(runtime, Who.Agent, () => context.Set(new("stop", "halt", [], [ContextTags.Interrupt])));

        var entry = Assert.Single((await context.Read()).Entries);
        Assert.Equal((ContextTags.Interrupt, "model"), (Assert.Single(entry.EffectiveTags), entry.Provenance!.Trust));
        Assert.True(Assert.Single(await runtime.WaitForSignals<ContextUpdated>(id, 1, Within, ct)).Ignored);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AnAppWriterIsNamedByItsAppId(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent-context", "memory/context-app-" + kind);
        var context = runtime.Get<IAgentContext>(id);

        await WrittenBy(runtime, Who.App, () => context.Set(new("working", "x", [])));

        Assert.Equal("notes", Assert.Single((await context.Read()).Entries).Provenance!.Writer);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task APlatformWriterCanInterrupt(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent-context", "memory/context-platform-" + kind);
        var context = runtime.Get<IAgentContext>(id);

        await WrittenBy(runtime, Who.Kernel, () => context.Set(new("stop", "halt", [], [ContextTags.Interrupt])));

        Assert.Equal("platform", Assert.Single((await context.Read()).Entries).Provenance!.Trust);
        Assert.False(Assert.Single(await runtime.WaitForSignals<ContextUpdated>(id, 1, Within, ct)).Ignored);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task TooManyTagsAreRejected(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var context = runtime.Get<IAgentContext>(runtime.Id("agent-context", "memory/context-tags-" + kind));

        await Assert.ThrowsAsync<ArgumentException>(() => context.Set(new("busy", "x", [], ["1", "2", "3", "4", "5", "6", "7", "8", "9"])));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task AVectorIndexRanksByCosineAndAnUnchangedWriteKeepsItsRevision(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("ai.vector-index", "memory/life-" + kind);
        var index = runtime.Get<IVectorIndex>(id);
        VectorEntry[] entries = [new("near", [1, 0], new() { ["kind"] = "a" }), new("far", [0, 1], new() { ["kind"] = "a" })];

        var written = await index.Replace(entries);
        Assert.Equal(written.Revision, (await index.Replace(entries)).Revision);

        var matches = await index.Nearest([1, 0.1f], 2);
        Assert.Equal(["near", "far"], matches.Select(match => match.Id));
        Assert.Single(await runtime.WaitForSignals<VectorIndexChanged>(id, 1, Within, ct));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task RecordedUsageSurvivesARestart(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var runtime = await fixture.Start(kind, ct);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("ai-intent-usage", "intent-durable");
        await runtime.Get<IIntentUsage>(id).RecordAsync(new(MeterKind.Chat, "OpenAI", "gpt-5.6-luna", 10, 3, 2, 5, 15, true, DateTimeOffset.UnixEpoch), ct);

        await runtime.Restart(ct);

        var read = await runtime.Get<IIntentUsage>(id).ReadAsync(ct);
        Assert.Equal($"{id.Brain.Value}/intent-durable", read.IntentId);
        Assert.Equal(15, Assert.Single(read.Entries).TotalTokens);
    }

    // RT-01 opt-in reentrancy: [InterleavesWhen] lets Generate and Describe of one model neuron run together; every call still
    // journals its start and end once.
    [Theory, MemberData(nameof(Kinds))]
    public async Task TwoGenerationsOfOneModelRunTogetherAndEachIsJournaledOnce(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("ai.llm", "models");
        var model = runtime.Get<ILLM>(id);
        fixture.Inference.Expect(2);

        var answers = await Task.WhenAll(model.Generate(Ask("one"), ct), model.Generate(Ask("two"), ct));

        Assert.Equal(2, fixture.Inference.Overlap);
        Assert.All(answers, answer => Assert.Equal("ok", Assert.IsType<AiText>(Assert.Single(Assert.Single(answer.Messages).Content)).Text));
        var started = await runtime.WaitForSignals<InferenceStarted>(id, 2, Within, ct);
        var completed = await runtime.WaitForSignals<InferenceCompleted>(id, 2, Within, ct);
        Assert.Equal(started.Select(signal => signal.OperationId).Order(), completed.Select(signal => signal.OperationId).Order());
        Assert.All(started, signal => Assert.Equal($"{id.Brain.Value}/models", signal.NeuronId));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task DescribeAnswersWhileAGenerationIsRunning(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var model = runtime.Get<ILLM>(runtime.Id("ai.llm.gpt56luna", "models"));
        fixture.Inference.Expect(2);

        var held = model.Generate(Ask("held"), ct);
        var described = await model.Describe().WaitAsync(Within, ct);
        Assert.Equal("fake", described.Model);
        await model.Generate(Ask("release"), ct);
        await held;
    }

    // 06-06d: the agent is plain. Its configuration, posts and clearing need no model, so they hold on both runtimes; turns run
    // against the inference stack in the agent facts of the Integration tier.
    [Theory, MemberData(nameof(Kinds))]
    public async Task AnAgentKeepsItsConfigurationAndPostsAndClearsThemWithItsSignals(NeuronRuntimeKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var id = runtime.Id("agent", "agents/plain-" + kind);
        var agent = runtime.Get<IAgent>(id);

        await agent.Configure(new() { Instructions = "Be brief." }, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => agent.Configure(new(), 0));
        await agent.Post("hello", "greeting");
        await agent.Post("hello", "greeting");

        var state = await agent.Read();
        Assert.Equal((1L, "Be brief.", "post/greeting", true), (state.Revision, state.Definition.Instructions, Assert.Single(state.Runs).RunId, state.Runs[0].Posted));
        Assert.Single(await agent.Transcript());
        await agent.Clear();
        Assert.Empty(await agent.Transcript());
        Assert.Equal(2, (await agent.Read()).Revision);
        var key = $"{id.Brain.Value}/{id.Key}";
        Assert.Equal(key, Assert.Single(await runtime.WaitForSignals<AgentConfigured>(id, 1, Within, ct)).AgentId);
        Assert.Equal("post/greeting", Assert.Single(await runtime.WaitForSignals<TurnCompleted>(id, 1, Within, ct)).RunId);
        Assert.Equal(2, Assert.Single(await runtime.WaitForSignals<TranscriptCleared>(id, 1, Within, ct)).Revision);
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task MemoryNeuronCapturesEpisodesAndFactsAndRecallsByTopic(NeuronRuntimeKind kind)
    {
        var runtime = await fixture.Runtime(kind);
        using var person = runtime.As(Who.Person);
        var memory = runtime.Get<IMemory>(runtime.Id("ai.memory", "memory"));

        var ep = await memory.CaptureEpisode("user", "I prefer dark mode and clean C# apps", 5);
        var fact = await memory.RememberFact("preferences", "User prefers dark mode", 9);

        Assert.NotEmpty(ep.Id);
        Assert.NotEmpty(fact.Id);

        var recalled = await memory.Recall("dark mode", 5);
        Assert.Equal(2, recalled.Length);
        Assert.Contains(recalled, hit => hit.Kind == "fact");
        Assert.Contains(recalled, hit => hit.Kind == "episode");
    }

    // The stamp lives in the async-local of the Task.Run, so it never leaks into the next fact.
    private static Task WrittenBy(INeuronRuntime runtime, Who who, Func<Task> write) => Task.Run(() =>
    {
        runtime.As(who, "p1", "a1", app: "notes");
        return write();
    });

    private static InferenceRequest Ask(string text) => new([new("user", [new AiText(text)])]);
}
