using IntoChat.OS.Flutter.Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace IntoChat.OS.Modules.Flutter.Tests.Hosting;

public sealed class FlutterHostOptionsGrammarFacts
{
    [Fact]
    public void TheModuleSectionBinds()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { [FlutterHostOptions.WebContainerKey] = "true" }).Build();

        var options = FlutterHostOptions.Bind(new FlutterHostOptions(), configuration);

        Assert.True(options.WebContainer);
    }
}
