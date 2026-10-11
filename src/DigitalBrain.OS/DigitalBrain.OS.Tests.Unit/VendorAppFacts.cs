using DigitalBrain.Apps;
using DigitalBrain.Microsoft.CSharp.Catalog;

namespace DigitalBrain.OS.Tests.Unit;

// Shipped bundles are ordinary packages: they compile against platform contracts and ship as-is.
public sealed class VendorAppFacts
{

    [Fact]
    public void EveryFirstPartyAppShips()
        => Assert.Equal(["customer-researcher", "sv-loans", "word-count"], SolutionProjects.FeaturesOf("intochat").Select(app => app.Name).Order());

    [Fact]
    public void EveryShippedProgramCompilesAgainstCurrentContracts()
    {
        foreach (var app in SolutionProjects.FeaturesOf("intochat"))
        {
            var sources = new Dictionary<string, string>(app.Files.Where(file => AppFiles.IsProgram(file.Key)))
            {
                [AppFiles.DeclarationPath] = AppDeclaration.Write(new(app.Name, app.Version, app.Manifest, app.Spec)),
            };
            var result = new CSharpScriptCheck().Compile(sources);
            Assert.True(result.Success, $"{app.Name}: {string.Join("; ", result.Errors.Select(error => $"{error.File}:{error.Line} {error.Message}"))}");
        }
    }

    // The researcher is the proof that a first-party app is an ordinary package: every contract its
    // scripts compile against is a platform module a user's own app could name, so it runs
    // on a host where no CustomerResearcher module is composed.
    [Fact]
    public void TheResearcherPackageUsesOnlyPlatformContracts()
    {
        var researcher = SolutionProjects.FeaturesOf("intochat").Single(app => app.Name == "customer-researcher");
        Assert.Equal(["controls.cs", "open.cs", "projection.cs", "queue.cs", "result.cs", "worker.cs"],
            researcher.Files.Where(file => AppFiles.IsProgram(file.Key) && !CSharpScriptCheck.IsShared(file.Value)).Select(file => file.Key).Order(StringComparer.Ordinal));
        var sources = researcher.Files.Where(file => AppFiles.IsProgram(file.Key)).Select(file => file.Value);
        Assert.All(sources, source =>
        {
            Assert.DoesNotContain("CustomerResearcher.Contracts", source, StringComparison.Ordinal);
            Assert.DoesNotContain("#:", source, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EveryShippedAppCarriesASpec()
        => Assert.All(SolutionProjects.FeaturesOf("intochat"), app =>
        {
            Assert.False(string.IsNullOrWhiteSpace(app.Spec), $"{app.Name} has no spec comment in {AppFiles.DeclarationPath}.");
        });

}
