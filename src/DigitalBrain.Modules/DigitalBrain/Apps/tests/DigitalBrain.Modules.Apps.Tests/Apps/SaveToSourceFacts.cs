using DigitalBrain.Modules.Apps.Tests;
using DigitalBrain.Modules.Apps.Tests.Runtimes;
using DigitalBrain.Testing.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Apps.Tests.Apps;

// In development an edited copy of a shipped app writes back into the vendor's folder as the next
// version, and the copy is the vendor's again. Nowhere else, and never for a person's own app.
public sealed class SaveToSourceFacts : IDisposable
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("save-to-source-").FullName;

    public SaveToSourceFacts()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Greeter", "synapses"));
        File.WriteAllText(Path.Combine(_root, "Greeter", "app.json"),
            """{"name":"greeter","title":"Greeter","description":"Greets.","version":"1.2.0","operations":[{"name":"ask","description":"Greet"}],"settings":[]}""");
        File.WriteAllText(Path.Combine(_root, "Greeter", "synapses", "greet.cs"), "// greet\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<ModuleBrain> Start(string environment) => ModuleTest.Create().WithModule<AppsModule>()
        .WithFakeAppRuntime(new())
        .ConfigureSilo(silo =>
        {
            silo.Services.AddSingleton<IShippedAppSource>(new DirectoryShippedAppSource(_root, "intochat", development: true));
            silo.Services.AddSingleton<ShippedCatalog>();
            silo.Services.AddSingleton<IHostEnvironment>(new Environment(environment));
        })
        .StartAsync(Ct);

    private static async Task<IApp> InstalledAndEdited(ModuleBrain brain)
    {
        AppOwner.Stamp("alice");
        await brain.SiloServices.GetRequiredService<AppInstaller>().Install("intochat", "greeter", Ct);
        Caller.Clear();
        var copy = brain.Get<IApp>(IApp.Key(AppOwner.Scope("alice"), "greeter"));
        await copy.WriteBehavior("synapses/greet.cs", "// greet warmly\n", Guid.NewGuid());
        return copy;
    }

    [Fact]
    public async Task SaveToSourceWritesTheFolderBumpsTheVersionAndReattachesTheCopy()
    {
        await using var brain = await Start(Environments.Development);
        var copy = await InstalledAndEdited(brain);
        Assert.True((await copy.Read()).Edited);

        var saved = await copy.SaveToSource();

        Assert.Equal("// greet warmly\n", File.ReadAllText(Path.Combine(_root, "Greeter", "synapses", "greet.cs")));
        Assert.Contains("\"version\": \"1.2.2\"", File.ReadAllText(Path.Combine(_root, "Greeter", "app.json")), StringComparison.Ordinal);
        Assert.Equal("1.2.2", saved.Version);
        Assert.Equal(new AppOrigin("intochat", new AppSource.Shipped(), "greeter", "1.2.2"), saved.Origin);
        Assert.False(saved.Edited);
        var listing = Assert.Single(await brain.SiloServices.GetRequiredService<ShippedCatalog>().Listings());
        Assert.Equal("1.2.2", listing.Version);
        Assert.Equal("// greet warmly\n", (await brain.SiloServices.GetRequiredService<ShippedCatalog>().Find("intochat", "greeter"))!.Files["synapses/greet.cs"]);
    }

    [Fact]
    public async Task SaveToSourceIsRefusedOutsideDevelopment()
    {
        await using var brain = await Start(Environments.Production);
        var copy = await InstalledAndEdited(brain);

        await Assert.ThrowsAsync<InvalidOperationException>(copy.SaveToSource);
        Assert.Equal("// greet\n", File.ReadAllText(Path.Combine(_root, "Greeter", "synapses", "greet.cs")));
        Assert.True((await copy.Read()).Edited);
    }

    [Fact]
    public async Task SaveToSourceIsRefusedForAnAppThatCameFromABrain()
    {
        await using var brain = await Start(Environments.Development);
        var app = brain.Get<IApp>(IApp.Key(AppOwner.Scope("alice"), "greeter"));
        await app.Create(new("Greeter", "Greets.", [], [], []), "## Scenario: Greets\nIt greets.");
        await app.RecordOrigin(new AppOrigin("bob", new AppSource.Brain("account-bob", "bob"), "greeter", "1.0.0"));

        await Assert.ThrowsAsync<InvalidOperationException>(app.SaveToSource);
        Assert.Equal("// greet\n", File.ReadAllText(Path.Combine(_root, "Greeter", "synapses", "greet.cs")));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
