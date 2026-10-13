using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using Xunit;

namespace DigitalBrain.Modules.Flame.Tests.Scene;

public sealed class SceneAccessFacts
{
    private const string Key = "owner/apps/ino/scenes/home";

    [Fact]
    public async Task AnotherAppCannotSetAnAppsScene()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        using (brain.Owner.AsApp("owner/apps/files"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Set(Scenes.Ino(), 0));
        }
    }

    [Fact]
    public async Task TheOwningAppCanSetAndTheShellCanReport()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        using (brain.Owner.AsApp("owner/apps/ino")) { await scene.Set(Scenes.Ino(), 0); }
        using (brain.Owner.AsCaller())
        {
            await scene.Report("sphere", "tap", 0.5, 0.5, null);
        }
        Assert.Equal(1, (await scene.Read()).Revision);
    }

    [Fact]
    public async Task AUserOfAnotherBrainIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        using (brain.Owner.AsCallerIn("bob"))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Report("sphere", "tap", 0.5, 0.5, null));
        }
    }

    [Fact]
    public async Task ASchedulerCallerIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        using (brain.Owner.AsScheduler())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Report("sphere", "tap", 0.5, 0.5, null));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Set(Scenes.Ino(), 1));
        }
    }
}
