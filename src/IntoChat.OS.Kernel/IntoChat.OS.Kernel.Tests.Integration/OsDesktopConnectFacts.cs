using IntoChat.OS.Apps;
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

        var root = await brain.Services.GetRequiredService<IntoChat.OS.Flutter.IUiRootProvider>().Connect(ct);

        Assert.Equal(new IntoChat.OS.Flutter.UiChildRef("view", IApp.Key(person.BrainId, "desktop") + "/view"), root);
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(person.BrainId, "desktop")).Read()).Status);
    }

    [Fact]
    public async Task SwitchingLayoutsChangesTheNextRootAndKeepsOpenViews()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var provider = brain.Services.GetRequiredService<IntoChat.OS.Flutter.IUiRootProvider>();
        await provider.Connect(ct);
        var workspace = brain.Get<IntoChat.OS.Flutter.Workspace.IWorkspace>(person.BrainId);
        var before = await workspace.Read();
        using (person.AsApp(IApp.Key(person.BrainId, "desktop")))
        { await workspace.Apply(new([new("ino", IApp.Key(person.BrainId, "ino"), "full", "")], "ino", []), "test:open", before.Revision); }
        // layout-switch.cs writes the desktop's own layout text; the shell then connects again.
        var layout = brain.Get<IntoChat.OS.Flutter.Text.IText>(IApp.Key(person.BrainId, "desktop") + "/layout");

        await layout.Set("classic");
        Assert.Equal(new IntoChat.OS.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), await provider.Connect(ct));
        Assert.Equal(["ino"], (await workspace.Read()).OpenViews.Select(view => view.Id));

        await layout.Set("desktop");
        Assert.Equal(new IntoChat.OS.Flutter.UiChildRef("view", IApp.Key(person.BrainId, "desktop") + "/view"), await provider.Connect(ct));
        Assert.Equal(["ino"], (await workspace.Read()).OpenViews.Select(view => view.Id));
    }

    private static Task<ITestBrain> Boot(CancellationToken ct) => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
        .ConfigureServices(services =>
        {
            services.RemoveAll<IAppRuntime>();
            services.AddSingleton<IAppRuntime, IdleRuntime>();
            services.AddOperatingSystemApps();
        })
        .StartAsync(ct);

    private sealed class IdleRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
