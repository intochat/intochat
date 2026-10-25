using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Chat;
using IntoChat.OS.Modules.Flutter.Text;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class OsInoMigrationFacts
{
    [Theory]
    [InlineData("none")]
    [InlineData("retire")]
    [InlineData("deploy")]
    [InlineData("transition")]
    public async Task MigrationAndItsRetryPreserveLegacyDraftAudioReadReceiptsPromptsAndRequests(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new IdleRuntime();
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
                services.AddSingleton<IChatAgentSource, QuietSource>();
                services.AddSingleton<IFeatureMigration, OsInoMigration>();
            }).StartAsync(ct);
        var person = await brain.PersonAsync("legacy", ct);
        using var caller = person.AsCaller();
        var legacyKey = IApp.Key(person.BrainId, "assistant");
        var inoKey = IApp.Key(person.BrainId, "ino");
        var selected = legacyKey + "/conversations/kept";
        var manifest = new AppManifest("Assistant", "chat", [new("send", "send"), new("new", "new"), new("new-conversation", "new"), new("voice", "voice")], [], []);
        var legacy = brain.Get<IApp>(legacyKey);
        await legacy.Import(new("assistant", "2.0.0", manifest, "custom spec", new() { ["prompts/system.md"] = "my prompt", ["custom.cs"] = "public static class Mine { }" }),
            new("os", new AppSource.Shipped(), "assistant", "2.0.0"));
        await legacy.Configure(new() { ["Model"] = "personal-model" }, []);
        var ino = brain.Get<IApp>(inoKey);
        await ino.Import(new("ino", "2.0.0", manifest, "old ino", []), new("os", new AppSource.Shipped(), "ino", "2.0.0"));
        await brain.Get<IText>(legacyKey + "/conversation").Set(failure == "transition" ? "future-pointer" : "kept");
        var chat = brain.Get<IChat>(legacyKey + "/transcript");
        var projected = failure == "transition" ? legacyKey + "/conversations/older-projection" : selected;
        await chat.Project(new(projected, 7, 1, [new("old-message", ChatRole.Assistant, "retained answer")]));
        await chat.MarkRead(projected, 7, 1, ["old-message"]);
        var composer = brain.Get<IComposer>(legacyKey + "/composer");
        await composer.Configure(selected, 7);
        var submission = Guid.NewGuid();
        await composer.Edit(selected, "pending draft");
        await composer.Submit(submission, selected, "pending draft");
        await composer.Edit(selected, "my draft");
        var capture = Guid.NewGuid();
        await composer.BeginVoice(capture, selected);
        await composer.CaptureVoice(capture, [1, 2, 3], "audio/wav");
        var requestId = await legacy.Request("send", "pending message");
        var oldHandled = await legacy.Request("new", "");
        var forwardedHandled = await ino.Request("new-conversation", "");
        var oldLedger = string.Join('\n', Enumerable.Range(0, 255).Select(_ => Guid.NewGuid()).Append(oldHandled));
        var inoLedger = string.Join('\n', Enumerable.Range(0, 255).Select(_ => Guid.NewGuid()).Append(forwardedHandled));
        await brain.Get<IText>(legacyKey + "/handled/new").Set(oldLedger);
        await brain.Get<IText>(inoKey + "/handled/new-conversation").Set(inoLedger);
        var bundle = new AppBundle("ino", "3.0.0", manifest with { Title = "Ino" }, "new ino", new()
        { ["prompts/system.md"] = "default prompt", ["NewProgram.cs"] = "public static class NewProgram { }" });
        var loader = brain.Services.GetRequiredService<FeatureLoader>();
        runtime.Failure = failure == "transition" ? "none" : failure;
        if (failure is "retire" or "deploy")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => loader.Load(person.BrainId, "ino", bundle,
                new("os", new AppSource.Shipped(), "ino", "3.0.0"), ct));
            Assert.Equal("my draft", (await composer.Read()).Draft);
            Assert.Contains((await composer.Read()).Submissions, pending => pending.Id == submission && pending.Context == selected && pending.Text == "pending draft");
            Assert.Equal("my prompt", (await legacy.Read()).Files["prompts/system.md"]);
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await loader.Load(person.BrainId, "ino", bundle, new("os", new AppSource.Shipped(), "ino", "3.0.0"), ct);
            var app = await ino.Read();
            Assert.Equal("my prompt", app.Files["prompts/system.md"]);
            Assert.Equal("personal-model", app.Settings["Model"]);
            Assert.Equal(legacyKey, app.Settings["ChatDataApp"]);
            Assert.Equal("my draft", (await composer.Read()).Draft);
            Assert.Equal(new byte[] { 1, 2, 3 }, (await composer.ReadAudio(capture))!.Bytes);
            Assert.Equal(selected, (await chat.Read()).Selection!.Agent);
            var transcript = await chat.Read();
            if (failure == "transition") { Assert.Equal(8, transcript.Selection!.Revision); }
            else
            {
                Assert.Single(transcript.Projection!.Messages);
                Assert.Empty(transcript.UnreadMessageIds);
            }
            Assert.Equal(inoKey, (await legacy.Read()).MigrationTarget);
            var retained = await ino.AsReference<IRetainsSignals>().Retained(nameof(IntoChat.OS.Modules.Apps.Signals.AppRequested), 0);
            Assert.Single(retained, item => item.Signal is IntoChat.OS.Modules.Apps.Signals.AppRequested request && request.RequestId == requestId);
            var merged = (await brain.Get<IText>(legacyKey + "/handled/new").Read()).Markdown.Split('\n').TakeLast(256).ToArray();
            Assert.Contains(oldHandled.ToString(), merged);
            Assert.Contains(forwardedHandled.ToString(), merged);
        }
        var archived = await brain.Get<IApp>(legacyKey).AsReference<IAppMigration>().Archive("ino-v3");
        Assert.Equal("public static class Mine { }", archived.Files["custom.cs"]);
    }

    private sealed class IdleRuntime : IAppRuntime
    {
        public string Failure { get; set; } = "none";
        public Func<Task>? NextDeploy { get; set; }
        public async Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
        {
            if (NextDeploy is { } action) { NextDeploy = null; await action(); }
            Fail("deploy");
            return new Dictionary<string, string>();
        }
        public Task Retire(string appKey, CancellationToken cancellationToken) { Fail("retire"); return Task.CompletedTask; }
        private void Fail(string stage)
        {
            if (Failure != stage) { return; }
            Failure = "none";
            throw new InvalidOperationException("Lost " + stage + " acknowledgement.");
        }
    }

    [Fact]
    public async Task RetryingAfterANewProgramSelectedAnotherConversationKeepsThatSelection()
    {
        var ct = TestContext.Current.CancellationToken;
        var runtime = new IdleRuntime();
        await using var brain = await TestBrain.Create().WithModule<AppsModule>().WithModule<FlutterModule>()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime>(runtime);
                services.AddSingleton<IChatAgentSource, QuietSource>();
                services.AddSingleton<IFeatureMigration, OsInoMigration>();
            }).StartAsync(ct);
        var person = await brain.PersonAsync("advanced", ct);
        using var caller = person.AsCaller();
        var legacyKey = IApp.Key(person.BrainId, "assistant");
        var inoKey = IApp.Key(person.BrainId, "ino");
        var manifest = new AppManifest("Chat", "chat", [new("new", "new")], [], []);
        await brain.Get<IApp>(legacyKey).Import(new("assistant", "2.0.0", manifest, "old", []),
            new("os", new AppSource.Shipped(), "assistant", "2.0.0"));
        var composer = brain.Get<IComposer>(legacyKey + "/composer");
        await composer.Configure(legacyKey + "/conversations/main", 3);
        var chat = brain.Get<IChat>(legacyKey + "/transcript");
        var newer = legacyKey + "/conversations/newer";
        runtime.NextDeploy = async () =>
        {
            var selection = (await chat.Read()).Selection!;
            await chat.SelectAgent(newer, selection.Revision, Guid.NewGuid());
            throw new InvalidOperationException("Deployment completed work before its acknowledgement failed.");
        };
        var bundle = new AppBundle("ino", "3.0.0", manifest, "new", new() { ["Program.cs"] = "public static class Program { }" });
        var loader = brain.Services.GetRequiredService<FeatureLoader>();
        var origin = new AppOrigin("os", new AppSource.Shipped(), "ino", "3.0.0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => loader.Load(person.BrainId, "ino", bundle, origin, ct));
        var result = await loader.Load(person.BrainId, "ino", bundle, origin, ct);
        Assert.Equal(AppStatus.Live, result.Status);
        Assert.Equal(newer, (await chat.Read()).Selection!.Agent);
        Assert.Equal(legacyKey + "/transcript", (await composer.Read()).FollowedChat);
    }

    private sealed class QuietSource(IIntoChatOSBrains brains) : IChatAgentSource
    {
        public INeuron Source(string key) => brains.Grain<IText>(key);
        public Task<ChatAgentSnapshot> Read(string key, long selectionRevision)
            => Task.FromResult(new ChatAgentSnapshot(new(key, selectionRevision, 0, []), null));
    }
}
