using IntoChat.OS.Modules.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Tests.Integration;

// v2: connecting starts the desktop with one `start` request; the desktop's layout text "classic" keeps the v1 Workspace root.
public sealed class OsDesktopConnectFacts
{
    [Fact]
    public async Task ConnectingStartsTheDesktopAndReturnsItsView()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();

        var root = await brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>().Connect(ct);

        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("view", IApp.Key(person.BrainId, "desktop") + "/view"), root);
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(person.BrainId, "desktop")).Read()).Status);
    }

    [Fact]
    public async Task SwitchingLayoutsChangesTheNextRootAndKeepsOpenViews()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var workspace = brain.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(person.BrainId);
        var before = await workspace.Read();
        using (person.AsApp(IApp.Key(person.BrainId, "desktop")))
        { await workspace.Apply(new([new("ino", IApp.Key(person.BrainId, "ino"), "full", "")], "ino", []), "test:open", before.Revision); }
        // layout-switch.cs writes the desktop's own layout text; the shell then connects again.
        var layout = brain.Get<IntoChat.OS.Modules.Flutter.Text.IText>(IApp.Key(person.BrainId, "desktop") + "/layout");

        await layout.Set("classic");
        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), await provider.Connect(ct));
        Assert.Equal(["ino"], (await workspace.Read()).OpenViews.Select(view => view.Id));

        await layout.Set("desktop");
        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("view", IApp.Key(person.BrainId, "desktop") + "/view"), await provider.Connect(ct));
        Assert.Equal(["ino"], (await workspace.Read()).OpenViews.Select(view => view.Id));
    }

    [Fact]
    public async Task AForeignAppNamedDesktopGetsTheClassicRootAndNoWorkspaceAuthority()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var desktop = brain.Get<IApp>(IApp.Key(person.BrainId, "desktop"));
        // The app named desktop is someone else's program, not the one the OS ships.
        await desktop.RecordOrigin(new AppOrigin("mallory", new AppSource.Brain("account-mallory", "mallory"), "desktop", "9.0.0"));

        var root = await provider.Connect(ct);

        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), root);
        Assert.Equal("mallory", (await desktop.Read()).Origin!.Publisher);
        var workspace = brain.Get<IntoChat.OS.Modules.Flutter.Workspace.IWorkspace>(person.BrainId);
        var revision = (await workspace.Read()).Revision;
        using (person.AsApp(IApp.Key(person.BrainId, "desktop")))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.Apply(new([], null, []), "foreign", revision));
        }
    }

    [Fact]
    public async Task AnEditedDesktopGetsTheClassicRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var desktop = brain.Get<IApp>(IApp.Key(person.BrainId, "desktop"));
        var snapshot = await desktop.Read();
        await desktop.SaveManifest(snapshot.Manifest with { Description = "edited" }, Guid.NewGuid());

        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), await provider.Connect(ct));
    }

    [Fact]
    public async Task AnOlderDesktopUpgradesToTheShippedVersionOnConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var desktop = brain.Get<IApp>(IApp.Key(person.BrainId, "desktop"));
        var installed = (await desktop.Read()).Origin!;
        Assert.True(AppVersions.Compare(installed.Version, "0.1.3") > 0);
        await desktop.RecordOrigin(installed with { Version = "0.1.3" });

        Assert.Equal(new IntoChat.OS.Modules.Flutter.UiChildRef("view", IApp.Key(person.BrainId, "desktop") + "/view"), await provider.Connect(ct));
        Assert.Equal(installed.Version, (await desktop.Read()).Origin!.Version);
    }

    // The connect-time updater never widens: a shipped app whose next release asks for more than the person accepted
    // keeps its installed version until the person consents in Apps.
    [Fact]
    public async Task ConnectTimeUpdaterNeverWidens()
    {
        var ct = TestContext.Current.CancellationToken;
        // Files requires storage@1; this brain is treated as having a storage connection.
        await using var brain = await Boot(ct, new StorageConnected());
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Modules.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var installer = brain.Services.GetRequiredService<AppInstaller>();
        var asked = await installer.Install(new(new("os", "files"), AppConsent.Of(null), Guid.Empty), ct);
        Assert.Equal(InstallStatus.NeedsConsent, asked.Status);
        Assert.Equal(InstallStatus.Ok, (await installer.Install(new(new("os", "files"), AppConsent.Of(asked.Requires), Guid.Empty), ct)).Status);
        var files = brain.Get<IApp>(IApp.Key(person.BrainId, "files"));
        var installed = (await files.Read()).Origin!;
        Assert.NotEmpty(installed.Consented!.Capabilities!);
        // An older copy that was accepted asking for nothing: the shipped release now asks for connections.
        await files.RecordOrigin(installed with { Version = "0.0.1", Consented = new() });

        await provider.Connect(ct);

        Assert.Equal("0.0.1", (await files.Read()).Origin!.Version);
        Assert.Equal(InstallStatus.NeedsConsent, (await installer.Update(new("files"), ct)).Status);
    }

    private static Task<ITestBrain> Boot(CancellationToken ct, ICapabilitySatisfaction? capabilities = null) => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
        // The desktop's layout text is a plain Flutter neuron (06-04): hosted only where the module is.
        .WithModule<IntoChat.OS.Modules.Flutter.FlutterModule>()
        .ConfigureServices(services =>
        {
            services.RemoveAll<IAppRuntime>();
            services.AddSingleton<IAppRuntime, IdleRuntime>();
            if (capabilities is not null) { services.AddSingleton(capabilities); }
            services.AddOperatingSystemApps();
        })
        .StartAsync(ct);

    private sealed class StorageConnected : ICapabilitySatisfaction
    {
        public Task<bool> Satisfied(string brain, string capability, CancellationToken ct) => Task.FromResult(capability == "storage@1");
    }

    private sealed class IdleRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
