using IntoChat.OS.Kernel.Install;

namespace IntoChat.OS.Tests.Unit.Kernel;

public sealed class InstallConfigFoundryFacts
{
    private static InstallConfig BaseConfig(FoundryConfig foundry) => new()
    {
        Subscription = "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
        Location = "polandcentral",
        Prefix = "intochat",
        Env = "dev",
        OsImage = "intochatdev.azurecr.io/os:latest",
        Foundry = foundry,
    };

    [Fact]
    public void ValidFoundryConfigPassesValidation()
    {
        var config = BaseConfig(new FoundryConfig
        {
            Enabled = true,
            Sku = "S0",
            Deployments =
            [
                new FoundryDeploymentConfig
                {
                    Name = "DeepSeek-V3.2",
                    ModelName = "DeepSeek-V3.2",
                    ModelFormat = "DeepSeek",
                    ModelVersion = "1.0",
                    SkuName = "GlobalStandard",
                    Capacity = 10,
                },
            ],
        });

        var validated = config.Validated();
        Assert.True(validated.Foundry.Enabled);
        Assert.Equal("S0", validated.Foundry.Sku);
    }

    [Theory]
    [InlineData("S0\nmalicious: true")]
    [InlineData("S0; rm -rf /")]
    [InlineData("S0 \"quoted\"")]
    [InlineData("S0/../../evil")]
    [InlineData("S0 $env")]
    public void RejectsUnsafeCharactersInFoundrySku(string unsafeSku)
    {
        var config = BaseConfig(new FoundryConfig
        {
            Enabled = true,
            Sku = unsafeSku,
        });

        Assert.Throws<InvalidOperationException>(config.Validated);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("modelName")]
    [InlineData("modelFormat")]
    [InlineData("modelVersion")]
    [InlineData("skuName")]
    public void RejectsUnsafeCharactersInFoundryDeploymentFields(string field)
    {
        const string payload = "bad:value\ninjected";
        var deployment = new FoundryDeploymentConfig
        {
            Name = field == "name" ? payload : "DeepSeek-V3.2",
            ModelName = field == "modelName" ? payload : "DeepSeek-V3.2",
            ModelFormat = field == "modelFormat" ? payload : "DeepSeek",
            ModelVersion = field == "modelVersion" ? payload : "1",
            SkuName = field == "skuName" ? payload : "GlobalStandard",
            Capacity = 10,
        };

        var config = BaseConfig(new FoundryConfig
        {
            Enabled = true,
            Sku = "S0",
            Deployments = [deployment],
        });

        Assert.Throws<InvalidOperationException>(config.Validated);
    }

    [Theory]
    [InlineData("foundry\nmalicious: true")]
    [InlineData("foundry; rm -rf /")]
    [InlineData("foundry/evil")]
    public void RejectsUnsafeCharactersInFoundryName(string unsafeName)
    {
        var config = BaseConfig(new FoundryConfig
        {
            Enabled = true,
            Sku = "S0",
        }) with
        {
            Names = new Dictionary<string, string> { ["foundry"] = unsafeName },
        };

        Assert.Throws<InvalidOperationException>(config.Validated);
    }
}
