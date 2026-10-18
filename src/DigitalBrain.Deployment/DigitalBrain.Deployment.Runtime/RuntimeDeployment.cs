using System.Globalization;
using DigitalBrain.Framework;
using DigitalBrain.Framework.Configuration;
using DigitalBrain.Microsoft.Orleans;
using Pulumi;
using Pulumi.AzureNative.App;
using Pulumi.AzureNative.App.Inputs;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.Storage;
using ProbeType = Pulumi.AzureNative.App.Type;
using AppManagedServiceIdentityType = Pulumi.AzureNative.App.ManagedServiceIdentityType;
using KeyVaultSecret = Pulumi.AzureNative.KeyVault.Secret;
using KeyVaultSecretArgs = Pulumi.AzureNative.KeyVault.SecretArgs;
using KeyVaultSecretPropertiesArgs = Pulumi.AzureNative.KeyVault.Inputs.SecretPropertiesArgs;

namespace DigitalBrain.Deployment.Runtime;

public sealed record RuntimeDeployment(ContainerApp App, IReadOnlyList<Resource> Grants);

// The OS silo inside a foundation another program owns: the runtime identity's grants, its Key Vault secrets,
// the brain log's Azure Files volume, the Blob lease container and the one Container App. It creates no
// resource group, network, environment, identity, vault, registry or storage account.
public static class DigitalBrainRuntimeDeployment
{
    public const int HttpPort = 8080;
    // The Orleans gateway, for clients such as DigitalBrain.Mcp. The silo port is deliberately not mapped:
    // a Container Apps port mapping is load-balanced, while silos must reach each other's own addresses.
    public const int GatewayPort = 30000;
    public const string MasterKeySecret = "master-key";

    private const string KeyVaultSecretsUser = "4633458b-17de-408a-b874-0445c86b69e6";
    private const string StorageBlobDataContributor = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string StorageTableDataContributor = "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3";
    private const string AcrPull = "7f951dda-4ed3-4680-a7ca-43fe172d538d";
    private const string BrainLogVolume = "brainlog";
    private const string ClientIdVariable = "AZURE_CLIENT_ID";

