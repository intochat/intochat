namespace DigitalBrain.Microsoft.Azure;

// A resource the installer or a neuron asks Azure for. Env and Owner become the env and owner tags;
// Tags are merged over managed-by, env and owner by AzureTags.Merge.
[GenerateSerializer, Alias("azure.spec")]
public abstract record ResourceSpec
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string ResourceGroup { get; init; } = "";
    [Id(2)] public string Location { get; init; } = "";
    [Id(3)] public string Env { get; init; } = "";
    [Id(4)] public string Owner { get; init; } = "azure";
    [Id(5)] public Dictionary<string, string> Tags { get; init; } = [];

    public abstract string Kind { get; }
}

[GenerateSerializer, Alias("azure.spec.resource-group")]
public sealed record ResourceGroupSpec : ResourceSpec
{
    public override string Kind => ResourceKinds.ResourceGroup;
}

// A user-assigned managed identity. Containers reference it by name.
[GenerateSerializer, Alias("azure.spec.identity")]
public sealed record IdentitySpec : ResourceSpec
{
    public override string Kind => ResourceKinds.Identity;
}

[GenerateSerializer, Alias("azure.spec.log-workspace")]
public sealed record LogWorkspaceSpec : ResourceSpec
{
    [Id(0)] public int RetentionDays { get; init; } = 30;

    public override string Kind => ResourceKinds.LogWorkspace;
}

[GenerateSerializer, Alias("azure.spec.storage-account")]
public sealed record StorageAccountSpec : ResourceSpec
{
    [Id(0)] public string Sku { get; init; } = "Standard_LRS";

    public override string Kind => ResourceKinds.StorageAccount;
}

[GenerateSerializer, Alias("azure.spec.file-share")]
public sealed record FileShareSpec : ResourceSpec
{
    [Id(0)] public string StorageAccount { get; init; } = "";
    [Id(1)] public int QuotaGb { get; init; } = 20;

    public override string Kind => ResourceKinds.FileShare;
}

[GenerateSerializer, Alias("azure.spec.blob-container")]
public sealed record BlobContainerSpec : ResourceSpec
{
    [Id(0)] public string StorageAccount { get; init; } = "";

    public override string Kind => ResourceKinds.BlobContainer;
}

// Role-based access: data-plane access comes from RoleGrantSpec, not access policies.
[GenerateSerializer, Alias("azure.spec.key-vault")]
public sealed record KeyVaultSpec : ResourceSpec
{
    [Id(0)] public string TenantId { get; init; } = "";

    public override string Kind => ResourceKinds.KeyVault;
}

// Overwrite false keeps an existing value: a master key is never replaced by a later install.
[GenerateSerializer, Alias("azure.spec.secret")]
public sealed record SecretSpec : ResourceSpec
{
    [Id(0)] public string Vault { get; init; } = "";
    [Id(1)] public string Value { get; init; } = "";
    [Id(2)] public bool Overwrite { get; init; }

    // A value of the form "{vault-secret:name}" inside Value is replaced, when the secret is written, by the
    // current value of that secret in the same vault, so a connection string can carry a password only the vault knows.
    public static string Reference(string secret) => "{vault-secret:" + secret + "}";

    public override string Kind => ResourceKinds.Secret;
}

[GenerateSerializer, Alias("azure.spec.environment-storage")]
public sealed record EnvironmentStorageSpec
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string StorageAccount { get; init; } = "";
    [Id(2)] public string ShareName { get; init; } = "";
}

// Workload profiles: Consumption. SubnetId is optional; without it the environment is not VNet-integrated.
[GenerateSerializer, Alias("azure.spec.managed-environment")]
public sealed record ManagedEnvironmentSpec : ResourceSpec
{
    [Id(0)] public string LogWorkspace { get; init; } = "";
    [Id(1)] public string? SubnetId { get; init; }
    [Id(2)] public EnvironmentStorageSpec[] Storages { get; init; } = [];

    public override string Kind => ResourceKinds.ManagedEnvironment;
}

[GenerateSerializer, Alias("azure.spec.secret-ref")]
public sealed record SecretRef
{
    // Container App secret name, read from Key Vault with the app's identity.
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string Vault { get; init; } = "";
    [Id(2)] public string VaultSecret { get; init; } = "";
    // The environment variable that carries the secret.
    [Id(3)] public string Variable { get; init; } = "";
}

