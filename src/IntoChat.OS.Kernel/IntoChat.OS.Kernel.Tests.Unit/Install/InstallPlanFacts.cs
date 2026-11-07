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
          "registryServer": "intochat.azurecr.io",
          "postgres": {
            "enabled": true,
            "databases": ["intochat", "secondary"]
          }
        }
        """;

    [Fact]
    public void WithManifestRewritesImageDigestsToTargetRegistryLoginServer()
    {
        var prodConfig = InstallConfig.Parse(ConfigJson) with
        {
            Env = "prod",
            RegistryServer = "acrintochatprod.azurecr.io",
        };
        var manifest = new ReleaseManifest(
            Version: "0.16.1",
            Commit: "abc1234",
            OsImage: "acrintochattest.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111",
            SandboxImage: "acrintochattest.azurecr.io/os-sandbox@sha256:3333333333333333333333333333333333333333333333333333333333333333");

        var updated = prodConfig.WithManifest(manifest);

        Assert.Equal("acrintochatprod.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111", updated.OsImage);
        Assert.Equal("acrintochatprod.azurecr.io/os-sandbox@sha256:3333333333333333333333333333333333333333333333333333333333333333", updated.Modules["csharp"]["sandboxImage"]);
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