    public static RuntimeDeployment DeployRuntime(DeploymentFoundation foundation, RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(foundation);
        Validate(options);
        var prefix = foundation.NamingPrefix;
        var appName = options.AppName ?? prefix + "-os";
        var durable = options.StorageMode == RuntimeStorageMode.Durable;
        var leased = durable && options.Ownership == RuntimeOwnership.BlobLease;

        var grants = new List<Resource>
        {
            Grant(foundation, "vault-secrets", KeyVaultSecretsUser, foundation.KeyVaultId),
            Grant(foundation, "storage-blobs", StorageBlobDataContributor, foundation.StorageAccountId),
            Grant(foundation, "storage-tables", StorageTableDataContributor, foundation.StorageAccountId),
            Grant(foundation, "registry-pull", AcrPull, foundation.RegistryId),
        };

        var variables = new List<EnvironmentVarArgs>();
        var secrets = new List<SecretArgs>();
        foreach (var (name, value) in Environment(foundation, options, durable, leased).Concat(options.Settings))
        {
            variables.Add(new EnvironmentVarArgs { Name = name, Value = value });
        }
        foreach (var (name, value) in options.Secrets.Prepend(new(DigitalBrainNames.MasterKeyEnvironmentVariable, options.MasterKey)))
        {
            var secretName = name == DigitalBrainNames.MasterKeyEnvironmentVariable ? MasterKeySecret : SecretName(name);
            var resourceOptions = secretName == MasterKeySecret
                // Losing the master key loses the brain: never replace or delete it from a deployment.
                ? new CustomResourceOptions { Protect = true, IgnoreChanges = ["properties.value"] }
                : null;
            var vaultSecret = new KeyVaultSecret(prefix + "-" + secretName, new KeyVaultSecretArgs
            {
                SecretName = secretName,
                VaultName = foundation.KeyVaultName,
                ResourceGroupName = foundation.ResourceGroupName,
                Properties = new KeyVaultSecretPropertiesArgs { Value = Output.CreateSecret(value.ToOutput()) },
            }, resourceOptions);
            secrets.Add(new SecretArgs { Name = secretName, KeyVaultUrl = vaultSecret.Properties.Apply(properties => properties.SecretUri), Identity = foundation.RuntimeIdentityId });
            variables.Add(new EnvironmentVarArgs { Name = name, SecretRef = secretName });
        }

        var volumes = new InputList<VolumeArgs>();
        var mounts = new InputList<VolumeMountArgs>();
        var dependencies = new List<Resource>(grants);
        if (durable)
        {
            var share = new Pulumi.AzureNative.Storage.FileShare(prefix + "-" + BrainLogShare(options), new FileShareArgs
            {
                AccountName = foundation.StorageAccountName,
                ResourceGroupName = foundation.ResourceGroupName,
                ShareName = BrainLogShare(options),
                ShareQuota = options.BrainLogQuotaGb,
            });
            var storage = new ManagedEnvironmentsStorage(prefix + "-" + BrainLogShare(options), new ManagedEnvironmentsStorageArgs
            {
                EnvironmentName = foundation.EnvironmentName,
                ResourceGroupName = foundation.ResourceGroupName,
                StorageName = BrainLogShare(options),
                Properties = new ManagedEnvironmentStoragePropertiesArgs
                {
                    AzureFile = new AzureFilePropertiesArgs
                    {
                        AccountName = foundation.StorageAccountName,
                        AccountKey = foundation.StorageAccountKey,
                        ShareName = BrainLogShare(options),
                        AccessMode = AccessMode.ReadWrite,
                    },
                },
            }, new CustomResourceOptions { DependsOn = [share] });
            volumes.Add(new VolumeArgs { Name = BrainLogVolume, StorageName = BrainLogShare(options), StorageType = StorageType.AzureFile, MountOptions = MountOptions(options) });
            mounts.Add(new VolumeMountArgs { VolumeName = BrainLogVolume, MountPath = BrainLogDirectory(options) });
            dependencies.Add(storage);
        }
        if (leased)
        {
            dependencies.Add(new BlobContainer(prefix + "-" + LeaseContainer(options), new BlobContainerArgs
            {
                AccountName = foundation.StorageAccountName,
                ResourceGroupName = foundation.ResourceGroupName,
                ContainerName = LeaseContainer(options),
                PublicAccess = PublicAccess.None,
            }));
        }

        var app = new ContainerApp(appName, new ContainerAppArgs
        {
            ContainerAppName = appName,
            ResourceGroupName = foundation.ResourceGroupName,
            EnvironmentId = foundation.EnvironmentId,
            WorkloadProfileName = "Consumption",
            Identity = new ManagedServiceIdentityArgs
            {
                Type = AppManagedServiceIdentityType.UserAssigned,
                UserAssignedIdentities = [foundation.RuntimeIdentityId],
            },
            Configuration = new ConfigurationArgs
            {
                // The old revision is deactivated only once the new one is ready; lease ownership covers the overlap.
                ActiveRevisionsMode = ActiveRevisionsMode.Single,
                Ingress = new IngressArgs
                {
                    External = true,
                    TargetPort = HttpPort,
                    Transport = "auto",
                    AdditionalPortMappings = [new IngressPortMappingArgs { External = false, TargetPort = GatewayPort, ExposedPort = GatewayPort }],
                },
                Secrets = secrets,
                Registries = [new RegistryCredentialsArgs { Server = foundation.RegistryLoginServer, Identity = foundation.RuntimeIdentityId }],
            },
            Template = new TemplateArgs
            {
                TerminationGracePeriodSeconds = options.TerminationGracePeriodSeconds,
                Containers =
                [
                    new ContainerArgs
                    {
                        Name = "os",
                        Image = options.Image,
                        Env = variables,
                        Resources = new ContainerResourcesArgs { Cpu = options.Cpu, Memory = options.Memory },
                        VolumeMounts = mounts,
                        Probes =
                        [
                            Probe(ProbeType.Liveness, options, failureThreshold: 3, periodSeconds: 10),
                            // Readiness never touches a brain: claims stay lazy, so a new revision is ready while the old one drains.
                            Probe(ProbeType.Readiness, options, failureThreshold: 3, periodSeconds: 10),
                            Probe(ProbeType.Startup, options, failureThreshold: 30, periodSeconds: 10),
                        ],
                    },
                ],
                Volumes = volumes,
                Scale = new ScaleArgs { MinReplicas = options.MinReplicas, MaxReplicas = options.MaxReplicas },
            },
        }, new CustomResourceOptions { DependsOn = [.. dependencies] });
        return new RuntimeDeployment(app, grants);
    }

