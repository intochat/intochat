using DigitalBrain;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

public sealed class AppRestoreFacts
{
    // The unit host has no program runtime, so every deploy throws: the restore after activation fails, and the first caller of
    // the reactivated app still gets its answer, with the failure on the activity.
    [Fact]
    public async Task RestoreFailureNeverSurfacesToTheFirstCaller()
    {
        using var person = IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", new BrainId("owner")));
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
        var id = new NeuronId(new BrainId("owner"), IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId("owner"), "notes").Key);
        var app = os.Get<IApp>(id);
        var bundle = new AppBundle("notes", "1.0.0", new AppManifest("Notes", "Keep notes", [], [], []), "spec", [], "Missing.Notes, Missing");
        try { await app.Import(bundle); }
        catch (Exception) { /* the first deploy fails here too; the record is what the reactivation restores */ }

        await os.Deactivate(id);
        var read = await app.Read();

        Assert.Equal("Notes", read.Manifest.Title);
        await os.Idle();
        // The restore is a one-way turn of its own: it lands after the first call, not inside it.
        var after = read;
        for (var attempt = 0; attempt < 100 && !after.Activity.Any(line => line.StartsWith("restore failed:", StringComparison.Ordinal)); attempt++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            after = await app.Read();
        }
        Assert.Contains(after.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }
}
