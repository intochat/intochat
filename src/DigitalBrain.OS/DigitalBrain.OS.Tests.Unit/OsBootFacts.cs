using DigitalBrain.Apps;
using Microsoft.Extensions.DependencyInjection;

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
                // The script runtime is the sandbox seam: registered first, it wins, and installs run no program.
                services.AddSingleton<IAppRuntime, IdleRuntime>();
                services.AddOperatingSystemApps();
            })
            .StartAsync(ct);

        var person = await brain.PersonAsync("ada", ct);

        using var caller = person.AsCaller();
        foreach (var name in new[] { "settings", "assistant", "router", "calculator", "datavisualizer" })
        {
            var installed = await brain.Get<IApp>(IApp.Key(person.BrainId, name)).Read();
            Assert.Equal(AppStatus.Live, installed.Status);
            Assert.Equal("os", installed.Origin!.Publisher);
            Assert.IsType<AppSource.Shipped>(installed.Origin.Source);
        }
    }

    [Fact]
    public async Task ADefaultMissingFromTheCatalogDoesNotKeepTheOthersFromInstalling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create()
            .WithModule<AppsModule>()
            .ConfigureServices(services =>
            {
                // The script runtime is the sandbox seam: registered first, it wins, and installs run no program.
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
        public Task Deploy(string appKey, Dictionary<string, string> sources, Dictionary<string, string> configuration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task Retire(string appKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
