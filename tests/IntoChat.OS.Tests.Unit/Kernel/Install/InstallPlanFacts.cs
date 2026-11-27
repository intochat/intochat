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
    public void PostgresSpecsEmitCanonicalIntegrationSchemaSecretsOnly()
    {
        var config = InstallConfig.Parse(ConfigJson);
        var specs = InstallPlan.Build(config);

        var secretSpecs = specs.OfType<SecretSpec>().ToList();

        var primaryPgConnection = Assert.Single(secretSpecs, s => s.Name == "postgres-connectionstring");
        Assert.Contains("Database=intochat", primaryPgConnection.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSpecs, s => s.Name == "db-connection");

        var secondaryPgConnection = Assert.Single(secretSpecs, s => s.Name == "postgres-connectionstring-secondary");
        Assert.Contains("Database=secondary", secondaryPgConnection.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(secretSpecs, s => s.Name == "db-connection-secondary");
    }

    [Fact]
    public void LegacyVaultPostgresSecretNamesFallbackToCanonicalIntegrationVariable()
    {
        var config = InstallConfig.Parse("""
            {
              "subscription": "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
              "location": "polandcentral",
              "prefix": "intochat",
              "env": "test",
              "osImage": "acrintochattest.azurecr.io/os:latest",
              "registryServer": "acrintochattest.azurecr.io",
              "postgres": { "enabled": false }
            }
            """);

        var existingVaultSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "db-connection",
        };

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules, existingVaultSecrets: existingVaultSecrets);

        var pgSecret = Assert.Single(plan.Secrets, s => s.Variable == ManifestEnvironment.IntegrationSecretVariable("postgres", "ConnectionString"));
        Assert.Equal("db-connection", pgSecret.VaultSecret);
        Assert.Contains("keyVaultUrl: \"https://intochat-test-kv.vault.azure.net/secrets/db-connection\"", plan.PulumiYaml, StringComparison.Ordinal);
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
            ["postgres-connectionstring"] = "Host=intochat-test-pg.postgres.database.azure.com;Database=intochat",
        };

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules, providedSecrets);

        // Zero SessionPool resources ever synthesized
        Assert.DoesNotContain(plan.Resources, r => r.Kind.Contains("sessionPool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("sessionPools", plan.PulumiYaml, StringComparison.OrdinalIgnoreCase);

        // Key Vault secrets and Container App bindings synthesized from ModuleManifests and IntegrationSchemas
        Assert.Contains(plan.Secrets, s => s.VaultSecret == "openai-apikey");
        Assert.Contains(plan.Secrets, s => s.VaultSecret == "postgres-connectionstring");
        Assert.DoesNotContain(plan.Secrets, s => s.VaultSecret == "postgres-connection-string");
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

        var stops = Commands(yaml, "stop");
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
        var newImage = Commands(PulumiStackSynthesizer.Synthesize(config with { OsImage = "ghcr.io/intochat/os:2.0.0" }, OperatingSystemModules.Modules).PulumiYaml, "stop");
        Assert.NotEqual(stops[config.OsName].Trigger, newImage[config.OsName].Trigger);
        Assert.Equal(stops[config.CloudName].Trigger, newImage[config.CloudName].Trigger);
    }

    // The stop can run in a `pulumi up` that then changes nothing (the first run after the stop command appears, or a run
    // that fails before the app): the start command after the app runs in every up that ran the stop, and in the next one
    // when that run failed first, and brings the app's revision back.
    [Fact]
    public void ThePulumiStack_StartsASiloAppAgainAfterEveryUpThatStoppedIt()
    {
        var config = InstallConfig.Universal(env: "test", subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2", includeCloudApp: true, includeSandboxApp: true);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        var stops = Commands(yaml, "stop");
        var starts = Commands(yaml, "start");
        Assert.Equal(stops.Keys.Order(StringComparer.Ordinal), starts.Keys.Order(StringComparer.Ordinal));
        foreach (var name in new[] { config.OsName, config.CloudName })
        {
            // The same trigger: Pulumi replaces, and so runs, both in the same up.
            Assert.Equal(stops[name].Trigger, starts[name].Trigger);
            var appLogical = stops[name].Logical[..^"-stop".Length];
            var app = yaml.IndexOf($"containerAppName: \"{name}\"", StringComparison.Ordinal);
            var start = yaml.IndexOf("\n  " + starts[name].Logical + ":", StringComparison.Ordinal);
            var next = System.Text.RegularExpressions.Regex.Match(yaml[(start + 1)..], @"\n  \S|\n\S");
            var block = next.Success ? yaml[start..(start + 1 + next.Index)] : yaml[start..];
            Assert.True(start > app);
            Assert.Contains($"\"${{{appLogical}}}\"", block, StringComparison.Ordinal);
            Assert.Contains($"\"${{{stops[name].Logical}}}\"", block, StringComparison.Ordinal);
            // An inactive latest revision is activated; one that does not become ready gives way to the last ready one.
            Assert.Contains("az containerapp revision activate -n \"$app\" -g \"$group\" --revision \"$latest\"", block, StringComparison.Ordinal);
            Assert.Contains("latestReadyRevisionName", block, StringComparison.Ordinal);
            Assert.Contains("az containerapp revision activate -n \"$app\" -g \"$group\" --revision \"$ready\"", block, StringComparison.Ordinal);
            Assert.Contains("exit 1", block, StringComparison.Ordinal);
        }
    }

    private static Dictionary<string, (string Logical, string Trigger)> Commands(string yaml, string kind)
        => System.Text.RegularExpressions.Regex.Matches(yaml,
                @"\n  (?<logical>[A-Za-z0-9-]+-" + kind + @"):\r?\n    type: command:local:Command\r?\n(?:.*\n)*?      triggers: \[""(?<trigger>[0-9a-f]{64})""\]\r?\n      create: \|\r?\n        app=""(?<app>[^""]+)""")
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

    [Fact]
    public void ThePulumiStack_IgnoresContainerAppCustomDomainChanges()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);

        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        foreach (var name in new[] { config.OsName, config.CloudName, config.SandboxName })
        {
            var app = yaml.IndexOf($"containerAppName: \"{name}\"", StringComparison.Ordinal);
            Assert.True(app > 0, $"ContainerApp '{name}' not found in synthesized Pulumi YAML.");
            var header = yaml.LastIndexOf("type: azure-native:app:ContainerApp", app, StringComparison.Ordinal);
            Assert.True(header > 0, $"ContainerApp header for '{name}' not found in synthesized Pulumi YAML.");
            var optionsBlock = yaml[header..app];
            Assert.Contains("    options:\n", optionsBlock, StringComparison.Ordinal);
            Assert.Contains("      ignoreChanges:\n        - configuration.ingress.customDomains\n", optionsBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("- configuration.secrets", optionsBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("- configuration\n", optionsBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryContainerAppEnvSecretRefIsDeclaredInConfigurationSecrets()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true) with
        {
            Postgres = new PostgresConfig { Enabled = true, Databases = ["intochat"] },
        };

        var providedSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["postgres-admin-password"] = "test-pg-pass",
            ["openrouter-apikey"] = "sk-or-test",
        };
        var existingVaultSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "openai-apikey",
        };

        var yaml = PulumiStackSynthesizer.Synthesize(
            config,
            OperatingSystemModules.Modules,
            providedSecrets,
            existingVaultSecrets).PulumiYaml;

        foreach (var name in new[] { config.OsName, config.CloudName, config.SandboxName })
        {
            var block = ResourceBlock(yaml, $"container-app-{name}");
            var declaredSecrets = System.Text.RegularExpressions.Regex
                .Matches(block, @"^\s{10}- name: ""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();
            var referencedSecrets = System.Text.RegularExpressions.Regex
                .Matches(block, @"^\s{16}secretRef: ""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value)
                .ToList();

            Assert.NotEmpty(referencedSecrets);
            Assert.Equal(declaredSecrets.Count, declaredSecrets.Distinct(StringComparer.Ordinal).Count());
            var declaredSet = declaredSecrets.ToHashSet(StringComparer.Ordinal);
            var missing = referencedSecrets.Where(r => !declaredSet.Contains(r)).Distinct(StringComparer.Ordinal).ToArray();
            Assert.True(missing.Length == 0, $"ContainerApp '{name}' references undeclared secrets: {string.Join(", ", missing)}");
        }
    }

    // Mirrors an external cloud host invoking `pulumi` with only its control-plane modules (no OS modules) and a partial
    // OS_DEPLOY_SECRETS_JSON: the OS ContainerApp must still declare all OS manifest secrets, foundation-generated
    // postgres-connectionstring, and existing Key Vault integration secrets so Azure never drops secrets referenced
    // by active or rollback revisions (ContainerAppSecretNotFound).
    [Fact]
    public void SynthesizeWithSubsetModulesPreservesAllOperatingSystemSecrets()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true) with
        {
            Postgres = new PostgresConfig { Enabled = true, Databases = ["intochat"] },
        };

        var providedSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["postgres-admin-password"] = "generated-admin-pass",
        };
        var existingVaultSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "openrouter-apikey",
        };

        var plan = PulumiStackSynthesizer.Synthesize(config, [], providedSecrets, existingVaultSecrets);
        var osBlock = ResourceBlock(plan.PulumiYaml, $"container-app-{config.OsName}");
        var declaredSecrets = System.Text.RegularExpressions.Regex
            .Matches(osBlock, @"^\s{10}- name: ""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var referencedSecrets = System.Text.RegularExpressions.Regex
            .Matches(osBlock, @"^\s{16}secretRef: ""([^""]+)""", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        string[] expectedOsSecrets =
        [
            "openrouter-apikey",
            "postgres-connectionstring",
            "clickhouse-connection-string",
            "github-client-secret",
            "github-private-key-pem",
            "github-webhook-secret",
            "google-auth-client-id",
            "google-auth-client-secret",
            "salesforce-consumer-key",
            "salesforce-consumer-secret",
        ];

        foreach (var secretName in expectedOsSecrets)
        {
            Assert.Contains(secretName, declaredSecrets);
            Assert.Contains(secretName, referencedSecrets);
        }
        Assert.DoesNotContain("postgres-connection-string", declaredSecrets);
        Assert.DoesNotContain("postgres-admin-connection", declaredSecrets);
        Assert.DoesNotContain("db-connection", declaredSecrets);

        // Existing Key Vault secret without a provided value must stay external (no Key Vault overwrite resource).
        Assert.DoesNotContain("secretName: \"openrouter-apikey\"", plan.PulumiYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePulumiStack_StopAndStartCommandsRetryTransientAzErrorsAndGuardRevisions()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        foreach (var name in new[] { config.OsName, config.CloudName })
        {
            var stopBlock = ResourceBlock(yaml, $"container-app-{name}-stop");
            Assert.Contains("az_retry()", stopBlock, StringComparison.Ordinal);
            Assert.Contains("-le 5", stopBlock, StringComparison.Ordinal);
            Assert.Contains("sleep $((_az_attempt * 2))", stopBlock, StringComparison.Ordinal);
            Assert.Contains("*RevisionAlreadyInRequestedState*", stopBlock, StringComparison.Ordinal);
            Assert.Contains("*ResourceNotFound*|*ResourceGroupNotFound*", stopBlock, StringComparison.Ordinal);
            Assert.Contains("properties.configuration.activeRevisionsMode", stopBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision set-mode -n \"$app\" -g \"$group\" --mode multiple >/dev/null", stopBlock, StringComparison.Ordinal);
            Assert.True(
                stopBlock.IndexOf("revision set-mode", StringComparison.Ordinal) < stopBlock.IndexOf("revision list -n \"$app\"", StringComparison.Ordinal),
                "Single -> Multiple revision mode transition must run before listing/deactivating active revisions.");
            Assert.Contains("if [ -n \"$revision\" ]; then", stopBlock, StringComparison.Ordinal);
            // Single-quoted JMESPath so /bin/sh never executes `0` as command substitution, with --all so inactive draining revisions are included.
            Assert.Contains("az containerapp revision list --all -n \"$app\" -g \"$group\" --query '[?properties.replicas > `0`].name' -o tsv", stopBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("--query \"[?properties.replicas > `0`].name\"", stopBlock, StringComparison.Ordinal);
            Assert.Contains("az containerapp replica list -n \"$app\" -g \"$group\" --revision \"$_rev\" --query '[].name' -o tsv", stopBlock, StringComparison.Ordinal);
            Assert.Contains("Timed out waiting for replicas of $app in $group to stop", stopBlock, StringComparison.Ordinal);
            Assert.Contains("return 1 2>/dev/null || exit 1", stopBlock, StringComparison.Ordinal);
            Assert.Contains("sleep 10", stopBlock, StringComparison.Ordinal);
            Assert.True(
                stopBlock.IndexOf("sleep 10", StringComparison.Ordinal) > stopBlock.IndexOf("Timed out waiting for replicas", StringComparison.Ordinal),
                "Post-drain 10s buffer for Orleans Dead write must run after the replica drain loop.");

            var startBlock = ResourceBlock(yaml, $"container-app-{name}-start");
            Assert.Contains("az_retry()", startBlock, StringComparison.Ordinal);
            Assert.Contains("-le 5", startBlock, StringComparison.Ordinal);
            Assert.Contains("sleep $((_az_attempt * 2))", startBlock, StringComparison.Ordinal);
            Assert.Contains("*RevisionAlreadyInRequestedState*", startBlock, StringComparison.Ordinal);
            Assert.Contains("*ResourceNotFound*|*ResourceGroupNotFound*", startBlock, StringComparison.Ordinal);
            Assert.Contains("if [ -z \"$latest\" ]; then", startBlock, StringComparison.Ordinal);
            Assert.Contains("if [ -n \"$ready\" ]; then", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp show -n \"$app\" -g \"$group\" --query properties.latestRevisionName -o tsv", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision show -n \"$app\" -g \"$group\" --revision \"$latest\"", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision activate -n \"$app\" -g \"$group\" --revision \"$latest\"", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision deactivate -n \"$app\" -g \"$group\" --revision \"$latest\"", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp replica list -n \"$app\" -g \"$group\" --revision \"$latest\" --query '[].name' -o tsv", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision activate -n \"$app\" -g \"$group\" --revision \"$ready\"", startBlock, StringComparison.Ordinal);
            Assert.Contains("properties.configuration.ingress", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp ingress traffic set -n \"$app\" -g \"$group\" --revision-weight \"$ready=100\" >/dev/null", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp ingress traffic set -n \"$app\" -g \"$group\" --revision-weight latest=100 >/dev/null", startBlock, StringComparison.Ordinal);
            Assert.Contains("az_retry az containerapp revision deactivate -n \"$app\" -g \"$group\" --revision \"$_rev\" >/dev/null", startBlock, StringComparison.Ordinal);
            Assert.True(
                startBlock.IndexOf("replica list -n \"$app\" -g \"$group\" --revision \"$latest\"", StringComparison.Ordinal) < startBlock.IndexOf("revision activate -n \"$app\" -g \"$group\" --revision \"$ready\"", StringComparison.Ordinal),
                "Failed latest revision must drain to 0 replicas before reactivating $ready.");
            Assert.True(
                startBlock.IndexOf("--revision-weight \"$ready=100\"", StringComparison.Ordinal) > startBlock.IndexOf("revision activate -n \"$app\" -g \"$group\" --revision \"$ready\"", StringComparison.Ordinal),
                "Rollback traffic shift to $ready=100 must run after activating $ready.");
        }
    }

    [Fact]
    public void SingleSiloAppsUseMultipleRevisionModeWithLatestTrafficWhileSandboxStaysSingle()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        foreach (var singleSiloName in new[] { config.OsName, config.CloudName })
        {
            var block = ResourceBlock(yaml, $"container-app-{singleSiloName}");
            Assert.Contains("        activeRevisionsMode: Multiple\n", block, StringComparison.Ordinal);
            Assert.Contains(
                """
                          traffic:
                            - latestRevision: true
                              weight: 100
                """.ReplaceLineEndings("\n"),
                block,
                StringComparison.Ordinal);
        }

        var sandboxBlock = ResourceBlock(yaml, $"container-app-{config.SandboxName}");
        Assert.Contains("        activeRevisionsMode: Single\n", sandboxBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("          traffic:\n", sandboxBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePulumiStack_EmitsContainerAppProbesAndTerminationGracePeriod()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        foreach (var name in new[] { config.OsName, config.CloudName, config.SandboxName })
        {
            var block = ResourceBlock(yaml, $"container-app-{name}");
            Assert.Contains("        terminationGracePeriodSeconds: 60\n", block, StringComparison.Ordinal);
            Assert.Contains(
                """
                            probes:
                              - type: Liveness
                                httpGet:
                                  path: "/alive"
                                  port: 8080
                                  scheme: HTTP
                                periodSeconds: 10
                                failureThreshold: 3
                              - type: Readiness
                                httpGet:
                                  path: "/ready"
                                  port: 8080
                                  scheme: HTTP
                                periodSeconds: 10
                                failureThreshold: 3
                              - type: Startup
                                httpGet:
                                  path: "/health"
                                  port: 8080
                                  scheme: HTTP
                                periodSeconds: 10
                                failureThreshold: 30
                """.ReplaceLineEndings("\n"),
                block,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ResolveTerminationGracePeriodSeconds_EnforcesAtLeastShutdownTimeout()
    {
        var defaultRequired = (int)Math.Ceiling(
            IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownTimeout(null, Orleans.Configuration.GrainCollectionOptions.DEFAULT_DEACTIVATION_TIMEOUT).TotalSeconds);

        var defaultApp = new ContainerAppSpec { Name = "intochat-test-os", StopBeforeUpdate = true };
        Assert.Equal(60, PulumiStackSynthesizer.ResolveTerminationGracePeriodSeconds(defaultApp));
        Assert.True(PulumiStackSynthesizer.ResolveTerminationGracePeriodSeconds(defaultApp) >= defaultRequired);

        var tooShortApp = new ContainerAppSpec { Name = "intochat-test-os", StopBeforeUpdate = true, TerminationGracePeriodSeconds = 15 };
        Assert.Equal(defaultRequired, PulumiStackSynthesizer.ResolveTerminationGracePeriodSeconds(tooShortApp));

        var drainDelay = TimeSpan.FromSeconds(30);
        var expectedWithDrain = (int)Math.Ceiling(
            IntoChat.OS.Kernel.AspireServer.ServerHostingExtensions.ShutdownTimeout(drainDelay, Orleans.Configuration.GrainCollectionOptions.DEFAULT_DEACTIVATION_TIMEOUT).TotalSeconds);
        var drainedApp = new ContainerAppSpec
        {
            Name = "intochat-test-os",
            StopBeforeUpdate = true,
            TerminationGracePeriodSeconds = 60,
            Variables = new Dictionary<string, string>
            {
                [PulumiStackSynthesizer.DrainDelayVariable] = drainDelay.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            },
        };
        Assert.Equal(expectedWithDrain, PulumiStackSynthesizer.ResolveTerminationGracePeriodSeconds(drainedApp));
        Assert.True(expectedWithDrain > 60);
    }

    [Fact]
    public async Task WriteWorkspaceAsyncWritesDeclarativeFilesAndAzRetryScript()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);
        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);

        using var workspace = new DigitalBrain.Testing.TempDirectory("intochat-pulumi-workspace-");
        await PulumiStackSynthesizer.WriteWorkspaceAsync(plan, workspace.Path, TestContext.Current.CancellationToken);

        var pulumiYamlPath = Path.Combine(workspace.Path, "Pulumi.yaml");
        var stackConfigPath = Path.Combine(workspace.Path, "Pulumi.test.yaml");
        var azRetryPath = Path.Combine(workspace.Path, "scripts", "az-retry.sh");

        Assert.True(File.Exists(pulumiYamlPath));
        Assert.True(File.Exists(stackConfigPath));
        Assert.True(File.Exists(azRetryPath));

        Assert.Equal(plan.PulumiYaml, await File.ReadAllTextAsync(pulumiYamlPath, TestContext.Current.CancellationToken));
        Assert.Equal(plan.StackConfigYaml, await File.ReadAllTextAsync(stackConfigPath, TestContext.Current.CancellationToken));

        var scriptBytes = await File.ReadAllBytesAsync(azRetryPath, TestContext.Current.CancellationToken);
        Assert.False(scriptBytes.Contains((byte)'\r'), "Written az-retry.sh must use LF line endings.");
        var scriptText = await File.ReadAllTextAsync(azRetryPath, TestContext.Current.CancellationToken);
        Assert.StartsWith("#!/bin/sh\n", scriptText, StringComparison.Ordinal);
        Assert.Contains("az_retry()", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleSiloAppsEnforceSingleReplicaScaleWhilePreservingSandboxScaleToZero()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);

        var apps = InstallPlan.Build(config).OfType<ContainerAppSpec>().ToDictionary(app => app.Name, StringComparer.Ordinal);
        var osApp = apps[config.OsName];
        var cloudApp = apps[config.CloudName];
        var sandboxApp = apps[config.SandboxName];

        Assert.Equal(1, osApp.MinReplicas);
        Assert.Equal(1, osApp.MaxReplicas);
        Assert.True(osApp.StopBeforeUpdate);

        Assert.Equal(1, cloudApp.MinReplicas);
        Assert.Equal(1, cloudApp.MaxReplicas);
        Assert.True(cloudApp.StopBeforeUpdate);

        Assert.Equal(0, sandboxApp.MinReplicas);
        Assert.Equal(1, sandboxApp.MaxReplicas);
        Assert.False(sandboxApp.StopBeforeUpdate);

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);
        Assert.Equal(1, plan.OsApp.MinReplicas);
        Assert.Equal(1, plan.OsApp.MaxReplicas);

        foreach (var singleSiloName in new[] { config.OsName, config.CloudName })
        {
            var block = ResourceBlock(plan.PulumiYaml, $"container-app-{singleSiloName}");
            Assert.Contains("      template:\n        scale:\n          minReplicas: 1\n          maxReplicas: 1\n", block, StringComparison.Ordinal);
            Assert.DoesNotContain("- template.scale", block, StringComparison.Ordinal);
            Assert.DoesNotContain("- template\n", block, StringComparison.Ordinal);
        }

        var sandboxBlock = ResourceBlock(plan.PulumiYaml, $"container-app-{config.SandboxName}");
        Assert.Contains("      template:\n        scale:\n          minReplicas: 0\n          maxReplicas: 1\n", sandboxBlock, StringComparison.Ordinal);

        Assert.Contains("  osMinReplicas:\n    value: 1\n", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("  osMaxReplicas:\n    value: 1\n", plan.PulumiYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveScaleAndEnforceSingleSiloScaleClampSingleSiloOverridesAndPreserveStatelessApps()
    {
        var singleSiloOverride = new ContainerAppSpec
        {
            Name = "intochat-test-os",
            MinReplicas = 0,
            MaxReplicas = 10,
            StopBeforeUpdate = true,
        };
        var statelessApp = new ContainerAppSpec
        {
            Name = "intochat-test-sandbox",
            MinReplicas = 0,
            MaxReplicas = 5,
            StopBeforeUpdate = false,
        };

        Assert.Equal((1, 1), PulumiStackSynthesizer.ResolveScale(singleSiloOverride));
        var enforced = PulumiStackSynthesizer.EnforceSingleSiloScale(singleSiloOverride);
        Assert.Equal(1, enforced.MinReplicas);
        Assert.Equal(1, enforced.MaxReplicas);

        Assert.Equal((0, 5), PulumiStackSynthesizer.ResolveScale(statelessApp));
        var preserved = PulumiStackSynthesizer.EnforceSingleSiloScale(statelessApp);
        Assert.Equal(0, preserved.MinReplicas);
        Assert.Equal(5, preserved.MaxReplicas);
    }

    [Fact]
    public async Task PulumiYamlEscapesEmbeddedShellParameterExpansionsWhileKeepingAzRetryScriptUnescaped()
    {
        Assert.Equal(
            """_x="$${_x}$${_x:+ }$rev"; echo "$(cmd)" $((i+1)) "$${_a}/$${_b}" """,
            PulumiAssets.EscapePulumiYaml("""_x="${_x}${_x:+ }$rev"; echo "$(cmd)" $((i+1)) "${_a}/${_b}" """));

        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true);
        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);

        // Every command:local:Command create script block must escape POSIX ${...} as $${...} so Pulumi YAML
        // never tries to resolve shell variables (e.g. _deactivated_revs) as Pulumi resources/config values.
        var unescapedShellExpr = new System.Text.RegularExpressions.Regex(@"(?<!\$)\$\{", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var commandBlocks = new[]
        {
            ResourceBlock(plan.PulumiYaml, $"container-app-{config.OsName}-stop"),
            ResourceBlock(plan.PulumiYaml, $"container-app-{config.OsName}-start"),
            ResourceBlock(plan.PulumiYaml, $"container-app-{config.CloudName}-stop"),
            ResourceBlock(plan.PulumiYaml, $"container-app-{config.CloudName}-start"),
        };

        foreach (var block in commandBlocks)
        {
            var createIndex = block.IndexOf("      create: |\n", StringComparison.Ordinal);
            Assert.True(createIndex >= 0, $"Expected 'create: |' script block in:\n{block}");
            var scriptSection = block[(createIndex + "      create: |\n".Length)..];
            var match = unescapedShellExpr.Match(scriptSection);
            Assert.False(match.Success, $"Found unescaped Pulumi YAML interpolation '{match.Value}' in command block:\n{block}");
        }

        Assert.Contains("$${_deactivated_revs}", commandBlocks[0], StringComparison.Ordinal);
        Assert.Contains("$${_nonzero_list:-none}", commandBlocks[0], StringComparison.Ordinal);
        Assert.Contains("$${_live_replicas:-none}", commandBlocks[0], StringComparison.Ordinal);
        Assert.Contains("$${_rev}/$${_rep}", commandBlocks[0], StringComparison.Ordinal);

        // Standalone scripts/az-retry.sh must remain unescaped POSIX /bin/sh.
        using var workspace = new DigitalBrain.Testing.TempDirectory("intochat-pulumi-escape-");
        await PulumiStackSynthesizer.WriteWorkspaceAsync(plan, workspace.Path, TestContext.Current.CancellationToken);
        var azRetry = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "scripts", "az-retry.sh"), TestContext.Current.CancellationToken);
        Assert.Contains("${_deactivated_revs}", azRetry, StringComparison.Ordinal);
        Assert.DoesNotContain("$${_deactivated_revs}", azRetry, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundryDisabledEmitsNoFoundryResourcesRolesOrEndpointVariable()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2");

        Assert.False(config.Foundry.Enabled);
        var specs = InstallPlan.Build(config);
        Assert.DoesNotContain(specs, static s => s is FoundrySpec or FoundryDeploymentSpec);
        Assert.DoesNotContain(specs.OfType<RoleGrantSpec>(), static g =>
            g.RoleDefinitionId is InstallPlan.CognitiveServicesOpenAIUser or InstallPlan.CognitiveServicesUser);

        var osApp = specs.OfType<ContainerAppSpec>().Single(s => s.Name == config.OsName);
        Assert.DoesNotContain(InstallPlan.FoundryEndpointVariable, osApp.Variables.Keys);

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);
        Assert.DoesNotContain("azure-native:cognitiveservices:Account", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-native:cognitiveservices:Deployment", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.DoesNotContain(InstallPlan.FoundryEndpointVariable, plan.PulumiYaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FoundryEnabledSynthesizesAccountDeploymentsRbacAndEndpointVariable()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2") with
        {
            Foundry = new FoundryConfig
            {
                Enabled = true,
                Sku = "S0",
                Deployments =
                [
                    new FoundryDeploymentConfig { Name = "DeepSeek-V3.2", ModelName = "DeepSeek-V3.2", ModelFormat = "DeepSeek", SkuName = "GlobalStandard", Capacity = 10 },
                    new FoundryDeploymentConfig { Name = "gpt-realtime", ModelName = "gpt-realtime", ModelFormat = "OpenAI", SkuName = "GlobalStandard", Capacity = 5 },
                    new FoundryDeploymentConfig { Name = "gpt-4o-transcribe", ModelName = "gpt-4o-transcribe", ModelFormat = "OpenAI", SkuName = "GlobalStandard", Capacity = 5 },
                    new FoundryDeploymentConfig { Name = "gpt-4o-mini-tts", ModelName = "gpt-4o-mini-tts", ModelFormat = "OpenAI", SkuName = "GlobalStandard", Capacity = 5 },
                    new FoundryDeploymentConfig { Name = "text-embedding-3-small", ModelName = "text-embedding-3-small", ModelFormat = "OpenAI", SkuName = "GlobalStandard", Capacity = 20 },
                ],
            },
        };

        var specs = InstallPlan.Build(config);
        var foundry = Assert.Single(specs.OfType<FoundrySpec>());
        Assert.Equal("intochat-test-foundry", foundry.Name);
        Assert.Equal("S0", foundry.Sku);

        var deployments = specs.OfType<FoundryDeploymentSpec>().ToArray();
        Assert.Equal(5, deployments.Length);
        Assert.Contains(deployments, static d => d.Name == "DeepSeek-V3.2" && d.ModelFormat == "DeepSeek");
        Assert.Contains(deployments, static d => d.Name == "gpt-realtime" && d.ModelFormat == "OpenAI");
        Assert.Contains(deployments, static d => d.Name == "text-embedding-3-small");

        var grants = specs.OfType<RoleGrantSpec>().ToArray();
        Assert.Contains(grants, g => g.RoleDefinitionId == InstallPlan.CognitiveServicesOpenAIUser
            && g.Principal == config.IdentityName
            && g.Scope.EndsWith("/Microsoft.CognitiveServices/accounts/intochat-test-foundry", StringComparison.Ordinal));
        Assert.Contains(grants, g => g.RoleDefinitionId == InstallPlan.CognitiveServicesUser
            && g.Principal == config.IdentityName
            && g.Scope.EndsWith("/Microsoft.CognitiveServices/accounts/intochat-test-foundry", StringComparison.Ordinal));

        var osApp = specs.OfType<ContainerAppSpec>().Single(s => s.Name == config.OsName);
        Assert.Equal("https://intochat-test-foundry.services.ai.azure.com/", osApp.Variables[InstallPlan.FoundryEndpointVariable]);

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);
        Assert.Contains("type: azure-native:cognitiveservices:Account", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("kind: AIServices", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("customSubDomainName: \"intochat-test-foundry\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("disableLocalAuth: true", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("type: azure-native:cognitiveservices:Deployment", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("deploymentName: \"DeepSeek-V3.2\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("deploymentName: \"gpt-realtime\"", plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("deploymentName: \"text-embedding-3-small\"", plan.PulumiYaml, StringComparison.Ordinal);

        // Foundry deployments must be sequentially chained via dependsOn to prevent Azure ARM HTTP 409 AccountOperationInProgress.
        var firstBlock = ResourceBlock(plan.PulumiYaml, "foundry-deployment-deepseek-v3-2");
        Assert.Contains("dependsOn: [\"${foundry-intochat-test-foundry}\"]", firstBlock, StringComparison.Ordinal);
        var secondBlock = ResourceBlock(plan.PulumiYaml, "foundry-deployment-gpt-realtime");
        Assert.Contains("dependsOn: [\"${foundry-deployment-deepseek-v3-2}\"]", secondBlock, StringComparison.Ordinal);
        var thirdBlock = ResourceBlock(plan.PulumiYaml, "foundry-deployment-gpt-4o-transcribe");
        Assert.Contains("dependsOn: [\"${foundry-deployment-gpt-realtime}\"]", thirdBlock, StringComparison.Ordinal);
        var fourthBlock = ResourceBlock(plan.PulumiYaml, "foundry-deployment-gpt-4o-mini-tts");
        Assert.Contains("dependsOn: [\"${foundry-deployment-gpt-4o-transcribe}\"]", fourthBlock, StringComparison.Ordinal);
        var fifthBlock = ResourceBlock(plan.PulumiYaml, "foundry-deployment-text-embedding-3-small");
        Assert.Contains("dependsOn: [\"${foundry-deployment-gpt-4o-mini-tts}\"]", fifthBlock, StringComparison.Ordinal);

        Assert.Contains(InstallPlan.CognitiveServicesOpenAIUser, plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains(InstallPlan.CognitiveServicesUser, plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains(InstallPlan.FoundryEndpointVariable, plan.PulumiYaml, StringComparison.Ordinal);
        Assert.Contains("https://intochat-test-foundry.services.ai.azure.com/", plan.PulumiYaml, StringComparison.Ordinal);

        var importJson = PulumiStackSynthesizer.ExportImportEntries(plan).ToJsonString();
        Assert.Contains("azure-native:cognitiveservices:Account", importJson, StringComparison.Ordinal);
        Assert.Contains("azure-native:cognitiveservices:Deployment", importJson, StringComparison.Ordinal);
    }

    [Fact]
    public void OsAppSetsSandboxKernelCallbackUrlFromAuthPublicBaseUrl()
    {
        var withPublicBase = InstallConfig.Parse("""
            {
              "subscription": "00000000-0000-0000-0000-000000000001",
              "location": "westeurope",
              "prefix": "intochat",
              "env": "test",
              "osImage": "ghcr.io/intochat/os:1.0.0",
              "sandboxImage": "ghcr.io/intochat/sandbox:1.0.0",
              "registryServer": "intochat.azurecr.io",
              "modules": {
                "auth": {
                  "PublicBaseUrl": "https://os.test.intochat.io/edge/ignored-path"
                }
              }
            }
            """);

        var osAppWithPublic = InstallPlan.OsApp(withPublicBase, new Dictionary<string, string>(), []);
        Assert.Equal("https://os.test.intochat.io", osAppWithPublic.Variables[InstallPlan.SandboxKernelCallbackUrlVariable]);
        Assert.Equal("http://intochat-test-sandbox", osAppWithPublic.Variables[InstallPlan.SandboxUrlVariable]);

        var withoutPublicBase = InstallConfig.Parse(ConfigJson);
        var osAppWithoutPublic = InstallPlan.OsApp(withoutPublicBase, new Dictionary<string, string>(), []);
        Assert.False(osAppWithoutPublic.Variables.ContainsKey(InstallPlan.SandboxKernelCallbackUrlVariable));
    }

    private static string ResourceBlock(string yaml, string logicalName)
    {
        var start = yaml.IndexOf("\n  " + logicalName + ":\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Resource '{logicalName}' not found in synthesized Pulumi YAML.");
        var next = System.Text.RegularExpressions.Regex.Match(yaml[(start + 1)..], @"\n  \S|\n\S");
        return next.Success ? yaml[start..(start + 1 + next.Index)] : yaml[start..];
    }
}
