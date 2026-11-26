using DigitalBrain;
using DigitalBrain.Testing;
using IntoChat.OS.Host.Kernel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

// The compiled-program check on an app record: a copy the host shipped and loaded passes on provenance (its stored type name may be
// stale after the type moved); every other record passes only when its stored type name resolves, as before.
public sealed class CompiledProgramCheckFacts
{
    private const string Stale = "IntoChat.OS.Apps.Gone.GoneApp, IntoChat.OS.Apps";
    private static readonly BrainId Owner = new("owner");

    private static FeatureCatalog Catalog()
        => new([typeof(AppsModule).Assembly, typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly]);

    private static TestOS Os(bool hosted)
    {
        var services = new ServiceCollection();
        services.AddOptions<AppsOptions>();
        services.AddSingleton(TimeProvider.System);
        if (hosted) { services.AddSingleton<IHostedAppTypes>(new CatalogHostedAppTypes(Catalog())); }
        return TestOS.Local(o => o.Services = services.BuildServiceProvider()).Register<AppNeuron>();
    }

    private static IDisposable Person()
        => IntoChat.OS.Tests.TestEdge.Authority.Enter(IntoChat.OS.Tests.TestEdge.Authority.Person("owner", "owner", Owner));

    private static NeuronId IdOf(string name)
        => new(Owner, IntoChat.OS.Host.Primitives.AppGrainType.Name, IntoChat.OS.Host.Primitives.AppRef.Of(Owner, name).Key);

    private static AppOrigin Shipped(string name, string version) => new(ShippedApps.Publisher, new AppSource.Shipped(), name, version);

    private static AppBundle InoWith(string? compiledType)
        => new("ino", "1.0.0", new AppManifest("Ino", "Talk", [], [], []), "", [], compiledType);

    private static async Task<AppSnapshot> Restored(TestOS os, NeuronId id)
    {
        await os.Deactivate(id);
        var app = os.Get<IApp>(id);
        _ = await app.Read();
        await os.Idle();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await os.Idle();
        return await app.Read();
    }

    [Fact]
    public async Task AStaleTypeNameStillRestoresWhenTheHostLoadedTheApp()
    {
        using var person = Person();
        await using var os = Os(hosted: true);
        var id = IdOf("ino");
        var app = os.Get<IApp>(id);

        var imported = await app.Import(InoWith(Stale), Shipped("ino", "1.0.0"));
        Assert.Equal(AppStatus.Live, imported.Status);
        var configured = await app.Configure([], []);
        var restored = await Restored(os, id);

        Assert.Equal(Stale, configured.Settings[AppTrust.CompiledTypeSetting]);
        Assert.Equal(Stale, restored.Settings[AppTrust.CompiledTypeSetting]);
        Assert.DoesNotContain(restored.Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAppTheHostDidNotLoadWithAnUnresolvableNameStillFails()
    {
        using var person = Person();
        await using var os = Os(hosted: true);
        var bundle = new AppBundle("notes", "1.0.0", new AppManifest("Notes", "Keep notes", [], [], []), "", [], Stale);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => os.Get<IApp>(IdOf("notes")).Import(bundle, Shipped("notes", "1.0.0")));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
    }

    [Fact]
    public async Task ANonShippedRecordNamedLikeAHostAppWithAnUnresolvableTypeStillFails()
    {
        using var person = Person();
        await using var os = Os(hosted: true);

        // No origin: an import or a person's own copy that merely carries the shipped app's name.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => os.Get<IApp>(IdOf("ino")).Import(InoWith(Stale)));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
        Assert.Equal(AppStatus.Failed, (await os.Get<IApp>(IdOf("ino")).Read()).Status);
    }

    [Fact]
    public async Task AnEditedShippedCopyWithAnUnresolvableTypeStillFails()
    {
        using var person = Person();
        await using var os = Os(hosted: true);
        var id = IdOf("ino");
        var app = os.Get<IApp>(id);
        await app.Import(InoWith(typeof(IntoChat.OS.Apps.Ino.InoApp).AssemblyQualifiedName), Shipped("ino", "1.0.0"));
        await app.SaveSpec("my own ino", Guid.NewGuid());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.Replace(InoWith(Stale) with { Version = "1.2.0", Spec = "my own ino" }));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
    }

