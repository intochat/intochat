using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Testing.Tests.Integration;

public sealed class EnvironmentProjectionFacts
{
    [Fact]
    public void ProjectionPreservesResourceValuesAndOrdinaryNamesWhileNormalizingConfigurationKeys()
    {
        var reference = new object();
        var projected = EnvironmentProjection.Configuration(new Dictionary<string, object>
        {
            ["Options__Endpoints__0"] = reference,
            ["SYSTEMONE_MODEL"] = "local",
        });
        Assert.Same(reference, projected["options:endpoints:0"]);
        Assert.Equal("local", projected["SYSTEMONE_MODEL"]);
        Assert.Equal(2, projected.Count);
    }

    [Fact]
    public void EnvironmentConfigurationGrammarRoundTripsModuleOptions()
    {
        var key = DigitalBrainConfiguration.ModuleOptionsSection("ai") + ":DefaultModel";
        Assert.Equal(key, DigitalBrainConfiguration.ConfigurationName(DigitalBrainConfiguration.EnvironmentName(key)));
        Assert.Equal(key, DigitalBrainConfiguration.ConfigurationName(key));
    }
}
