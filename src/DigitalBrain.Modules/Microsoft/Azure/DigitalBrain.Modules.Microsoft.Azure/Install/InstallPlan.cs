using System.Globalization;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;

namespace DigitalBrain.Microsoft.Azure.Install;

// The ordered specs of the OS foundation (the OS silo, its storage, vault and identity):
// resource group, identity, logs, storage, vault, environment, grants, secret, then the OS app.
public static class InstallPlan
{
    public const string MasterKeySecret = "master-key";
    public const string BrainLogShare = "brainlog";
    public const string Owner = "os";
    public const int HttpPort = 8080;
    // The Orleans gateway. The silo port is deliberately not mapped: a Container Apps port mapping is
    // load-balanced, while silos must reach each other's own addresses.
    public const int GatewayPort = 30000;

    private const string KeyVaultSecretsUser = "4633458b-17de-408a-b874-0445c86b69e6";
    private const string StorageBlobDataContributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string StorageTableDataContributor = "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3";
    private const string BrainLogVolume = "brainlog";
    private const int AppUid = 1654;
    private const int AppGid = 1654;

    public static ResourceSpec[] Build(InstallConfig config) => Build(config, new Dictionary<string, string>(), []);

    // Everything the OS app runs on: its resources, without the app itself.
    public static ResourceSpec[] Foundation(InstallConfig config) => Build(config)[..^1];

    // The OS app, with the variables and secrets the module deployments asked for.
    public static ContainerAppSpec OsApp(InstallConfig config, IReadOnlyDictionary<string, string> variables, IReadOnlyList<SecretRef> secrets) =>
        (ContainerAppSpec)Build(config, variables, secrets)[^1];

    public static FoundationRefs Refs(InstallConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var c = config.Validated();
        var group = ResourceNames.ResourceGroup(c.Prefix, c.Env);
        ResourceRef Ref(string kind, string name) => new(ResourceIds.For(c.Subscription, kind, group, name), kind, name, group);
        return new FoundationRefs(
            Ref(ResourceKinds.ResourceGroup, group),
            Ref(ResourceKinds.Identity, ResourceNames.Identity(c.Prefix, c.Env)),
            Ref(ResourceKinds.ManagedEnvironment, ResourceNames.Environment(c.Prefix, c.Env)),
            Ref(ResourceKinds.StorageAccount, ResourceNames.StorageAccount(c.Prefix, c.Env)),
            Ref(ResourceKinds.KeyVault, ResourceNames.KeyVault(c.Prefix, c.Env)),
            c.RegistryServer);
    }

