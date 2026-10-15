using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DigitalBrain.OS.Tests.Unit;

// A new brain is born with the OS defaults, installed as its owner from the features folder.
public sealed class OsBootFacts
{
    [Fact]
    public async Task AFreshBrainHasTheOsAppsInstalledFromTheFeaturesFolder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
            .ConfigureServices(services =>
            {
                // The script runtime is the sandbox seam: the idle one replaces the module's, so installs run no program.
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime, IdleRuntime>();
                services.AddOperatingSystemApps();
            })
            .StartAsync(ct);

        var person = await brain.PersonAsync("ada", ct);

        using var caller = person.AsCaller();
        foreach (var name in new[] { "settings", "ino", "router", "calculator", "datavisualizer", "friends", "circle", "workspace" })
        {
            var installed = await brain.Get<IApp>(IApp.Key(person.BrainId, name)).Read();
            Assert.Equal(AppStatus.Live, installed.Status);
            Assert.Equal("os", installed.Origin!.Publisher);
            Assert.IsType<AppSource.Shipped>(installed.Origin.Source);
        }
        Assert.Empty((await brain.Get<IApp>(IApp.Key(person.BrainId, "assistant")).Read()).Manifest.Title);
        var calculator = brain.Get<IApp>(IApp.Key(person.BrainId, "calculator"));
        await calculator.WriteBehavior("custom.cs", "public static class PersonalCode { }", Guid.NewGuid());
        var before = await calculator.Read();
        var root = await brain.Services.GetRequiredService<DigitalBrain.Flutter.IUiRootProvider>().Connect(ct);
        Assert.Equal(new DigitalBrain.Flutter.UiChildRef("layout", IApp.Key(person.BrainId, "workspace") + "/root"), root);
        Assert.Equal(before.ProgramRevision, (await calculator.Read()).ProgramRevision);
        Assert.True((await calculator.Read()).Edited);
    }

    [Fact]
    public async Task ConnectingAnExistingWorkspaceInstallsItsMissingCircleApp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule, AppsOptions>(options => options.Features = SolutionProjects.Features)
            .ConfigureServices(services =>
            {
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime, IdleRuntime>();
                services.AddOperatingSystemApps(options => options.Apps.Remove(new("os", "circle")));
            })
            .StartAsync(ct);
        using var person = await brain.PersonAsync("ada", ct);
        using var caller = person.AsCaller();
        var circle = brain.Get<IApp>(IApp.Key(person.BrainId, "circle"));
        Assert.Empty((await circle.Read()).Manifest.Title);

        await brain.Services.GetRequiredService<DigitalBrain.Flutter.IUiRootProvider>().Connect(ct);

        var installed = await circle.Read();
        Assert.Equal(AppStatus.Live, installed.Status);
        Assert.Equal("os", installed.Origin!.Publisher);
        Assert.IsType<AppSource.Shipped>(installed.Origin.Source);
    }

    [Fact]
    public async Task ADefaultMissingFromTheCatalogDoesNotKeepTheOthersFromInstalling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule>()
            .ConfigureServices(services =>
            {
                // The script runtime is the sandbox seam: the idle one replaces the module's, so installs run no program.
                services.RemoveAll<IAppRuntime>();
                services.AddSingleton<IAppRuntime, IdleRuntime>();
                services.AddSingleton(new FeatureCatalog(new FeatureDirectory(HelloFeatures())));
                services.AddOperatingSystemBoot(os => { os.Apps.Add(new OsApp("os", "missing")); os.Apps.Add(new OsApp("os", "hello")); });
            })
            .StartAsync(ct);

        var person = await brain.PersonAsync("ada", ct);

        using var caller = person.AsCaller();
        Assert.Equal(AppStatus.Live, (await brain.Get<IApp>(IApp.Key(person.BrainId, "hello")).Read()).Status);
        Assert.Equal(AppStatus.Drafted, (await brain.Get<IApp>(IApp.Key(person.BrainId, "missing")).Read()).Status);
    }

    // A features folder holding os/hello, written once per run.
    private static string HelloFeatures()
    {
        var root = Directory.CreateTempSubdirectory("os-boot-features-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "os", "Hello")).FullName;
        File.WriteAllText(Path.Combine(folder, AppFiles.DeclarationPath), AppDeclaration.Write(new("hello", "1.0.0",
            new("Hello", "Shows hello world", [new("open", "Open")], [new("Greeting", "what to say", "hello")], []), "## Scenario: Greets" + Environment.NewLine + "It greets.")));
        Directory.CreateDirectory(Path.Combine(folder, "synapses"));
        File.WriteAllText(Path.Combine(folder, "synapses", "hello.cs"), "// b");
        return root;
    }

    private sealed class IdleRuntime : IAppRuntime
    {
        public Task<IReadOnlyDictionary<string, string>> Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