[GenerateSerializer, Alias("azure.spec.volume-mount")]
public sealed record VolumeMountSpec
{
    [Id(0)] public string Volume { get; init; } = "";
    [Id(1)] public string EnvironmentStorage { get; init; } = "";
    [Id(2)] public string MountPath { get; init; } = "";
    [Id(3)] public string MountOptions { get; init; } = "";
}

// Where an app listens. TargetPort 0 means the spec's IngressPort. External false gives environment-internal ingress.
[GenerateSerializer, Alias("azure.spec.ingress")]
public sealed record IngressSpec
{
    [Id(0)] public bool External { get; init; } = true;
    [Id(1)] public int TargetPort { get; init; }
}

// A Key Vault secret mounted as one file: an app secret plus a Secret volume and a sub-path mount at Path.
[GenerateSerializer, Alias("azure.spec.secret-file")]
public sealed record SecretFileSpec
{
    // Container App secret name.
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public string Vault { get; init; } = "";
    [Id(2)] public string VaultSecret { get; init; } = "";
    // The full path of the file in the container, for example /usr/share/nginx/html/config.json.
    [Id(3)] public string Path { get; init; } = "";
}

// A registry the app pulls from. Without PasswordSecret the pull uses an identity (the app's, or Identity by name);
// with it the pull uses Username and the Key Vault secret PasswordSecret in Vault (ghcr-read-token for GHCR).
[GenerateSerializer, Alias("azure.spec.registry-credential")]
public sealed record RegistryCredentialSpec
{
    [Id(0)] public string Server { get; init; } = "";
    [Id(1)] public string Identity { get; init; } = "";
    [Id(2)] public string Username { get; init; } = "";
    [Id(3)] public string Vault { get; init; } = "";
    [Id(4)] public string PasswordSecret { get; init; } = "";
}

[GenerateSerializer, Alias("azure.spec.container-app")]
public sealed record ContainerAppSpec : ResourceSpec
{
    // The value ArmAzure replaces with the identity's client id (AZURE_CLIENT_ID).
    public const string IdentityClientIdToken = "{identity-client-id}";

    [Id(0)] public string Environment { get; init; } = "";
    [Id(1)] public string Image { get; init; } = "";
    [Id(2)] public double Cpu { get; init; } = 0.5;
    [Id(3)] public string Memory { get; init; } = "1Gi";
    [Id(4)] public int MinReplicas { get; init; } = 1;
    [Id(5)] public int MaxReplicas { get; init; } = 1;
    [Id(6)] public Dictionary<string, string> Variables { get; init; } = [];
    [Id(7)] public SecretRef[] Secrets { get; init; } = [];
    [Id(8)] public string Identity { get; init; } = "";
    [Id(9)] public int IngressPort { get; init; } = 8080;
    [Id(10)] public int[] InternalPorts { get; init; } = [];
    [Id(11)] public VolumeMountSpec[] VolumeMounts { get; init; } = [];
    [Id(12)] public string RegistryServer { get; init; } = "";
    [Id(13)] public string[] ProbePaths { get; init; } = [];
    [Id(14)] public int TerminationGracePeriodSeconds { get; init; } = 60;
    [Id(15)] public IngressSpec Ingress { get; init; } = new();
    [Id(16)] public SecretFileSpec[] SecretFiles { get; init; } = [];
    [Id(17)] public RegistryCredentialSpec[] Registries { get; init; } = [];

    public override string Kind => ResourceKinds.ContainerApp;
}

// Principal is an identity name in the spec's resource group, or a principal id. Scope is a resource id.
[GenerateSerializer, Alias("azure.spec.role-grant")]
public sealed record RoleGrantSpec : ResourceSpec
{
    [Id(0)] public string Principal { get; init; } = "";
    [Id(1)] public string RoleDefinitionId { get; init; } = "";
    [Id(2)] public string Scope { get; init; } = "";

    public override string Kind => ResourceKinds.RoleGrant;
}

