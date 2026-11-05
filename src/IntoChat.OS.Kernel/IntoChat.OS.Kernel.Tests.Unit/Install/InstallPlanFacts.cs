using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

public sealed class InstallPlanFacts
{
    private const string ConfigJson = """
        {
          "subscription": "00000000-0000-0000-0000-000000000001",
          "location": "westeurope",
          "prefix": "intochat",
          "env": "test",
          "osImage": "ghcr.io/intochat/os:1.0.0",
          "uiImage": "ghcr.io/intochat/os-ui:1.0.0",
          "registryServer": "intochat.azurecr.io",
          "postgres": {
            "enabled": true,
            "databases": ["intochat", "secondary"]
          }
        }
        """;

    [Fact]
    public void UiAppSpecSetsPort80AndKernelOriginEnvironmentVariable()
    {
        var config = InstallConfig.Parse(ConfigJson);
        var uiSpec = InstallPlan.UiApp(config, "os.test.intochat.io");

        Assert.Equal("intochat-test-ui", uiSpec.Name);
        Assert.Equal("ghcr.io/intochat/os-ui:1.0.0", uiSpec.Image);
        Assert.Equal(80, uiSpec.IngressPort);
        Assert.True(uiSpec.Variables.TryGetValue("KERNEL_ORIGIN", out var origin));
        Assert.Equal("https://os.test.intochat.io", origin);
    }

    [Fact]
    public void PostgresSpecsIncludeBothDbConnectionAndPostgresConnectionString()
    {
        var config = InstallConfig.Parse(ConfigJson);
        var specs = InstallPlan.Build(config);

        var secretSpecs = specs.OfType<SecretSpec>().ToList();

        var primaryDbConnection = Assert.Single(secretSpecs, s => s.Name == "db-connection");
        var primaryPgConnection = Assert.Single(secretSpecs, s => s.Name == "postgres-connectionstring");
        Assert.Equal(primaryDbConnection.Value, primaryPgConnection.Value);

        var secondaryDbConnection = Assert.Single(secretSpecs, s => s.Name == "db-connection-secondary");
        var secondaryPgConnection = Assert.Single(secretSpecs, s => s.Name == "postgres-connectionstring-secondary");
        Assert.Equal(secondaryDbConnection.Value, secondaryPgConnection.Value);
    }
}
