using DigitalBrain.Apps.Signals;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Framework;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Apps.Tests.Apps;

public sealed class AppPresentationFacts
{
    private static Task<ITestBrain> Start() => TestBrain.Create().WithModule<AppsModule>().WithFakeAppRuntime(new()).StartAsync(TestContext.Current.CancellationToken);
    private static async Task<IApp> Create(ITestBrain brain, string name = "files")
    {
        var app = brain.Get<IApp>(IApp.Key(brain.Owner.BrainId, name));
        await app.Create(new("Files", "files", [], [], []), "spec");
        return app;
    }
    private static Presentation Root() => new(new Sphere("icon.svg", ["mood"]));

    [Fact]
    public async Task AnAppCanPublishNamedViewsWithoutASphere()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        var key = IApp.Key(brain.Owner.BrainId, "files");
        using (brain.Owner.AsApp(key))
        {
            var root = new Presentation(Views: [new("full", new("text", key + "/text")), new("compact", new("text", key + "/text"))]);
            var state = (await app.Present(0, root)).Presentation!;
            Assert.Null(state.Root!.Sphere);
            Assert.Equal(2, state.Root.Views!.Length);
            Assert.Equal(state.Revision, (await app.Present(0, root)).Presentation!.Revision);
        }
        await app.WriteBehavior("shared.cs", "public static class Shared { }", Guid.NewGuid());
        Assert.Null((await app.Read()).Presentation);
    }

    [Fact]
    public async Task NamedViewsRequireUniqueIdsAndLocalRoots()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        var key = IApp.Key(brain.Owner.BrainId, "files");
        using (brain.Owner.AsApp(key))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => app.Present(0,
                new(Views: [new("full", new("text", key + "/a")), new("full", new("text", key + "/b"))])));
            await Assert.ThrowsAsync<ArgumentException>(() => app.Present(0,
                new(Views: [new("full", new("text", "another/brain/apps/private/text"))])));
            await Assert.ThrowsAsync<ArgumentException>(() => app.Present(0, new Presentation()));
            await Assert.ThrowsAsync<ArgumentNullException>(() => app.Present(0, new(new Sphere("icon.svg", null!))));
        }
    }

    [Fact]
    public async Task TheExecutingAssignmentPublishesAndAStaleOneIsRefused()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        var key = IApp.Key(brain.Owner.BrainId, "files");
        var revision = (await app.Read()).ProgramRevision;
        using var signals = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<PresentationChanged>();
        using (brain.Owner.AsApp(key))
        {
            var state = await app.Present(revision, Root());
            Assert.NotNull(state.Presentation!.Root);
            Assert.Equal(1, state.Presentation.Revision);
            Assert.Equal(key, (await signals.Reader.ReadAsync(TestContext.Current.CancellationToken)).App);
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.Present(revision - 1, Root()));
        }
        await app.WriteBehavior("shared.cs", "public static class Shared { }", Guid.NewGuid());
        Assert.Null((await app.Read()).Presentation);
        Assert.True((await app.Read()).ProgramRevision > revision);
        using (brain.Owner.AsApp(key)) { await Assert.ThrowsAsync<InvalidOperationException>(() => app.Present(revision, Root())); }
    }

    [Fact]
    public async Task AnotherAppOrAPersonCannotPublish()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using (brain.Owner.AsCaller()) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Present(0, Root())); }
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "other"))) { await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Present(0, Root())); }
    }

    [Fact]
    public async Task IdenticalPublicationIsIdempotent()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using var signals = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<PresentationChanged>();
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        {
            await app.Present(0, Root());
            Assert.Equal(1, (await app.Present(0, Root())).Presentation!.Revision);
        }
        Assert.True(signals.Reader.TryRead(out _));
        Assert.False(signals.Reader.TryRead(out _));
    }

    [Fact]
    public async Task ASlotOutsideTheBrainIsRefused()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await Assert.ThrowsAsync<ArgumentException>(() => app.Present(0, Root() with { Main = new("text", "other/b/apps/x/text") })); }
    }

    [Fact]
    public async Task AnInvalidTreeIsRefusedAtPublish()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await Assert.ThrowsAsync<ArgumentException>(() => app.Present(0, new(new Sphere("icon.svg", ["mood"], new(TreeNode.Condition("unread", "1")))))); }
    }

    [Fact]
    public async Task ScriptsWriteDeclaredKeysAndReservedKeysAreStamped()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        {
            await app.Present(0, Root());
            Assert.Equal("thinking", (await app.WriteBlackboard(0, new() { ["mood"] = "thinking" })).Presentation!.Blackboard["mood"]);
            await Assert.ThrowsAsync<ArgumentException>(() => app.WriteBlackboard(0, new() { ["running"] = "true" }));
            await Assert.ThrowsAsync<ArgumentException>(() => app.WriteBlackboard(0, new() { ["unread"] = "1" }));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => app.Stamp("running", "true"));
        }
        await app.Stamp("running", "true");
        Assert.Equal("true", (await app.Read()).Presentation!.Blackboard["running"]);
    }

    [Fact]
    public async Task FailurePresentsAsError()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        {
            var state = (await app.FailPresentation(0, "boom")).Presentation!;
            Assert.Null(state.Root);
            Assert.Equal("boom", state.Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProgressCommitsBothReservedFlagsOnceAndRepeatedDetailsDoNotChangePresentation(bool published)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Start();
        var app = await Create(brain);
        if (published)
        {
            using var caller = brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files"));
            await app.Present(0, Root());
            await app.WriteBlackboard(0, new() { ["mood"] = "custom" });
        }
        var revision = (await app.Read()).Presentation?.Revision ?? 0;
        using var changes = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<PresentationChanged>();
        using var activities = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<AppActivity>();
        await app.Progress("working", "first detail");
        var first = (await app.Read()).Presentation;
        Assert.NotNull(first);
        Assert.Equal("true", first.Blackboard["running"]);
        Assert.Equal("false", first.Blackboard["attention"]);
        Assert.Equal(revision + 1, first.Revision);
        Assert.True(changes.Reader.TryRead(out var initial));
        Assert.Equal(first.Revision, initial.Revision);
        Assert.False(changes.Reader.TryRead(out _));

        await app.Progress("reading", "different detail");
        Assert.Equal(first.Revision, (await app.Read()).Presentation!.Revision);
        Assert.False(changes.Reader.TryRead(out _));
        Assert.True(activities.Reader.TryRead(out var working));
        Assert.Equal("first detail", working.Detail);
        Assert.True(activities.Reader.TryRead(out var reading));
        Assert.Equal("different detail", reading.Detail);

        await app.Progress("attention", "needs input");
        var last = (await app.Read()).Presentation!;
        Assert.Equal("false", last.Blackboard["running"]);
        Assert.Equal("true", last.Blackboard["attention"]);
        Assert.Equal(first.Revision + 1, last.Revision);
        Assert.True(changes.Reader.TryRead(out var attention));
        Assert.Equal(last.Revision, attention.Revision);
        Assert.False(changes.Reader.TryRead(out _));
        if (published) { Assert.Equal("custom", last.Blackboard["mood"]); }
        await brain.DeactivateAsync(app, ct);
        var restored = (await app.Read()).Presentation!;
        Assert.Equal(last.Revision, restored.Revision);
        Assert.Equal("false", restored.Blackboard["running"]);
        Assert.Equal("true", restored.Blackboard["attention"]);
    }

    [Fact]
    public async Task ProgressBeforeTheSphereExistsKeepsTheActivityAndDropsUndeclaredMood()
    {
        await using var brain = await Start();
        var app = await Create(brain);
        using var activities = brain.Services.GetRequiredService<LocalSignalHub>().Subscribe<AppActivity>();
        await app.Progress("listening", "heard", new() { ["mood"] = "listening" });
        var early = (await app.Read()).Presentation!;
        Assert.Equal("true", early.Blackboard["running"]);
        Assert.False(early.Blackboard.ContainsKey("mood"));
        Assert.True(activities.Reader.TryRead(out var heard));
        Assert.Equal("heard", heard.Detail);

        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await app.Present(0, new(new Sphere("icon.svg", []))); }
        await Assert.ThrowsAsync<ArgumentException>(() => app.Progress("working", "later", new() { ["mood"] = "thinking" }));

        using (brain.Owner.AsApp(IApp.Key(brain.Owner.BrainId, "files")))
        { await app.Present(0, Root()); }
        await app.Progress("working", "later", new() { ["mood"] = "thinking" });
        Assert.Equal("thinking", (await app.Read()).Presentation!.Blackboard["mood"]);
    }
}
