using IntoChat.OS.Modules.Apps.Signals;
using IntoChat.OS.Modules.Apps.Tests.Integration.Runtimes;
using IntoChat.OS.Testing;
using Microsoft.Extensions.DependencyInjection;
using DigitalBrain.Runtime.Orleans.Hosting;

namespace IntoChat.OS.Modules.Apps.Tests.Apps;

public sealed class AppMigrationFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Trait("host", "own")]
    [Fact]
    public async Task PausingAndAdoptingKeepsTheOriginalStateAndDoesNotRunEitherProgram()
    {
        var runtime = new FakeAppRuntime();
        await using var brain = await FakeAppRuntime.Start(runtime, Ct);
        var person = await brain.PersonAsync("migration", Ct);
        using var caller = person.AsCaller();
        var sourceKey = IApp.Key(person.BrainId, "assistant");
        var targetKey = IApp.Key(person.BrainId, "ino");
        var source = brain.Get<IApp>(sourceKey);
        var target = brain.Get<IApp>(targetKey);
        await source.Create(new("Assistant", "old", [new("send", "send")], [], []), "custom spec");
        await source.WriteFile("prompts/system.md", "my instructions", Guid.NewGuid());
        await source.Configure(new() { ["Model"] = "mine" }, new() { ["database"] = "mine" });
        await source.WriteBehavior("custom.cs", "public static class Custom { }", Guid.NewGuid());
        await target.Create(new("Ino", "new", [new("send", "send")], [], []), "ino spec");
        var grains = brain.Services;
        var legacy = SiloNeurons.Get<IAppMigration>(brain.Services, sourceKey);
        var migrated = SiloNeurons.Get<IAppMigration>(brain.Services, targetKey);
        await legacy.Pause("ino-v3");
        await migrated.Pause("ino-v3");
        var before = runtime.Deployed.Count;
        await brain.DeactivateAsync(source, Ct);
        await brain.DeactivateAsync(target, Ct);
        await source.Read();
        await source.CurrentBuildAttempt();
        await migrated.Adopt("ino-v3", sourceKey);
        await migrated.Adopt("ino-v3", sourceKey);
        Assert.Equal(before, runtime.Deployed.Count);
        Assert.Equal("mine", (await target.Read()).Settings["Model"]);
        Assert.Equal(sourceKey, (await target.Read()).Settings["ChatDataApp"]);
        Assert.Equal("my instructions", (await legacy.Archive("ino-v3")).Files["prompts/system.md"]);
        Assert.Equal("custom spec", (await source.Read()).Spec);
        Assert.Contains(sourceKey, runtime.Retired);
        await SiloNeurons.Get<IAppDeploy>(brain.Services, sourceKey).Restore();
        Assert.Equal(before, runtime.Deployed.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.WriteBehavior("late.cs", "public static class Late { }", Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(source.Delete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.Configure(new() { ["Model"] = "late" }, []));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.WriteFile("prompts/system.md", "late", Guid.NewGuid()));
    }

    [Trait("host", "own")]
    [Fact]
    public async Task AliasDeliveryRetainsTheOriginalRequestIdAndKeepsPendingLegacyRequests()
    {
        await using var brain = await FakeAppRuntime.Start(new(), Ct);
        var person = await brain.PersonAsync("alias", Ct);
        using var caller = person.AsCaller();
        var sourceKey = IApp.Key(person.BrainId, "assistant");
        var targetKey = IApp.Key(person.BrainId, "ino");
        var source = brain.Get<IApp>(sourceKey);
        var target = brain.Get<IApp>(targetKey);
        var manifest = new AppManifest("Chat", "chat", [new("send", "send")], [], []);
        await source.Create(manifest, "source");
        await target.Create(manifest, "target");
        var oldId = await source.Request("send", "already handled");
        var grains = brain.Services;
        var legacy = SiloNeurons.Get<IAppMigration>(brain.Services, sourceKey);
        await SiloNeurons.Get<IAppMigration>(brain.Services, targetKey).Pause("ino-v3");
        await legacy.Pause("ino-v3");
        await legacy.Redirect("ino-v3", targetKey);
        var id = await source.Request("send", "new message");
        var retained = await SiloNeurons.Get<IAppMigration>(brain.Services, targetKey).RetainedRequests();
        var requests = retained.Select(item => item.Request).ToArray();
        Assert.Contains(requests, request => request.RequestId == oldId);
        var request = Assert.Single(requests, request => request.RequestId == id);
        Assert.Equal(id, request.RequestId);
        Assert.NotEqual(oldId, request.RequestId);
        Assert.Equal("new message", request.Input);
        Assert.Equal("source", (await legacy.Archive("ino-v3")).Spec);
    }
}