    // What the platform passes cannot reach into what the deployment owns, and never configures the hosted
    // runtime: HostedStorage refuses to start beside the Orleans grain state this deployment sets.
    public static void Validate(RuntimeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Image);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ClusterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.StorageFormat);
        if (options.MinReplicas < 1 || options.MaxReplicas < options.MinReplicas)
        {
            throw new InvalidOperationException("The OS silo runs at least one replica, and MaxReplicas is not below MinReplicas.");
        }
        var hosting = DigitalBrainConfiguration.EnvironmentName(DigitalBrainNames.HostingConfigurationKey) + "__";
        var supplied = SuppliedNames();
        var names = options.Settings.Keys.Concat(options.Secrets.Keys).Select(EnvironmentForm).ToArray();
        var hosted = names.Where(name => name.StartsWith(hosting, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosted.Length > 0)
        {
            throw new InvalidOperationException($"The OS runtime must not carry HostedRuntimeOptions ({DigitalBrainNames.HostingConfigurationKey}); remove: {string.Join(", ", hosted)}.");
        }
        var overriding = names.Where(supplied.Contains).Concat(names.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key)).ToArray();
        if (overriding.Length > 0)
        {
            throw new InvalidOperationException($"These settings are supplied by DeployRuntime or given twice: {string.Join(", ", overriding)}.");
        }
    }

    // Everything the silo needs from the foundation, in the environment form the configuration provider reads.
    private static IEnumerable<KeyValuePair<string, Input<string>>> Environment(DeploymentFoundation foundation, RuntimeOptions options, bool durable, bool leased)
    {
        yield return new("ASPNETCORE_ENVIRONMENT", "Production");
        // DefaultAzureCredential picks the user-assigned identity by this client id (Orleans stores, lease, vault).
        yield return new(ClientIdVariable, foundation.RuntimeIdentityClientId);
        yield return new(Variable(DigitalBrainNames.KeyVaultUriConfigurationKey), foundation.KeyVaultUri);
        yield return new("Orleans__ClusterId", options.ClusterId);
        yield return new("Orleans__ServiceId", options.ServiceId);
        yield return new(Variable(DigitalBrainNames.BrainStorageDirectoryConfigurationKey), BrainLogDirectory(options));
        yield return new(Variable(DigitalBrainNames.ConfiguredCustodyAcknowledgedConfigurationKey), "true");
        if (durable)
        {
            // https service URIs select token auth with the runtime identity (os#197).
            var tables = foundation.StorageAccountName.Apply(account => $"https://{account}.table.core.windows.net/");
            var blobs = foundation.StorageAccountName.Apply(account => $"https://{account}.blob.core.windows.net/");
            yield return new(Integration(OrleansStorageHosting.ClusteringField), tables);
            yield return new(Integration(OrleansStorageHosting.RemindersField), tables);
            yield return new(Integration(OrleansStorageHosting.GrainStateField), blobs);
            yield return new(Integration(OrleansStorageHosting.GrainStateContainerField), "grainstate-" + options.StorageFormat);
        }
        if (leased)
        {
            yield return new(Variable(DigitalBrainNames.BrainStorageOwnershipKindConfigurationKey), DigitalBrainNames.BrainStorageOwnershipBlobLease);
            yield return new(Variable(DigitalBrainNames.BrainStorageOwnershipContainerUriConfigurationKey),
                foundation.StorageAccountName.Apply(account => $"https://{account}.blob.core.windows.net/{LeaseContainer(options)}"));
        }
    }

    private static HashSet<string> SuppliedNames()
    {
        var everything = new RuntimeOptions { Image = "-", ClusterId = "-", ServiceId = "-", MasterKey = "-" };
        var foundation = new DeploymentFoundation("-", "-", Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"),
            Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"), Output.Create("-"));
        return [.. Environment(foundation, everything, durable: true, leased: true).Select(pair => pair.Key), DigitalBrainNames.MasterKeyEnvironmentVariable];
    }

    private static ContainerAppProbeArgs Probe(ProbeType type, RuntimeOptions options, int failureThreshold, int periodSeconds) => new()
    {
        Type = type,
        HttpGet = new ContainerAppProbeHttpGetArgs { Path = options.HealthPath, Port = HttpPort },
        FailureThreshold = failureThreshold,
        PeriodSeconds = periodSeconds,
    };

    private static RoleAssignment Grant(DeploymentFoundation foundation, string name, string roleDefinitionId, Input<string> scope)
        => new(foundation.NamingPrefix + "-os-" + name, new RoleAssignmentArgs
        {
            PrincipalId = foundation.RuntimeIdentityPrincipalId,
            PrincipalType = PrincipalType.ServicePrincipal,
            RoleDefinitionId = $"/subscriptions/{foundation.SubscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{roleDefinitionId}",
            Scope = scope,
        });

    // nobrl is deliberately absent: lease ownership does not need it, and byte-range locks keep writer.lock as a second guard.
    internal static string MountOptions(RuntimeOptions options)
        => string.Create(CultureInfo.InvariantCulture, $"dir_mode=0700,file_mode=0600,uid={options.AppUid},gid={options.AppGid},mfsymlinks,cache=strict,actimeo=30,nosharesock");

    private static string BrainLogShare(RuntimeOptions options) => "brainlog-" + options.StorageFormat;

    private static string LeaseContainer(RuntimeOptions options) => "brainlease-" + options.StorageFormat;

    private static string BrainLogDirectory(RuntimeOptions options) => "/data/brain/" + options.StorageFormat;

    private static string Variable(string configurationKey) => DigitalBrainConfiguration.EnvironmentName(configurationKey);

    private static string Integration(string field) => Variable(OrleansModule.Integration.Key(field));

    private static string EnvironmentForm(string name) => name.Contains(':', StringComparison.Ordinal) ? Variable(name) : name;

    // Container Apps secret names are lowercase letters, digits and dashes.
    private static string SecretName(string variable) => variable.ToLowerInvariant().Replace('_', '-').Replace(':', '-');
}
