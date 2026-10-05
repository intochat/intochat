using DigitalBrain.Flutter.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DigitalBrain.Modules.Flutter.Tests.Hosting;

public sealed class FlutterHostOptionsGrammarFacts
{
    // The host options are read in the AppHost process, which the OS-side guard never sees;
    // the bind defends itself, like AIOptionsRuntime does.
    [Fact]
    public void TheLegacyHostingSectionRefusesToBind()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["DigitalBrain:Flutter:Hosting:WebContainer"] = "true" }).Build();

        var error = Assert.Throws<InvalidOperationException>(() => FlutterHostOptions.Bind(new FlutterHostOptions(), configuration));
        Assert.Contains(FlutterHostOptions.SectionName, error.Message);
    }

    [Fact]
    public void TheModuleSectionBinds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [FlutterHostOptions.WebContainerKey] = "true" }).Build();

        var options = FlutterHostOptions.Bind(new FlutterHostOptions(), configuration);

        Assert.True(options.WebContainer);
    }
}
