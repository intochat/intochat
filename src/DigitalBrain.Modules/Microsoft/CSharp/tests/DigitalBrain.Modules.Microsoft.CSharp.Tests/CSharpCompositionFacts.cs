using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using Microsoft.Extensions.DependencyInjection;

namespace DigitalBrain.Modules.Microsoft.CSharp.Tests;

public sealed class CSharpCompositionFacts
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionKeepsPackageServicesWhileOnlyAuthoringRegistersConsoleTools(bool authoring)
    {
        var builder = ModuleTest.Create().WithModule<CSharpModule, CSharpOptions>(options => options.Authoring = authoring).WithReminders();
        await using var brain = await builder.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(brain.SiloServices.GetRequiredService<CSharpToolService>().CanRun);
        Assert.NotNull(brain.SiloServices.GetRequiredService<CSharpCatalogStore>());
        var factories = brain.SiloServices.GetServices<IAgentToolFactory>().OfType<CSharpAgentTools>();
        var capabilities = brain.SiloServices.GetServices<IRegistryResourceProvider>().OfType<CSharpRegistryResources>();
        if (authoring)
        {
            Assert.Single(factories);
            var discovery = await Assert.Single(capabilities).Discover(TestContext.Current.CancellationToken);
            Assert.Equal(CSharpAgentTools.Names.Order(StringComparer.Ordinal), Assert.Single(discovery.Capabilities).Tools.Order(StringComparer.Ordinal));
        }
        else
        {
            Assert.Empty(factories);
            Assert.Empty(capabilities);
        }
    }
}
