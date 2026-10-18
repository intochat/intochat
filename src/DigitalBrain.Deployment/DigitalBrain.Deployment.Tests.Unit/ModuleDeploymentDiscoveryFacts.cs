using DigitalBrain;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Modules;

namespace DigitalBrain.Deployment.Tests.Unit;

public sealed class ModuleDeploymentDiscoveryFacts
{
    [Fact]
    public void A_contributor_referenced_by_the_root_is_found_under_its_module()
    {
        var deployments = DigitalBrainDeployment.Discover([new ModuleId("qdrant")], typeof(DigitalBrain.Qdrant.QdrantDeployment).Assembly);
        Assert.IsType<DigitalBrain.Qdrant.QdrantDeployment>(Assert.Single(deployments));
    }

    [Fact]
    public void A_composed_module_whose_contributor_is_not_referenced_is_skipped()
    {
        var deployments = DigitalBrainDeployment.Discover([new ModuleId("qdrant")], typeof(DigitalBrainDeployment).Assembly);
        Assert.Empty(deployments);
    }

    [Fact]
    public void A_contributor_referenced_for_an_uncomposed_module_fails_loudly()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            DigitalBrainDeployment.Discover([new ModuleId("postgres")], typeof(DigitalBrain.Qdrant.QdrantDeployment).Assembly));
        Assert.Contains("qdrant", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
