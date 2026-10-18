using IntoChat.OS;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Framework.Modules;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class ModuleSelectionFacts
{
    [Fact]
    public void ARegisteredModuleRunsWithoutAnyConfiguration()
    {
        var server = new IntoChatOSServerBuilder().AddModule<SampleModule>();
        var modules = server.SelectModules(new ConfigurationBuilder().Build());
        Assert.Single(modules);
        Assert.IsType<SampleModule>(modules[0]);
    }

    [Fact]
    public void ADisabledModuleStaysOut()
    {
        var server = new IntoChatOSServerBuilder().AddModule<SampleModule>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [IntoChatOSConfiguration.ModuleSection("sample") + ":Enabled"] = "false" }).Build();
        Assert.Empty(server.SelectModules(configuration));
    }

    [Fact]
    public void DisablingIgnoresCaseLikeAnyOtherConfigurationKey()
    {
        var server = new IntoChatOSServerBuilder().AddModule<SampleModule>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [IntoChatOSConfiguration.ModuleSection("Sample") + ":Enabled"] = "false" }).Build();
        Assert.Empty(server.SelectModules(configuration));
    }

    [Fact]
    public void AffirmingARegisteredModuleInAnotherCaseIsNotAnError()
    {
        var server = new IntoChatOSServerBuilder().AddModule<SampleModule>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [IntoChatOSConfiguration.ModuleSection("SAMPLE") + ":Enabled"] = "true" }).Build();
        Assert.Single(server.SelectModules(configuration));
    }

    [Fact]
    public void EnablingAnUnknownModuleIsAnError()
    {
        var server = new IntoChatOSServerBuilder().AddModule<SampleModule>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [IntoChatOSConfiguration.ModuleSection("ghost") + ":Enabled"] = "true" }).Build();
        Assert.Throws<InvalidOperationException>(() => server.SelectModules(configuration));
    }

    public sealed class SampleModule() : Module(new ModuleId("sample"))
    { public override void Configure(Orleans.Hosting.ISiloBuilder silo) { } }
}
