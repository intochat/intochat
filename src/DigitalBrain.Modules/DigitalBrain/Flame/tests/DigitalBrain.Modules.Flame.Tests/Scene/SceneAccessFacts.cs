using DigitalBrain.Flame;
using DigitalBrain.Flame.Scene;
using DigitalBrain.Framework;
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
    public async Task TheOwningAppCanSetAndTheShellCanReportAndFire()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        using (brain.Owner.AsApp("owner/apps/ino")) { await scene.Set(Scenes.Ino(), 0); }
        using (brain.Owner.AsCaller())
        {
            await scene.Report("sphere", "tap", 0.5, 0.5, null);
            await scene.Fire("think");
        }
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);
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
        using (As(CallerKind.Scheduler, TrustedEdge.Scheduler))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Fire("think"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => scene.Set(Scenes.Ino(), 1));
        }
    }

    [Fact]
    public async Task AnAssistantOfTheSameBrainMayFire()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlameModule>().StartAsync(ct);
        var scene = brain.Get<IScene>(Key);
        await scene.Set(Scenes.Ino(), 0);
        using (As(CallerKind.Assistant, TrustedEdge.AuthenticatedHttp)) { await scene.Fire("think"); }
        Assert.Equal("thinking", (await scene.Read()).Current["presence"]);
    }

    // Person stamps only User and App callers; other kinds are stamped the way Person does it internally.
    private static Restore As(CallerKind kind, TrustedEdge edge)
    {
        var previous = Orleans.Runtime.RequestContext.Get(CallerContextStamper.RequestContextKey);
        CallerContextStamper.Stamp(new CallerContext { PrincipalId = "owner", AccountId = "owner", BrainId = "owner", Kind = kind, StampedBy = edge });
        return new Restore(previous);
    }

    private sealed class Restore(object? previous) : IDisposable
    {
        public void Dispose()
        {
            if (previous is null) { Orleans.Runtime.RequestContext.Remove(CallerContextStamper.RequestContextKey); }
            else { Orleans.Runtime.RequestContext.Set(CallerContextStamper.RequestContextKey, previous); }
        }
    }
}