    private static ResourceSpec[] Build(InstallConfig config, IReadOnlyDictionary<string, string> extraVariables, IReadOnlyList<SecretRef> extraSecrets)
    {
        ArgumentNullException.ThrowIfNull(config);
        var c = config.Validated();
        var group = ResourceNames.ResourceGroup(c.Prefix, c.Env);
        var identity = ResourceNames.Identity(c.Prefix, c.Env);
        var workspace = ResourceNames.LogWorkspace(c.Prefix, c.Env);
        var account = ResourceNames.StorageAccount(c.Prefix, c.Env);
        var vault = ResourceNames.KeyVault(c.Prefix, c.Env);
        var environment = ResourceNames.Environment(c.Prefix, c.Env);
        var app = ResourceNames.App(c.Prefix, c.Env, "os");
        var grainState = "grainstate-" + c.StorageFormat;
        var lease = "brainlease-" + c.StorageFormat;
        var scopePrefix = $"/subscriptions/{c.Subscription}/resourceGroups/{group}/providers";
        var vaultScope = $"{scopePrefix}/Microsoft.KeyVault/vaults/{vault}";
        var accountScope = $"{scopePrefix}/Microsoft.Storage/storageAccounts/{account}";

        T In<T>(T spec) where T : ResourceSpec => spec with { ResourceGroup = group, Location = c.Location, Env = c.Env, Owner = Owner };
        RoleGrantSpec Grant(string name, string role, string scope) =>
            In(new RoleGrantSpec { Name = $"{app}-{name}", Principal = identity, RoleDefinitionId = role, Scope = scope });

        return
        [
            In(new ResourceGroupSpec { Name = group }),
            In(new IdentitySpec { Name = identity }),
            In(new LogWorkspaceSpec { Name = workspace }),
            In(new StorageAccountSpec { Name = account }),
            In(new FileShareSpec { Name = BrainLogShare, StorageAccount = account, QuotaGb = c.StorageQuotaGb }),
            In(new BlobContainerSpec { Name = grainState, StorageAccount = account }),
            In(new BlobContainerSpec { Name = lease, StorageAccount = account }),
            In(new KeyVaultSpec { Name = vault }),
            In(new ManagedEnvironmentSpec
            {
                Name = environment,
                LogWorkspace = workspace,
                Storages = [new EnvironmentStorageSpec { Name = BrainLogShare, StorageAccount = account, ShareName = BrainLogShare }],
            }),
            Grant("vault-secrets", KeyVaultSecretsUser, vaultScope),
            Grant("storage-blobs", StorageBlobDataContributor, accountScope),
            Grant("storage-tables", StorageTableDataContributor, accountScope),
            // Losing the master key loses the brain: a later install never overwrites it.
            In(new SecretSpec { Name = MasterKeySecret, Vault = vault, Overwrite = false }),
            In(new ContainerAppSpec
            {
                Name = app,
                Environment = environment,
                Identity = identity,
                Image = c.OsImage,
                Cpu = c.Cpu,
                Memory = c.Memory,
                IngressPort = HttpPort,
                InternalPorts = [GatewayPort],
                RegistryServer = c.RegistryServer,
                // Liveness is the process alone, readiness the drain, startup waits for dependencies.
                ProbePaths = [HostProbePaths.Alive, HostProbePaths.Ready, HostProbePaths.Health],
                Variables = Variables(c, account, vault, lease, grainState, extraVariables),
                Secrets = [new SecretRef { Name = MasterKeySecret, Vault = vault, VaultSecret = MasterKeySecret, Variable = DigitalBrainNames.MasterKeyEnvironmentVariable }, .. extraSecrets],
                VolumeMounts =
                [
                    new VolumeMountSpec
                    {
                        Volume = BrainLogVolume,
                        EnvironmentStorage = BrainLogShare,
                        MountPath = BrainLogDirectory(c),
                        // nobrl is deliberately absent: lease ownership does not need it.
                        MountOptions = string.Create(CultureInfo.InvariantCulture, $"dir_mode=0700,file_mode=0600,uid={AppUid},gid={AppGid},mfsymlinks,cache=strict,actimeo=30,nosharesock"),
                    },
                ],
            }),
        ];
    }

    public static string DryRunLine(ResourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return $"ensure {spec.Kind} {spec.Name} in {spec.ResourceGroup}";
    }

    private static Dictionary<string, string> Variables(InstallConfig c, string account, string vault, string lease, string grainState, IReadOnlyDictionary<string, string> extra)
    {
        var tables = $"https://{account}.table.core.windows.net/";
        var blobs = $"https://{account}.blob.core.windows.net/";
        var variables = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            // DefaultAzureCredential picks the user-assigned identity by this client id (Orleans stores, lease, vault).
            ["AZURE_CLIENT_ID"] = ContainerAppSpec.IdentityClientIdToken,
            [Variable(DigitalBrainNames.KeyVaultUriConfigurationKey)] = $"https://{vault}.vault.azure.net/",
            ["Orleans__ClusterId"] = c.ClusterId,
            ["Orleans__ServiceId"] = c.ServiceId,
            [Variable(DigitalBrainNames.BrainStorageDirectoryConfigurationKey)] = BrainLogDirectory(c),
            [Variable(DigitalBrainNames.ConfiguredCustodyAcknowledgedConfigurationKey)] = "true",
            // https service URIs select token auth with the runtime identity.
            [Integration(OrleansIntegrationNames.Clustering)] = tables,
            [Integration(OrleansIntegrationNames.Reminders)] = tables,
            [Integration(OrleansIntegrationNames.GrainState)] = blobs,
            [Integration(OrleansIntegrationNames.GrainStateContainer)] = grainState,
            [Variable(DigitalBrainNames.BrainStorageOwnershipKindConfigurationKey)] = DigitalBrainNames.BrainStorageOwnershipBlobLease,
            [Variable(DigitalBrainNames.BrainStorageOwnershipContainerUriConfigurationKey)] = blobs + lease,
        };
        foreach (var (name, value) in extra) { variables[name] = value; }
        return variables;
    }

    private static string BrainLogDirectory(InstallConfig c) => "/data/brain/" + c.StorageFormat;

    private static string Variable(string configurationKey) => ConfigurationKeyGrammar.EnvironmentName(configurationKey);

    private static string Integration(string field) => Variable(ConfigurationKeyGrammar.IntegrationKey(OrleansIntegrationNames.Id, field));
}
