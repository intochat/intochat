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

    // An OS silo refuses a second silo in its cluster (SingleSiloGuard), so the OS and Cloud apps never overlap two revisions:
    // an update stops the running one first. The sandbox is no silo and keeps single revision mode's overlap.
    [Fact]
    public void TheSiloApps_StopTheirRunningRevisionBeforeAnUpdate()
    {
        var config = InstallConfig.Universal(env: "test", subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2", includeCloudApp: true, includeSandboxApp: true);
        var apps = InstallPlan.Build(config).OfType<ContainerAppSpec>().ToDictionary(app => app.Name, StringComparer.Ordinal);

        Assert.True(apps[config.OsName].StopBeforeUpdate);
        Assert.True(apps[config.CloudName].StopBeforeUpdate);
        Assert.False(apps[config.SandboxName].StopBeforeUpdate);
        Assert.True(InstallPlan.OsApp(config, new Dictionary<string, string>(), []).StopBeforeUpdate);
    }

    [Fact]
    public void ThePulumiStack_StopsASiloAppBeforeItsTemplateChanges()
    {
        var config = InstallConfig.Universal(env: "test", subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2", includeCloudApp: true, includeSandboxApp: true);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        var stops = StopCommands(yaml);
        Assert.Equal([config.CloudName, config.OsName], stops.Keys.Order(StringComparer.Ordinal));
        foreach (var name in new[] { config.OsName, config.CloudName })
        {
            // The app depends on its stop command, which Pulumi therefore runs before it updates the app.
            var app = yaml.IndexOf($"containerAppName: \"{name}\"", StringComparison.Ordinal);
            var header = yaml.LastIndexOf("type: azure-native:app:ContainerApp", app, StringComparison.Ordinal);
            var dependsOn = yaml[header..app];
            Assert.Contains(stops[name].Logical, dependsOn, StringComparison.Ordinal);
            Assert.True(yaml.IndexOf(stops[name].Logical + ":", StringComparison.Ordinal) < header);
        }

        // A new image is a new revision: the trigger changes. The tags are not part of the revision: they do not trigger.
        var newImage = StopCommands(PulumiStackSynthesizer.Synthesize(config with { OsImage = "ghcr.io/intochat/os:2.0.0" }, OperatingSystemModules.Modules).PulumiYaml);
        Assert.NotEqual(stops[config.OsName].Trigger, newImage[config.OsName].Trigger);
        Assert.Equal(stops[config.CloudName].Trigger, newImage[config.CloudName].Trigger);
    }

    private static Dictionary<string, (string Logical, string Trigger)> StopCommands(string yaml)
        => System.Text.RegularExpressions.Regex.Matches(yaml,
                @"\n  (?<logical>[A-Za-z0-9-]+-stop):\r?\n    type: command:local:Command\r?\n(?:.*\n)*?      triggers: \[""(?<trigger>[0-9a-f]{64})""\]\r?\n      create: \|\r?\n        app=""(?<app>[^""]+)""")
            .ToDictionary(match => match.Groups["app"].Value, match => (match.Groups["logical"].Value, match.Groups["trigger"].Value), StringComparer.Ordinal);

    [Fact]
    public async Task PulumiNeuronSynthesizesAndPersistsStackSummary()
    {
        using var workspace = new DigitalBrain.Testing.TempDirectory("intochat-pulumi-test-");
        var engine = new KernelPulumiEngine(OperatingSystemModules.Modules, workspace.Path);
        var summary = await engine.SynthesizeAsync("test", includeCloud: true, includeSandbox: true, TestContext.Current.CancellationToken);

        Assert.Equal("test", summary.Env);
        Assert.Equal("intochat-test", summary.ResourceGroup);
        Assert.Equal("intochat-test-os", summary.OsAppName);
        Assert.Equal("intochat-test-cloud", summary.CloudAppName);
        Assert.Equal("intochat-test-sandbox", summary.SandboxAppName);
        Assert.Contains("containerAppName: \"intochat-test-sandbox\"", summary.PulumiYaml, StringComparison.Ordinal);
    }
}
