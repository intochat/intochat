using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Kernel.Install;

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
            OsImage: "acrintochattest.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111");

        var updated = prodConfig.WithManifest(manifest);

        Assert.Equal("acrintochatprod.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111", updated.OsImage);
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

    [Fact]
    public void PulumiStackSynthesizerMapsModuleManifestsAndIntegrationsToVaultSecretsWithoutSessionPools()
    {
        var config = InstallConfig.Parse("""
            {
              "subscription": "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
              "location": "polandcentral",
              "prefix": "intochat",
              "env": "test",
              "resourceGroup": "rg-intochat-test",
              "osImage": "acrintochattest.azurecr.io/os:latest",
              "registryServer": "acrintochattest.azurecr.io",
              "registry": { "enabled": true, "sku": "Basic" },
              "postgres": { "enabled": true, "sku": "Standard_B1ms", "tier": "Burstable", "storageGb": 32, "adminLogin": "intochat", "databases": ["intochat"] },
              "modules": {
                "auth": {
                  "Posture": "Secured",
                  "PublicBaseUrl": "https://os.test.intochat.io"
                }
              }
            }
            """);

        var providedSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["openai-apikey"] = "sk-test-openai-key",
            ["postgres-connection-string"] = "Host=intochat-test-pg.postgres.database.azure.com;Database=intochat",
        };

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules, providedSecrets);

        // Zero SessionPool resources ever synthesized
        Assert.DoesNotContain(plan.Resources, r => r.Kind.Contains("sessionPool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sessionPools", plan.PulumiYaml, StringComparison.OrdinalIgnoreCase);

        // Key Vault secrets and Container App bindings synthesized from ModuleManifests and IntegrationSchemas
        Assert.Contains(plan.Secrets, s => s.VaultSecret == "openai-apikey");
        Assert.Contains(plan.Secrets, s => s.VaultSecret == "postgres-connection-string");
        Assert.Contains("keyVaultUrl: \"https://intochat-test-kv.vault.azure.net/secrets/openai-apikey\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("secretName: \"openai-apikey\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("containerAppName: \"intochat-test-os\"", plan.PulumiYaml, StringComparison.Ordinal);

        var importJson = PulumiStackSynthesizer.ExportImportEntries(plan).ToJsonString();
        Assert.Contains("azure-native:app:ContainerApp", importJson, StringComparison.Ordinal);
        Assert.Contains("azure-native:keyvault:Vault", importJson, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionPools", importJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UniversalConfigWithCloudAndSandboxSynthesizesNewResourceGroupAndScaleToZeroSandboxApp()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);

        Assert.Equal("intochat-test", config.GroupName);
        Assert.Equal("acrintochattest", config.RegistryName);
        Assert.Equal("intochat-test-os", config.OsName);
        Assert.Equal("intochat-test-cloud", config.CloudName);
        Assert.Equal("intochat-test-sandbox", config.SandboxName);

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);

        Assert.Contains("resourceGroupName: \"intochat-test\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("containerAppName: \"intochat-test-os\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("containerAppName: \"intochat-test-cloud\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("containerAppName: \"intochat-test-sandbox\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("value: \"http://intochat-test-sandbox\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("keyVaultUrl: \"https://intochat-test-kv.vault.azure.net/secrets/sandbox-enroll-key\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionPools", plan.PulumiYaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PulumiNeuronSynthesizesAndPersistsStackSummary()
    {
        var engine = new KernelPulumiEngine(OperatingSystemModules.Modules);
        var summary = await engine.SynthesizeAsync("test", includeCloud: true, includeSandbox: true, TestContext.Current.CancellationToken);

        Assert.Equal("test", summary.Env);
        Assert.Equal("intochat-test", summary.ResourceGroup);
        Assert.Equal("intochat-test-os", summary.OsAppName);
        Assert.Equal("intochat-test-cloud", summary.CloudAppName);
        Assert.Equal("intochat-test-sandbox", summary.SandboxAppName);
        Assert.Contains("containerAppName: \"intochat-test-sandbox\"", summary.PulumiYaml, StringComparison.Ordinal);
    }
}
