using IntoChat.OS.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Kernel.Tests.Integration;

// v2: connecting starts the desktop with one `start` request; Layout=classic keeps the v1 Workspace root.
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
    public async Task ClassicLayoutKeepsTheWorkspaceRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await Boot(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        await brain.Get<IApp>(IApp.Key(person.BrainId, "desktop")).Configure(new() { ["Layout"] = "classic" }, []);

        var root = await brain.Services.GetRequiredService<IntoChat.OS.Flutter.IUiRootProvider>().Connect(ct);

        Assert.Equal(new IntoChat.OS.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), root);
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
