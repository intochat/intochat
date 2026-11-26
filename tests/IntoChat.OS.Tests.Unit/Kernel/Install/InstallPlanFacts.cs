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
            "postgres-admin-connection",
            "postgres-connection-string",
            "salesforce-consumer-key",
            "salesforce-consumer-secret",
        ];

        foreach (var secretName in expectedOsSecrets)
        {
            Assert.Contains(secretName, declaredSecrets);
            Assert.Contains(secretName, referencedSecrets);
        }

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
            Assert.Contains("if [ -n \"$revision\" ]; then", stopBlock, StringComparison.Ordinal);
            // Single-quoted JMESPath so /bin/sh never executes `0` as command substitution, with --all so inactive draining revisions are included.
            Assert.Contains("az containerapp revision list --all -n \"$app\" -g \"$group\" --query '[?properties.replicas > `0`].name' -o tsv", stopBlock, StringComparison.Ordinal);
            Assert.DoesNotContain("--query \"[?properties.replicas > `0`].name\"", stopBlock, StringComparison.Ordinal);
            Assert.Contains("az containerapp replica list -n \"$app\" -g \"$group\" --revision \"$_rev\" --query '[].name' -o tsv", stopBlock, StringComparison.Ordinal);
            Assert.Contains("Timed out waiting for replicas of $app in $group to stop", stopBlock, StringComparison.Ordinal);
            Assert.Contains("return 1 2>/dev/null || exit 1", stopBlock, StringComparison.Ordinal);

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
            Assert.Contains("az_retry az containerapp revision activate -n \"$app\" -g \"$group\" --revision \"$ready\"", startBlock, StringComparison.Ordinal);
        }
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

    private static string ResourceBlock(string yaml, string logicalName)
    {
        var start = yaml.IndexOf("\n  " + logicalName + ":\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Resource '{logicalName}' not found in synthesized Pulumi YAML.");
        var next = System.Text.RegularExpressions.Regex.Match(yaml[(start + 1)..], @"\n  \S|\n\S");
        return next.Success ? yaml[start..(start + 1 + next.Index)] : yaml[start..];
    }
}
