using DigitalBrain.AI.Agents;
using DigitalBrain.AI.Tools;
using DigitalBrain.Framework;
using DigitalBrain.Microsoft.CSharp;
using DigitalBrain.Microsoft.CSharp.Authoring;
using DigitalBrain.Microsoft.CSharp.Catalog;
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
        if (authoring) { Assert.Single(factories); }
        else { Assert.Empty(factories); }
    }
}