    [Fact]
    public async Task ARecordWithAnotherPublisherStillFollowsTheOldRule()
    {
        using var person = Person();
        await using var os = Os(hosted: true);
        var origin = new AppOrigin("someone-else", new AppSource.Shipped(), "ino", "1.0.0");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => os.Get<IApp>(IdOf("ino")).Import(InoWith(Stale), origin));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
    }

    [Fact]
    public async Task AnOriginNamedDifferentlyFromTheKeyStillFollowsTheOldRule()
    {
        using var person = Person();
        await using var os = Os(hosted: true);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => os.Get<IApp>(IdOf("ino")).Import(InoWith(Stale), Shipped("settings", "1.0.0")));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
    }

    [Fact]
    public async Task AResolvableNameStillPassesWithoutProvenance()
    {
        using var person = Person();
        await using var os = Os(hosted: false);
        var id = IdOf("ino");

        var imported = await os.Get<IApp>(id).Import(InoWith(typeof(IntoChat.OS.Apps.Ino.InoApp).AssemblyQualifiedName));

        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.DoesNotContain((await Restored(os, id)).Activity, line => line.StartsWith("restore failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AStaleNameFailsWithoutProvenanceAsToday()
    {
        using var person = Person();
        await using var os = Os(hosted: false);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => os.Get<IApp>(IdOf("ino")).Import(InoWith(Stale), Shipped("ino", "1.0.0")));
        Assert.Equal($"Compiled app type '{Stale}' could not be resolved.", error.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnAppWithNoCompiledTypeIsUntouched(bool hosted)
    {
        using var person = Person();
        await using var os = Os(hosted);

        var imported = await os.Get<IApp>(IdOf("notes")).Import(
            new AppBundle("notes", "1.0.0", new AppManifest("Notes", "Keep notes", [], [], []), "", []));

        Assert.Equal(AppStatus.Live, imported.Status);
        Assert.False(imported.Settings.ContainsKey(AppTrust.CompiledTypeSetting));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheDynamicBundleBuildersStillVerify(bool hosted)
    {
        using var person = Person();
        await using var os = Os(hosted);
        AppBundle[] bundles =
        [
            IntoChat.OS.Apps.ArtCraft.ArtCraftApp.Bundle,
            IntoChat.OS.Apps.ArtCraft.VectorCraftApp.Bundle,
            IntoChat.OS.Apps.Ino.InoModelSetup.BuildBundle(typeof(IntoChat.OS.Apps.Ino.InoApp)),
        ];
        foreach (var bundle in bundles)
        {
            var imported = await os.Get<IApp>(IdOf(bundle.Name)).Import(bundle, Shipped(bundle.Name, bundle.Version));
            Assert.Equal(AppStatus.Live, imported.Status);
            Assert.Equal(bundle.CompiledType, imported.Settings[AppTrust.CompiledTypeSetting]);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EverythingThatResolvedBeforeStillPasses(bool hosted)
    {
        using var person = Person();
        await using var os = Os(hosted);
        var compiled = CompiledApps.Of(typeof(IntoChat.OS.Apps.Settings.SettingsApp).Assembly)
            .Concat(CompiledApps.Of(typeof(AppsModule).Assembly))
            .Where(app => !string.IsNullOrWhiteSpace(app.Bundle.CompiledType))
            .ToArray();
        Assert.NotEmpty(compiled);
        foreach (var app in compiled)
        {
            var imported = await os.Get<IApp>(IdOf(app.Bundle.Name)).Import(app.Bundle, Shipped(app.Bundle.Name, app.Bundle.Version));
            Assert.True(imported.Status == AppStatus.Live, $"{app.Bundle.Name}: {imported.Status}");
        }
    }

    [Fact]
    public async Task TheStoredSettingIsNeverRewrittenByTheCheck()
    {
        using var person = Person();
        await using var os = Os(hosted: true);
        var id = IdOf("ino");
        var before = await os.Get<IApp>(id).Import(InoWith(Stale), Shipped("ino", "1.0.0"));

        var after = await Restored(os, id);

        Assert.Equal(before.Settings, after.Settings);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Activity, after.Activity);
    }
}
