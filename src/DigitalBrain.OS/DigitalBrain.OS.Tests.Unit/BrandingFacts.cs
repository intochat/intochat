using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

// Branding is configuration, so these facts pin the host's config to the section the module
// actually binds: FlutterModule binds its options from DigitalBrain:Modules:flutter:Options.
// Keys that bind to nothing are a silent branding regression.
public sealed class BrandingFacts
{
    private static string HostPath(string file)
        => Path.Combine(SolutionProjects.DirectoryOf("DigitalBrain.OS"), file);

    [Fact]
    public void TheHostAppSettingsCarryTheShellTitleWhereTheFlutterModuleBindsIt()
    {
        var configuration = new ConfigurationBuilder().AddJsonFile(HostPath("appsettings.json")).Build();
        var options = new DigitalBrain.Flutter.FlutterModuleOptions();
        configuration.GetSection(DigitalBrain.Framework.Configuration.DigitalBrainConfiguration.ModuleOptionsSection("flutter")).Bind(options);
        Assert.Equal("IntoChat", options.AssistantTitle);
    }
}
