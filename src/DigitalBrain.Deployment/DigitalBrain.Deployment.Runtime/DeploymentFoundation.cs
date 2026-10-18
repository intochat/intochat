using Pulumi;

namespace DigitalBrain.Deployment.Runtime;

// What the program that owns the foundation hands the OS runtime: an existing workload-profile environment,
// the runtime's user-assigned identity, and the vault, registry and storage account it reads from.
public sealed record DeploymentFoundation(
    string NamingPrefix,
    string SubscriptionId,
    Output<string> ResourceGroupName,
    Output<string> EnvironmentId,
    Output<string> EnvironmentName,
    Output<string> KeyVaultId,
    Output<string> KeyVaultName,
    Output<string> KeyVaultUri,
    Output<string> RegistryId,
    Output<string> RegistryLoginServer,
    Output<string> StorageAccountId,
    Output<string> StorageAccountName,
    // Only the environment storage definition of the Azure Files mount holds it; no container sees it.
    Output<string> StorageAccountKey,
    Output<string> RuntimeIdentityId,
    Output<string> RuntimeIdentityPrincipalId,
    Output<string> RuntimeIdentityClientId);
