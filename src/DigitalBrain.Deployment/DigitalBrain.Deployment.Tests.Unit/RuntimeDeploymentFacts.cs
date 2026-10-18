using System.Collections.Immutable;
using System.Globalization;
using DigitalBrain.Deployment.Runtime;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;
using Pulumi;
using Pulumi.Testing;

namespace DigitalBrain.Deployment.Tests.Unit;

// DeployRuntime puts the OS silo into a foundation another program owns (W5 ADR A3).
[Collection(nameof(DigitalBrainDeploymentFacts))]
public sealed class RuntimeDeploymentFacts
{
    private const string ClientId = "runtime-client-id";
    private const string Account = "stintochattest1a2b";

    [Fact]
    public async Task CreatesOnlyRuntimeResourcesAndNoSecondFoundation()
    {
        var mocks = await Deploy(Options());

        var types = mocks.Resources.Select(resource => resource.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["azure-native:app:ContainerApp", "azure-native:app:ManagedEnvironmentsStorage", "azure-native:authorization:RoleAssignment",
             "azure-native:keyvault:Secret", "azure-native:storage:BlobContainer", "azure-native:storage:FileShare"],
            types);
        Assert.Single(mocks.Resources, resource => resource.Type == "azure-native:app:ContainerApp");
        Assert.Equal("brainlog-v1", (string)mocks.Resources.Single(resource => resource.Type == "azure-native:storage:FileShare").Inputs["shareName"]);
        Assert.Equal("brainlease-v1", (string)mocks.Resources.Single(resource => resource.Type == "azure-native:storage:BlobContainer").Inputs["containerName"]);
    }