[GenerateSerializer, Alias("azure.spec.security-rule")]
public sealed record SecurityRuleSpec
{
    [Id(0)] public string Name { get; init; } = "";
    [Id(1)] public int Priority { get; init; }
    // Inbound or Outbound.
    [Id(2)] public string Direction { get; init; } = "Outbound";
    // Allow or Deny.
    [Id(3)] public string Access { get; init; } = "Allow";
    // Tcp, Udp or *.
    [Id(4)] public string Protocol { get; init; } = "Tcp";
    [Id(5)] public string DestinationAddressPrefix { get; init; } = "*";
    [Id(6)] public string DestinationPortRange { get; init; } = "*";
}

[GenerateSerializer, Alias("azure.spec.network-security-group")]
public sealed record NetworkSecurityGroupSpec : ResourceSpec
{
    [Id(0)] public SecurityRuleSpec[] Rules { get; init; } = [];

    public override string Kind => ResourceKinds.NetworkSecurityGroup;
}

[GenerateSerializer, Alias("azure.spec.virtual-network")]
public sealed record VirtualNetworkSpec : ResourceSpec
{
    [Id(0)] public string AddressSpace { get; init; } = "10.0.0.0/16";

    public override string Kind => ResourceKinds.VirtualNetwork;
}

// A subnet in a virtual network of the spec's resource group. A delegation service such as
// Microsoft.App/environments reserves the subnet for it.
[GenerateSerializer, Alias("azure.spec.subnet")]
public sealed record SubnetSpec : ResourceSpec
{
    [Id(0)] public string VirtualNetwork { get; init; } = "";
    [Id(1)] public string AddressPrefix { get; init; } = "";
    [Id(2)] public string NetworkSecurityGroup { get; init; } = "";
    [Id(3)] public string DelegationService { get; init; } = "";

    public override string Kind => ResourceKinds.Subnet;
}

// A dynamic custom-container session pool: one Hyper-V session per caller running Image, pulled with
// Identity (which needs AcrPull on the registry). Sessions never see the identity.
[GenerateSerializer, Alias("azure.spec.session-pool")]
public sealed record SessionPoolSpec : ResourceSpec
{
    [Id(0)] public string Environment { get; init; } = "";
    [Id(1)] public string Image { get; init; } = "";
    [Id(2)] public double Cpu { get; init; } = 1;
    [Id(3)] public string Memory { get; init; } = "2Gi";
    [Id(4)] public int TargetPort { get; init; } = 8080;
    [Id(5)] public string Identity { get; init; } = "";
    [Id(6)] public string RegistryServer { get; init; } = "";
    [Id(7)] public int MaxSessions { get; init; } = 100;
    [Id(8)] public int ReadySessions { get; init; } = 2;
    [Id(9)] public int MaxAliveSeconds { get; init; } = 86400;
    [Id(10)] public bool Egress { get; init; } = true;

    public override string Kind => ResourceKinds.SessionPool;
}

// Basic is enough for a test environment. Admin user stays off: pulls use identities (AcrPull) or a token.
[GenerateSerializer, Alias("azure.spec.registry")]
public sealed record RegistrySpec : ResourceSpec
{
    [Id(0)] public string Sku { get; init; } = "Basic";

    public override string Kind => ResourceKinds.Registry;
}

// Azure Database for PostgreSQL Flexible Server on a public endpoint with the allow-Azure-services rule.
// The admin password is the Key Vault secret AdminPasswordSecret in Vault.
[GenerateSerializer, Alias("azure.spec.postgres")]
public sealed record PostgresSpec : ResourceSpec
{
    [Id(0)] public string Sku { get; init; } = "Standard_B1ms";
    [Id(1)] public string Tier { get; init; } = "Burstable";
    [Id(2)] public int StorageGb { get; init; } = 32;
    [Id(3)] public string Version { get; init; } = "16";
    [Id(4)] public string AdminLogin { get; init; } = "";
    [Id(5)] public string Vault { get; init; } = "";
    [Id(6)] public string AdminPasswordSecret { get; init; } = "";

    public override string Kind => ResourceKinds.Postgres;
}

[GenerateSerializer, Alias("azure.spec.postgres-database")]
public sealed record PostgresDatabaseSpec : ResourceSpec
{
    // The server name, in the spec's resource group.
    [Id(0)] public string Server { get; init; } = "";

    public override string Kind => ResourceKinds.PostgresDatabase;
}
