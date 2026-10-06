using DigitalBrain.Apps;

namespace DigitalBrain.OS.Tests.Unit;

// Shipped bundles execute through their tests.cs share gate.
public sealed class VendorAppFacts
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DigitalBrain.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException($"Repository root (DigitalBrain.slnx) was not found above {AppContext.BaseDirectory}.");
    }

    private static readonly DirectoryShippedAppSource Source =
        new(Path.Combine(FindRepositoryRoot(), "src", "IntoChat", "Apps"), "intochat");
    [Fact]
    public void EveryFirstPartyAppShips()
        => Assert.Equal(["customer-researcher", "word-count"], Source.Load().Select(app => app.Name).Order());

    [Fact]
    public void EveryShippedProgramAndTestsFileCompilesAgainstCurrentContracts()
    {
        foreach (var app in Source.Load())
        {
            var sources = new Dictionary<string, string>(app.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)));
            var result = new DigitalBrain.Microsoft.CSharp.CSharpScriptCheck().Check(sources);
            Assert.True(result.Success, $"{app.Name}: {string.Join("; ", result.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}"))}");
        }
    }

    // The researcher is the proof that a first-party app is an ordinary package: every contract its
    // scripts compile against is a platform module a user's own app could name, so its tests verify
    // on a host where no CustomerResearcher module is composed.
    [Fact]
    public void TheResearcherPackageUsesOnlyPlatformContracts()
    {
        string[] platform =
        [
            "/brain/src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/DigitalBrain.Modules.Apps.Contracts.csproj",
            "/brain/src/Modules/Google/Flutter/DigitalBrain.Modules.Flutter.Contracts/DigitalBrain.Modules.Flutter.Contracts.csproj",
            "/brain/src/Modules/Microsoft/Playwright/DigitalBrain.Modules.Microsoft.Playwright.Contracts/DigitalBrain.Modules.Microsoft.Playwright.Contracts.csproj",
            "/brain/src/Modules/AI/DigitalBrain.Modules.AI.Contracts/DigitalBrain.Modules.AI.Contracts.csproj",
            "/brain/src/Modules/Postgres/DigitalBrain.Modules.Postgres.Contracts/DigitalBrain.Modules.Postgres.Contracts.csproj",
        ];
        var researcher = Source.Load().Single(app => app.Name == "customer-researcher");
        Assert.Equal(["behaviors/controls.cs", "behaviors/research.cs", "behaviors/surface.cs"],
            researcher.Files.Keys.Where(AppFiles.IsBehavior).Order(StringComparer.Ordinal));
        var sources = researcher.Files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal)).Select(file => file.Value);
        Assert.All(sources, source =>
        {
            Assert.DoesNotContain("CustomerResearcher.Contracts", source, StringComparison.Ordinal);
            foreach (var directive in source.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("#:project", StringComparison.Ordinal)))
            { Assert.Contains(directive["#:project".Length..].Trim(), platform); }
        });
    }

    [Fact]
    public void EveryShippedAppCarriesSpecAndTests()
        => Assert.All(Source.Load(), app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Spec), $"{app.Name} has no {AppFiles.SpecPath}.");
            Assert.False(string.IsNullOrWhiteSpace(app.Files.GetValueOrDefault(AppFiles.TestsPath)), $"{app.Name} has no {AppFiles.TestsPath}.");
        });

}
