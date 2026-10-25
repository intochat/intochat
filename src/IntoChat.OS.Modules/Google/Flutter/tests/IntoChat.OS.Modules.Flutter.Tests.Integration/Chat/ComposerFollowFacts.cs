using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using Orleans;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Flutter.Tests.Integration.Chat;

public sealed class ComposerFollowFacts
{
    [Fact]
    public void FollowingContractRequiresGenerationOnBoundInteractions()
    {
        Assert.NotNull(typeof(IComposer).GetMethod("Follow", [typeof(string)]));
        Assert.NotNull(typeof(IComposer).GetMethod("Edit", [typeof(string), typeof(long), typeof(string)]));
        Assert.NotNull(typeof(IComposer).GetMethod("Submit", [typeof(Guid), typeof(string), typeof(long), typeof(string)]));
        Assert.NotNull(typeof(IComposer).GetMethod("BeginVoice", [typeof(Guid), typeof(string), typeof(long)]));
        Assert.NotNull(typeof(ComposerState).GetProperty("FollowedChat"));
        Assert.NotNull(typeof(ComposerSubmission).GetProperty("Generation"));
    }
    [Trait("host", "own")]
    [Fact]
    public async Task AuthoritativeSelectionRejectsDelayedABAInteractionsAndPreservesOldSubmission()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(services => services.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/a/apps/chat";
        var chat = brain.Get<IChat>(key + "/transcript");
        var composer = brain.Get<IComposer>(key + "/composer");
        var a = key + "/agent-a";
        var b = key + "/agent-b";
        var first = await chat.SelectAgent(a, 0, Guid.NewGuid());
        await composer.Follow(key + "/transcript");
        await composer.Edit(a, first.Revision, "old draft");
        var request = Guid.NewGuid();
        await composer.Submit(request, a, first.Revision, "old draft");
        await composer.BeginVoice(Guid.NewGuid(), a, first.Revision);
        var second = await chat.SelectAgent(b, first.Revision, Guid.NewGuid());
        var third = await chat.SelectAgent(a, second.Revision, Guid.NewGuid());
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Edit(a, first.Revision, "late"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Submit(Guid.NewGuid(), a, first.Revision, "late"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.BeginVoice(Guid.NewGuid(), a, first.Revision));
        await composer.Edit(a, third.Revision, "new draft");
        Assert.Equal(request, await composer.Submit(request, a, first.Revision, "old draft"));
        await composer.Settle(request);
        var current = await composer.Read();
        Assert.Equal("new draft", current.Draft);
        Assert.Null(current.Voice);
        Assert.Equal(first.Revision, Assert.Single(current.Submissions).Generation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Configure(a, third.Revision));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Edit(a, "legacy"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Submit(Guid.NewGuid(), a, "new draft"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.BeginVoice(Guid.NewGuid(), a));
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Follow(key + "/another"));
        await brain.DeactivateAsync(composer, ct);
        await composer.Follow(key + "/transcript");
        Assert.Equal("new draft", (await composer.Read()).Draft);
    }
    [Trait("host", "own")]
    [Fact]
    public async Task MigrationAndSameSourceUpdatesPreserveDraftAudioAndPendingInput()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>()
            .ConfigureServices(services => services.AddSingleton<IChatAgentSource, TestChatAgentSource>()).StartAsync(ct);
        const string key = "owner/a/apps/chat";
        var source = key + "/agent";
        var composerKey = key + "/composer";
        var chatKey = key + "/transcript";
        var composer = brain.Get<IComposer>(composerKey);
        var chat = brain.Get<IChat>(chatKey);
        await composer.Configure(source, 7);
        await composer.Edit(source, "pending draft");
        var submission = (await composer.Submit(Guid.NewGuid(), source, "pending draft"))!.Value;
        var capture = Guid.NewGuid();
        await composer.BeginVoice(capture, source);
        await composer.CaptureVoice(capture, [1, 2, 3], "audio/wav");
        var grains = brain.Services.GetRequiredService<IGrainFactory>();
        await grains.GetGrain<IChatMigration>(chatKey).AdoptAgent(source, 9, Guid.NewGuid());
        await grains.GetGrain<IComposerMigration>(composerKey).AdoptFollow(chatKey);
        var adopted = await composer.Read();
        Assert.Equal("pending draft", adopted.Draft);
        Assert.Equal(9, adopted.Generation);
        Assert.Equal(capture, adopted.Voice!.Id);
        Assert.Equal([1, 2, 3], (await composer.ReadAudio(capture))!.Bytes);
        Assert.Equal(submission, Assert.Single(adopted.Submissions).Id);
        await using var changed = await brain.Observe<ChatChanged>(chat, ct);
        await brain.Get<IText>(source).Set("streamed token");
        await changed.NextAsync(ct: ct);
        // Reconciliation is also idempotent when an ordinary transcript update occurred.
        await composer.Follow(chatKey);
        var unchanged = await composer.Read();
        Assert.Equal(adopted.Revision, unchanged.Revision);
        Assert.Equal(adopted.Draft, unchanged.Draft);
        Assert.Equal(adopted.Voice, unchanged.Voice);
        await brain.DeactivateAsync(composer, ct);
        await composer.Follow(chatKey);
        Assert.Equal("pending draft", (await composer.Read()).Draft);
        Assert.Equal([1, 2, 3], (await composer.ReadAudio(capture))!.Bytes);
    }
    [Trait("host", "own")]
    [Fact]
    public async Task StandaloneLegacyReplayReturnsItsOriginalSubmissionAcrossConfigurationChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        await composer.Edit("a", "original");
        var request = Guid.NewGuid();
        await composer.Submit(request, "a", "original");
        await composer.Configure("b", 2);
        await composer.Configure("a", 3);
        Assert.Equal(request, await composer.Submit(request, "a", "original"));
        Assert.Equal(1, Assert.Single((await composer.Read()).Submissions).Generation);
    }
}
