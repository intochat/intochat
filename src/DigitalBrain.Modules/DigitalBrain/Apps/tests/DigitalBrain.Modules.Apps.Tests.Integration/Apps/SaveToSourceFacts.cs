using DigitalBrain.Modules.Apps.Tests.Integration;
using DigitalBrain.Modules.Apps.Tests.Integration.Runtimes;
using DigitalBrain.Testing;
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
        Directory.CreateDirectory(Path.Combine(_root, "intochat", "Greeter"));
        File.WriteAllText(Path.Combine(_root, "intochat", "Greeter", "app.cs"),
            Declarations.Of("greeter", "1.2.0", "Greeter", "Greets.", [new("ask", "Greet")]));
        File.WriteAllText(Path.Combine(_root, "intochat", "Greeter", "greet.cs"), "// greet\n");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task<ITestBrain> Start(string environment) => TestBrain.Create()
        .WithModule<AppsModule, AppsOptions>(options => options.Features = _root)
        .WithFakeAppRuntime(new())
        // This production-mode fixture deliberately uses its isolated test key, not a machine TPM.
        .WithConfiguration(DigitalBrain.Framework.DigitalBrainNames.ConfiguredCustodyAcknowledgedConfigurationKey, "true")
        // The test host runs as "Testing"; save-to-source and a writable features folder are development-only.
        .ConfigureServices(services => services.AddSingleton<IHostEnvironment>(new Environment(environment)))
        .StartAsync(Ct);

    private static async Task<(Person Alice, IApp Copy)> InstalledAndEdited(ITestBrain brain)
    {
        var alice = await brain.PersonAsync("alice", Ct);
        var copy = brain.Get<IApp>(IApp.Key(alice.BrainId, "greeter"));
        using (alice.AsCaller())
        {
            await brain.Services.GetRequiredService<AppInstaller>().Install("intochat", "greeter", Ct);
            await copy.WriteBehavior("greet.cs", "// greet warmly\n", Guid.NewGuid());
        }
        return (alice, copy);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SaveToSourceWritesTheFolderBumpsTheVersionAndReattachesTheCopy()
    {
        await using var brain = await Start(Environments.Development);
        var (alice, copy) = await InstalledAndEdited(brain);
        using var _ = alice.AsCaller();
        Assert.True((await copy.Read()).Edited);

        var saved = await copy.SaveToSource();

        Assert.Equal("// greet warmly\n", File.ReadAllText(Path.Combine(_root, "intochat", "Greeter", "greet.cs")));
        Assert.Contains("Version = \"1.2.2\"", File.ReadAllText(Path.Combine(_root, "intochat", "Greeter", "app.cs")), StringComparison.Ordinal);
        Assert.Equal("1.2.2", saved.Version);
        Assert.Equal(new AppOrigin("intochat", new AppSource.Shipped(), "greeter", "1.2.2"), saved.Origin);
        Assert.False(saved.Edited);
        var listing = Assert.Single(await brain.Services.GetRequiredService<FeatureCatalog>().Listings());
        Assert.Equal("1.2.2", listing.Version);
        Assert.Equal("// greet warmly\n", (await brain.Services.GetRequiredService<FeatureCatalog>().Find("intochat", "greeter"))!.Files["greet.cs"]);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SaveToSourceIsRefusedOutsideDevelopment()
    {
        await using var brain = await Start(Environments.Production);
        var (alice, copy) = await InstalledAndEdited(brain);
        using var _ = alice.AsCaller();

        await Assert.ThrowsAsync<InvalidOperationException>(copy.SaveToSource);
        Assert.Equal("// greet\n", File.ReadAllText(Path.Combine(_root, "intochat", "Greeter", "greet.cs")));
        Assert.True((await copy.Read()).Edited);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task SaveToSourceIsRefusedForAnAppThatCameFromABrain()
    {
        await using var brain = await Start(Environments.Development);
        var alice = await brain.PersonAsync("alice", Ct);
        using var _ = alice.AsCaller();
        var app = brain.Get<IApp>(IApp.Key(alice.BrainId, "greeter"));
        await app.Create(new("Greeter", "Greets.", [], [], []), "## Scenario: Greets\nIt greets.");
        await app.RecordOrigin(new AppOrigin("bob", new AppSource.Brain("account-bob", "bob"), "greeter", "1.0.0"));

        await Assert.ThrowsAsync<InvalidOperationException>(app.SaveToSource);
        Assert.Equal("// greet\n", File.ReadAllText(Path.Combine(_root, "intochat", "Greeter", "greet.cs")));
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
