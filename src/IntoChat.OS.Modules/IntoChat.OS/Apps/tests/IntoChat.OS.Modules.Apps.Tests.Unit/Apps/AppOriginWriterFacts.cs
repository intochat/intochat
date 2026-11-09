using DigitalBrain;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using DigitalBrain.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Apps;

// ShippedApps.Is trusts Origin and Edited: an app-kind caller can never set either through Import or Replace.
public sealed class AppOriginWriterFacts
{
    private static readonly AppOrigin Shipped = new(ShippedApps.Publisher, new AppSource.Shipped(), "notes", "1.0.0");

    private static AppBundle Bundle() => new("notes", "1.0.0", new AppManifest("Notes", "Keep notes", [], [], []), "spec", [], "Missing.Notes, Missing");

    [Theory]
    [InlineData("import")]
    [InlineData("replace")]
    public async Task AnAppCallerCannotClaimAShippedOrigin(string call)
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        await using var os = TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
        var app = os.Get<IApp>(new NeuronId(new BrainId("owner"), IntoChat.OS.Connections.AppGrainType.Name, "apps/notes"));
        if (call == "replace")
        {
            try { await app.Import(Bundle()); }
            catch (Exception error) when (error is not UnauthorizedAccessException) { /* no program runtime here; the record lands */ }
        }

        using (RuntimeCallerContext.Stamp(new CapabilityCaller("app:owner/apps/notes", "a1", "owner", "owner/apps/notes")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => call == "import" ? app.Import(Bundle(), Shipped) : app.Replace(Bundle(), Shipped));
        }

        var read = await app.Read();
        Assert.Null(read.Origin);
    }
}
