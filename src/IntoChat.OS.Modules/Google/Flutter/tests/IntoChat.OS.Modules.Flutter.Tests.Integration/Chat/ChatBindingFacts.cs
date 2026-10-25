using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

public sealed class ChatBindingFacts
{
    [Fact]
    public void SelectionIsAnExplicitCompareAndSetOperation()
    {
        Assert.NotNull(typeof(IChat).GetMethod("SelectAgent", [typeof(string), typeof(long), typeof(Guid)]));
        Assert.NotNull(typeof(ChatState).GetProperty("Selection"));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SelectionReceiptsPreventReplayFromNavigatingAndRejectManualWriters()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        var chat = brain.Get<IChat>("owner/apps/test/chat");
        var firstId = Guid.NewGuid();
        var first = await chat.SelectAgent("owner/apps/test/a", 0, firstId);
        var second = await chat.SelectAgent("owner/apps/test/b", first.Revision, Guid.NewGuid());
        Assert.Equal(first, await chat.SelectAgent(first.Agent, 0, firstId));
        Assert.Equal(second, (await chat.Read()).Selection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SelectAgent(second.Agent, 0, firstId));
        await Assert.ThrowsAsync<RevisionConflictException>(() => chat.SelectAgent(first.Agent, first.Revision, Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.SelectAgent("someone/apps/test/a", second.Revision, Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.Project(new(second.Agent, second.Revision, 99, [])));
        await brain.DeactivateAsync(chat, ct);
        Assert.Equal(second, (await chat.Read()).Selection);
        Assert.Equal(first, await chat.SelectAgent(first.Agent, 0, firstId));
        Assert.Equal(second, (await chat.Read()).Selection);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AgentChangesProjectNativelyAndMigrationRetainsReadReceipts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var key = "owner/apps/test/chat";
        var source = "owner/apps/test/a";
        var chat = brain.Get<IChat>(key);
        var text = brain.Get<IChatSourceText>(source);
        await text.Set("answer");
        var adapter = new TestChatAgentSource(brain.Services.GetRequiredService<IGrainFactory>());
        var snapshot = await adapter.Read(source, 7);
        await chat.Project(snapshot.Projection);
        await chat.MarkRead(source, 7, snapshot.Projection.Version, ["answer"]);
        await brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IChatMigration>(key).AdoptAgent(source, 7, Guid.NewGuid());
        Assert.Empty((await chat.Read()).UnreadMessageIds);
        await using var changed = await brain.Observe<ChatChanged>(chat, ct);
        await text.Set("new answer");
        await changed.NextAsync(ct: ct);
        Assert.Equal("new answer", Assert.Single((await chat.Read()).Projection!.Messages).Text);
        Assert.Equal(["answer"], (await chat.Read()).UnreadMessageIds);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ProjectionsCarryTheModelLabelAndTheAppCard()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        using var caller = brain.Owner.AsCaller();
        var chat = brain.Get<IChat>("owner/apps/test/cards");
        var card = new ChatAppCard("notes", "Notes", "note", false, "opaque <input>", "vlad", "1.2.3");
        await chat.Project(new("owner/apps/test/a", 3, 1,
            [new ChatEntry("r1/assistant", ChatRole.Assistant, "Opened.") { Model = "gpt-5-mini", App = card }]));
        await brain.DeactivateAsync(chat, ct);
        var entry = Assert.Single((await chat.Read()).Projection!.Messages);
        Assert.Equal("gpt-5-mini", entry.Model);
        Assert.Equal(card, entry.App);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SameSourceSelectionKeepsTheVisibleRevisionButRetainsItsOperationReceipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        var chat = brain.Get<IChat>("owner/apps/test/same");
        var first = await chat.SelectAgent("owner/apps/test/a", 0, Guid.NewGuid());
        var before = await chat.Read();
        var repeat = Guid.NewGuid();
        Assert.Equal(first, await chat.SelectAgent(first.Agent, first.Revision, repeat));
        Assert.Equal(before.Revision, (await chat.Read()).Revision);
        var second = await chat.SelectAgent("owner/apps/test/b", first.Revision, Guid.NewGuid());
        Assert.Equal(first, await chat.SelectAgent(first.Agent, 0, repeat));
        Assert.Equal(second, (await chat.Read()).Selection);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AdoptingAnotherProjectedSourceRequiresAStrictlyNewerGeneration()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/apps/test/adoption";
        var chat = brain.Get<IChat>(key);
        await chat.Project(new("owner/apps/test/a", 7, 1, [new("a", ChatRole.Assistant, "prior answer")]));
        var migration = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IChatMigration>(key);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migration.AdoptAgent("owner/apps/test/b", 7, Guid.NewGuid()));
        await migration.AdoptAgent("owner/apps/test/b", 8, Guid.NewGuid());
        var state = await chat.Read();
        Assert.Equal(new ChatSelection("owner/apps/test/b", 8), state.Selection);
        Assert.Equal("owner/apps/test/b", state.Projection!.Source);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task NeverBoundChatDoesNotScheduleSourceReconciliation()
    {
        var ct = TestContext.Current.CancellationToken;
        var calls = new ReconciliationCalls();
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IOutgoingGrainCallFilter>(calls)).StartAsync(ct);
        var chat = brain.Get<IChat>("owner/apps/test/manual");
        Assert.Null((await chat.Read()).Selection);
        await chat.Project(new("manual", 1, 1, []));
        await chat.Read();
        Assert.Equal(0, Volatile.Read(ref calls.Count));
    }

    private sealed class ReconciliationCalls : IOutgoingGrainCallFilter
    {
        public int Count;
        public Task Invoke(IOutgoingGrainCallContext context)
        {
            if (context.InterfaceMethod?.DeclaringType?.Name == "IChatReconciliation")
            { Interlocked.Increment(ref Count); }
            return context.Invoke();
        }
    }
    [Trait("host", "own")]
    [Fact]
    public async Task MigrationRetryDoesNotRewindAConversationSelectedByTheNewProgram()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/apps/test/retry";
        var chat = brain.Get<IChat>(key);
        var migration = brain.Services.GetRequiredService<IGrainFactory>().GetGrain<IChatMigration>(key);
        var operation = Guid.NewGuid();
        await migration.AdoptAgent("owner/apps/test/a", 7, operation);
        var next = await chat.SelectAgent("owner/apps/test/b", 7, Guid.NewGuid());
        await migration.AdoptAgent("owner/apps/test/a", 7, operation);
        Assert.Equal(next, (await chat.Read()).Selection);
        await Assert.ThrowsAsync<InvalidOperationException>(() => migration.AdoptAgent("owner/apps/test/b", 7, operation));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task FailedNewSourceProjectionCannotExposeThePreviousConversationsRun()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(s => s.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/apps/test/failure";
        var chat = brain.Get<IChat>(key);
        await brain.Get<IChatSourceText>(key + "/a").Set("answer");
        var first = await chat.SelectAgent(key + "/a", 0, Guid.NewGuid());
        var prior = await chat.Read();
        Assert.NotNull(prior.Run);
        await using var runChanges = await brain.Observe<ChatRunChanged>(chat, ct);
        var adapter = (TestChatAgentSource)brain.Services.GetRequiredService<IChatAgentSource>();
        adapter.FailSource = key + "/b";
        adapter.ReadsUntilFailure = 1; // Authorization succeeds; projection recovery fails after selection commits.
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SelectAgent(key + "/b", first.Revision, Guid.NewGuid()));
        var current = await chat.Read();
        Assert.Equal(key + "/b", current.Selection!.Agent);
        Assert.Null(current.Run);
        Assert.Null(current.Projection);
        Assert.True(current.ActivityRevision > prior.ActivityRevision);
        Assert.Equal(current.ActivityRevision, (await runChanges.NextAsync(ct: ct)).ActivityRevision);
    }
}

internal sealed class TestChatAgentSource(IGrainFactory grains) : IChatAgentSource
{
    public string? FailSource { get; set; }
    public int ReadsUntilFailure { get; set; }
    public INeuron Source(string key) => grains.GetGrain<IChatSourceText>(key);
    public async Task<ChatAgentSnapshot> Read(string key, long selectionRevision)
    {
        if (key == FailSource && ReadsUntilFailure-- <= 0) { throw new InvalidOperationException("Source unavailable."); }
        var text = await grains.GetGrain<IChatSourceText>(key).Read();
        return new(new(key, selectionRevision, text.Version,
            text.Markdown.Length == 0 ? [] : [new("answer", ChatRole.Assistant, text.Markdown)]),
            text.Markdown.Length == 0 ? null : new(key, selectionRevision, text.Version, "answer", ChatRunStatus.Completed));
    }
}