    [Fact]
    public async Task GrantsTheRuntimeIdentityStorageVaultAndRegistryRoles()
    {
        var mocks = await Deploy(Options());

        var grants = mocks.Resources.Where(resource => resource.Type == "azure-native:authorization:RoleAssignment").ToArray();
        Assert.All(grants, grant => Assert.Equal("runtime-principal", (string)grant.Inputs["principalId"]));
        Assert.Equal(
            ["/kv:4633458b-17de-408a-b874-0445c86b69e6", "/registry:7f951dda-4ed3-4680-a7ca-43fe172d538d",
             "/storage:0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3", "/storage:ba92f5b4-2d11-453d-a403-e96b0029c9fe"],
            grants.Select(grant => (string)grant.Inputs["scope"] + ":" + ((string)grant.Inputs["roleDefinitionId"]).Split('/')[^1]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RunsOneSingleRevisionSiloWithOnlyTheGatewayPortMappedInternally()
    {
        var mocks = await Deploy(Options());

        var app = App(mocks);
        Assert.Equal("intochat-test-os", (string)app.Inputs["containerAppName"]);
        var configuration = Map(app.Inputs["configuration"]);
        Assert.Equal("Single", (string)configuration["activeRevisionsMode"]);
        var ingress = Map(configuration["ingress"]);
        Assert.True((bool)ingress["external"]);
        Assert.Equal(8080, Int(ingress["targetPort"]));
        var mapping = Map(Assert.Single(List(ingress["additionalPortMappings"])));
        Assert.False((bool)mapping["external"]);
        Assert.Equal(30000, Int(mapping["targetPort"]));
        Assert.Equal(30000, Int(mapping["exposedPort"]));
        var scale = Map(Map(app.Inputs["template"])["scale"]);
        Assert.Equal(1, Int(scale["minReplicas"]));
        Assert.Equal(1, Int(scale["maxReplicas"]));
    }

    [Fact]
    public async Task ProbesHealthAndDrainsForTheGracePeriod()
    {
        var mocks = await Deploy(Options());

        var template = Map(App(mocks).Inputs["template"]);
        Assert.Equal(60, Int(template["terminationGracePeriodSeconds"]));
        var probes = List(Container(mocks)["probes"]).Select(Map).ToDictionary(probe => (string)probe["type"]);
        Assert.Equal(["Liveness", "Readiness", "Startup"], probes.Keys.Order(StringComparer.Ordinal));
        // Liveness is the process alone and readiness honours the drain; only startup waits for dependencies.
        Assert.Equal("/alive", (string)Map(probes["Liveness"]["httpGet"])["path"]);
        Assert.Equal("/ready", (string)Map(probes["Readiness"]["httpGet"])["path"]);
        Assert.Equal("/health", (string)Map(probes["Startup"]["httpGet"])["path"]);
        Assert.Equal(DigitalBrain.ServiceDefaults.HealthProbes.AlivePath, (string)Map(probes["Liveness"]["httpGet"])["path"]);
        Assert.Equal(DigitalBrain.ServiceDefaults.HealthProbes.ReadyPath, (string)Map(probes["Readiness"]["httpGet"])["path"]);
        Assert.Equal(30, Int(probes["Startup"]["failureThreshold"]));
        Assert.Equal(10, Int(probes["Startup"]["periodSeconds"]));
    }

    [Fact]
    public async Task MountsTheBrainLogShareWithTheDesignedOptions()
    {
        var mocks = await Deploy(Options());

        var storage = mocks.Resources.Single(resource => resource.Type == "azure-native:app:ManagedEnvironmentsStorage");
        var azureFile = Map(Map(storage.Inputs["properties"])["azureFile"]);
        Assert.Equal("brainlog-v1", (string)azureFile["shareName"]);
        Assert.Equal("ReadWrite", (string)azureFile["accessMode"]);
        var volume = Map(Assert.Single(List(Map(App(mocks).Inputs["template"])["volumes"])));
        Assert.Equal("AzureFile", (string)volume["storageType"]);
        Assert.Equal("dir_mode=0700,file_mode=0600,uid=1654,gid=1654,mfsymlinks,cache=strict,actimeo=30,nosharesock", (string)volume["mountOptions"]);
        // Byte-range locks stay on so writer.lock remains a second guard behind the Blob lease.
        Assert.DoesNotContain("nobrl", (string)volume["mountOptions"], StringComparison.Ordinal);
        var mount = Map(Assert.Single(List(Container(mocks)["volumeMounts"])));
        Assert.Equal("/data/brain/v1", (string)mount["mountPath"]);
        Assert.Equal((string)volume["name"], (string)mount["volumeName"]);
    }

    [Fact]
    public async Task SuppliesOrleansStorageOwnershipAndCustodyFromTheFoundation()
    {
        var mocks = await Deploy(Options());

        var variables = Environment(mocks);
        Assert.Equal(ClientId, variables["AZURE_CLIENT_ID"].Value);
        Assert.Equal("intochat-test", variables["Orleans__ClusterId"].Value);
        Assert.Equal("intochat-test", variables["Orleans__ServiceId"].Value);
        Assert.Equal("Production", variables["ASPNETCORE_ENVIRONMENT"].Value);
        Assert.Equal($"https://{Account}.table.core.windows.net/", variables[Integration(OrleansStorageHosting.ClusteringField)].Value);
        Assert.Equal($"https://{Account}.table.core.windows.net/", variables[Integration(OrleansStorageHosting.RemindersField)].Value);
        Assert.Equal($"https://{Account}.blob.core.windows.net/", variables[Integration(OrleansStorageHosting.GrainStateField)].Value);
        Assert.Equal("grainstate-v1", variables[Integration(OrleansStorageHosting.GrainStateContainerField)].Value);
        Assert.Equal("/data/brain/v1", variables[Env(DigitalBrainNames.BrainStorageDirectoryConfigurationKey)].Value);
        Assert.Equal("true", variables[Env(DigitalBrainNames.ConfiguredCustodyAcknowledgedConfigurationKey)].Value);
        Assert.Equal(DigitalBrainNames.BrainStorageOwnershipBlobLease, variables[Env(DigitalBrainNames.BrainStorageOwnershipKindConfigurationKey)].Value);
        Assert.Equal($"https://{Account}.blob.core.windows.net/brainlease-v1", variables[Env(DigitalBrainNames.BrainStorageOwnershipContainerUriConfigurationKey)].Value);
        Assert.Equal("https://kv-intochat-test.vault.azure.net/", variables[Env(DigitalBrainNames.KeyVaultUriConfigurationKey)].Value);
        Assert.Equal("master-key", variables[DigitalBrainNames.MasterKeyEnvironmentVariable].SecretRef);
        Assert.Null(variables[DigitalBrainNames.MasterKeyEnvironmentVariable].Value);
        Assert.DoesNotContain(variables.Keys, name => name.StartsWith(Env(DigitalBrainNames.HostingConfigurationKey), StringComparison.Ordinal));
        Assert.DoesNotContain(variables.Keys, name => name.StartsWith("Orleans__Endpoints__", StringComparison.Ordinal));

        var secret = Map(Assert.Single(List(Map(App(mocks).Inputs["configuration"])["secrets"])));
        Assert.Equal("master-key", (string)secret["name"]);
        Assert.Equal("/identities/runtime", (string)secret["identity"]);
        Assert.Equal("https://vault.vault.azure.net/secrets/intochat-test-master-key", (string)secret["keyVaultUrl"]);
    }

    [Fact]
    public async Task ExtraSecretsGoToKeyVaultAndPlainSettingsPassThrough()
    {
        var mocks = await Deploy(Options() with
        {
            Settings = new Dictionary<string, Input<string>> { ["Provider__ClientId"] = "abc" },
            Secrets = new Dictionary<string, Input<string>> { ["Provider__ApiKey"] = "sk-1" },
        });

        var variables = Environment(mocks);
        Assert.Equal("abc", variables["Provider__ClientId"].Value);
        Assert.Equal("provider--apikey", variables["Provider__ApiKey"].SecretRef);
        Assert.Equal(["intochat-test-master-key", "intochat-test-provider--apikey"],
            mocks.Resources.Where(resource => resource.Type == "azure-native:keyvault:Secret").Select(resource => resource.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RefusesTheBareHostingSection()
        => Assert.Throws<InvalidOperationException>(() => DigitalBrainRuntimeDeployment.Validate(
            Options() with { Settings = new Dictionary<string, Input<string>> { [DigitalBrainNames.HostingConfigurationKey] = "x" } }));

    [Theory]
    [InlineData("RuntimeId")]
    [InlineData("AccountId")]
    public void RefusesHostedRuntimeSettings(string property)
    {
        var environmentForm = Env(DigitalBrainNames.HostingConfigurationKey + ":" + property);
        var configurationForm = DigitalBrainNames.HostingConfigurationKey + ":" + property;

        Assert.Contains("Hosting", Assert.Throws<InvalidOperationException>(() => DigitalBrainRuntimeDeployment.Validate(
            Options() with { Settings = new Dictionary<string, Input<string>> { [environmentForm] = "runtime-1" } })).Message, StringComparison.Ordinal);
        Assert.Contains("Hosting", Assert.Throws<InvalidOperationException>(() => DigitalBrainRuntimeDeployment.Validate(
            Options() with { Secrets = new Dictionary<string, Input<string>> { [configurationForm] = "runtime-1" } })).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RefusesSettingsThatOverrideWhatTheDeploymentSupplies()
    {
        var options = Options() with { Secrets = new Dictionary<string, Input<string>> { ["AZURE_CLIENT_ID"] = "other" } };

        var error = Assert.Throws<InvalidOperationException>(() => DigitalBrainRuntimeDeployment.Validate(options));

        Assert.Contains("AZURE_CLIENT_ID", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryModeLeavesOutDurableStoresAndTheMount()
    {
        var mocks = await Deploy(Options() with { StorageMode = RuntimeStorageMode.Memory });

        Assert.DoesNotContain(mocks.Resources, resource => resource.Type is "azure-native:storage:FileShare" or "azure-native:storage:BlobContainer" or "azure-native:app:ManagedEnvironmentsStorage");
        var variables = Environment(mocks);
        Assert.DoesNotContain(Integration(OrleansStorageHosting.ClusteringField), variables.Keys);
        Assert.DoesNotContain(Integration(OrleansStorageHosting.GrainStateField), variables.Keys);
        Assert.DoesNotContain(Integration(OrleansStorageHosting.RemindersField), variables.Keys);
        Assert.DoesNotContain(Env(DigitalBrainNames.BrainStorageOwnershipKindConfigurationKey), variables.Keys);
        Assert.Equal("true", variables[Env(DigitalBrainNames.ConfiguredCustodyAcknowledgedConfigurationKey)].Value);
        var template = Map(App(mocks).Inputs["template"]);
        Assert.True(!template.TryGetValue("volumes", out var volumes) || List(volumes).IsEmpty);
    }

    [Fact]
    public async Task FileLockOwnershipKeepsTheShareButNotTheLease()
    {
        var mocks = await Deploy(Options() with { Ownership = RuntimeOwnership.FileLock });

        Assert.DoesNotContain(mocks.Resources, resource => resource.Type == "azure-native:storage:BlobContainer");
        Assert.Single(mocks.Resources, resource => resource.Type == "azure-native:storage:FileShare");
        Assert.Equal(DigitalBrainNames.BrainStorageOwnershipWriterLock, Environment(mocks)[Env(DigitalBrainNames.BrainStorageOwnershipKindConfigurationKey)].Value);
        Assert.DoesNotContain(Env(DigitalBrainNames.BrainStorageOwnershipContainerUriConfigurationKey), Environment(mocks).Keys);
    }

    // Platform's Pulumi program takes this package: Pulumi, AzureNative and the dependency-free configuration names, nothing else.
    [Fact]
    public void TheRuntimeAssemblyReferencesOnlyPulumiAndTheConfigurationNames()
    {
        var references = typeof(DigitalBrainRuntimeDeployment).Assembly.GetReferencedAssemblies().Select(name => name.Name!)
            .Where(name => name != "System" && !name.StartsWith("System.", StringComparison.Ordinal) && name != "netstandard" && name != "mscorlib")
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(["DigitalBrain.Contracts.Configuration", "Pulumi", "Pulumi.AzureNative"], references);
        Assert.DoesNotContain(typeof(DigitalBrainNames).Assembly.GetReferencedAssemblies(),
            name => name.Name != "netstandard" && name.Name != "System" && !name.Name!.StartsWith("System.", StringComparison.Ordinal));
    }

    [Fact]
    public void TheConfigurationNamesAreTheOnesTheRuntimeReads()
    {
        Assert.Equal(OrleansStorageHosting.ClusteringField, OrleansIntegrationNames.Clustering);
        Assert.Equal(OrleansModule.Integration.Key(OrleansStorageHosting.GrainStateField), DigitalBrain.Framework.Configuration.ConfigurationKeyGrammar.IntegrationKey(OrleansIntegrationNames.Id, OrleansIntegrationNames.GrainState));
        Assert.Equal(DigitalBrain.ServiceDefaults.HealthProbes.HealthPath, HostProbePaths.Health);
    }

    private static RuntimeOptions Options() => new()
    {
        Image = "acrintochat.azurecr.io/intochat-distribution:1.0.0",
        ClusterId = "intochat-test",
        ServiceId = "intochat-test",
        MasterKey = Output.CreateSecret("master-key-bytes"),
        AppName = "intochat-test-os",
    };

    private static Runtime.DeploymentFoundation Foundation() => new(
        "intochat-test", "00000000-0000-0000-0000-000000000000",
        Output.Create("rg-intochat-test"), Output.Create("/environments/cae-intochat-test"), Output.Create("cae-intochat-test"),
        Output.Create("/kv"), Output.Create("kv-intochat-test"), Output.Create("https://kv-intochat-test.vault.azure.net/"),
        Output.Create("/registry"), Output.Create("acrintochat.azurecr.io"),
        Output.Create("/storage"), Output.Create(Account), Output.CreateSecret("account-key"),
        Output.Create("/identities/runtime"), Output.Create("runtime-principal"), Output.Create(ClientId));

    private static async Task<AzureMocks> Deploy(RuntimeOptions options)
    {
        var mocks = new AzureMocks();
        await Pulumi.Deployment.TestAsync(mocks, new TestOptions { IsPreview = false, ProjectName = "intochat", StackName = "test" },
            () => DigitalBrainRuntimeDeployment.DeployRuntime(Foundation(), options));
        return mocks;
    }

    private static string Env(string key) => DigitalBrainConfiguration.EnvironmentName(key);

    private static string Integration(string field) => Env(OrleansModule.Integration.Key(field));

    private static int Int(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static MockResourceArgs App(AzureMocks mocks) => mocks.Resources.Single(resource => resource.Type == "azure-native:app:ContainerApp");

    private static ImmutableDictionary<string, object> Container(AzureMocks mocks) => Map(List(Map(App(mocks).Inputs["template"])["containers"])[0]);

    private static ImmutableDictionary<string, object> Map(object value) => (ImmutableDictionary<string, object>)value;

    private static ImmutableArray<object> List(object value) => (ImmutableArray<object>)value;

    private static Dictionary<string, (string? Value, string? SecretRef)> Environment(AzureMocks mocks)
        => List(Container(mocks)["env"]).Select(Map).ToDictionary(variable => (string)variable["name"],
            variable => (variable.TryGetValue("value", out var value) ? value as string : null, variable.TryGetValue("secretRef", out var secret) ? secret as string : null));
}
