using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Host.Modules;

namespace IntoChat.OS.Kernel.Install;

/// <summary>
/// Synthesizes a Pulumi YAML Infrastructure-as-Code stack from <see cref="InstallConfig"/>,
/// <see cref="InstallPlan"/>, and composed <see cref="OsModule"/> manifests/integration schemas.
/// Every module's <c>Settings</c>, <c>Secrets</c>, and <c>IntegrationSchema</c> secret fields
/// map deterministically to Key Vault secrets and Container App environment variables,
/// and zero Azure Container Apps Session Pools (<c>Microsoft.App/sessionPools</c>) are ever emitted.
/// </summary>
public static partial class PulumiStackSynthesizer
{
    public const string StackProjectName = "intochat-os";

    public static PulumiStackPlan Synthesize(
        InstallConfig config,
        IEnumerable<OsModule> modules,
        IReadOnlyDictionary<string, string>? providedSecrets = null,
        IReadOnlySet<string>? existingVaultSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modules);

        var validated = config.Validated();
        var moduleList = ResolveModules(modules);
        var manifests = ManifestEnvironment.ManifestsOf(moduleList);
        var (settingVariables, manifestSecrets) = ManifestEnvironment.Render(validated, manifests);

        var foundationSpecs = InstallPlan.Foundation(validated);
        var foundationSecretNames = foundationSpecs.OfType<SecretSpec>().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in settingVariables)
        {
            variables[k] = v;
        }
        var secrets = new List<KeyVaultSecretRef>(manifestSecrets);
        var secretSpecs = new List<SecretSpec>();

        // Module manifest secrets ({moduleId}-{secret}) -> Key Vault secrets + Container App env bindings
        foreach (var secretRef in manifestSecrets)
        {
            var value = providedSecrets is not null && providedSecrets.TryGetValue(secretRef.VaultSecret, out var v) ? v : "";
            secretSpecs.Add(new SecretSpec
            {
                Name = secretRef.VaultSecret,
                ResourceGroup = validated.GroupName,
                Location = validated.Location,
                Env = validated.Env,
                Owner = InstallPlan.Owner,
                Vault = validated.VaultName,
                Value = value,
                External = string.IsNullOrEmpty(value),
                Overwrite = !string.IsNullOrEmpty(value),
            });
        }

        // IntegrationSchema secrets ({integrationId}-{field}) -> Key Vault secrets + Container App env bindings
        foreach (var integrationSecret in ManifestEnvironment.IntegrationSecretsOf(moduleList, validated.VaultName))
        {
            if (secrets.Any(existing => string.Equals(existing.Variable, integrationSecret.Variable, StringComparison.Ordinal)))
            {
                continue;
            }
            var resolvedRef = integrationSecret;
            string? secretValue = null;
            var hasProvidedValue = providedSecrets is not null
                && providedSecrets.TryGetValue(integrationSecret.VaultSecret, out secretValue)
                && !string.IsNullOrEmpty(secretValue);
            var isFoundationSecret = foundationSecretNames.Contains(integrationSecret.VaultSecret);
            var isExistingVaultSecret = existingVaultSecrets is not null
                && existingVaultSecrets.Contains(integrationSecret.VaultSecret);

            // Backward-compatible fallback for existing Azure Key Vaults or secret files using legacy names
            // (e.g. db-connection / postgres-connection-string -> canonical postgres-connectionstring).
            if (!hasProvidedValue && !isFoundationSecret && !isExistingVaultSecret)
            {
                foreach (var legacyName in ManifestEnvironment.LegacySecretFallbacks(integrationSecret.VaultSecret))
                {
                    if (providedSecrets is not null && providedSecrets.TryGetValue(legacyName, out var legacyValue) && !string.IsNullOrEmpty(legacyValue))
                    {
                        secretValue = legacyValue;
                        hasProvidedValue = true;
                        break;
                    }
                    if (existingVaultSecrets is not null && existingVaultSecrets.Contains(legacyName))
                    {
                        resolvedRef = integrationSecret with { VaultSecret = legacyName };
                        isExistingVaultSecret = true;
                        break;
                    }
                }
            }

            if (providedSecrets is not null && providedSecrets.Count > 0
                && !hasProvidedValue && !isFoundationSecret && !isExistingVaultSecret)
            {
                continue;
            }
            secrets.Add(resolvedRef);
            if (!secretSpecs.Any(s => string.Equals(s.Name, resolvedRef.VaultSecret, StringComparison.Ordinal)))
            {
                secretSpecs.Add(new SecretSpec
                {
                    Name = resolvedRef.VaultSecret,
                    ResourceGroup = validated.GroupName,
                    Location = validated.Location,
                    Env = validated.Env,
                    Owner = InstallPlan.Owner,
                    Vault = validated.VaultName,
                    Value = hasProvidedValue && secretValue is not null ? secretValue : "",
                    External = !hasProvidedValue,
                    Overwrite = hasProvidedValue,
                });
            }
        }

        // Include any additional operator-provided Key Vault secrets (e.g. from backup/secrets-from file)
        if (providedSecrets is not null)
        {
            foreach (var (secretName, secretVal) in providedSecrets)
            {
                if (string.IsNullOrEmpty(secretVal)
                    || secretSpecs.Any(s => string.Equals(s.Name, secretName, StringComparison.Ordinal)))
                {
                    continue;
                }
                secretSpecs.Add(new SecretSpec
                {
                    Name = secretName,
                    ResourceGroup = validated.GroupName,
                    Location = validated.Location,
                    Env = validated.Env,
                    Owner = InstallPlan.Owner,
                    Vault = validated.VaultName,
                    Value = secretVal,
                    External = false,
                    Overwrite = true,
                });
            }
        }

        secretSpecs.RemoveAll(s => foundationSecretNames.Contains(s.Name));

        // Populate generated or provided values on foundation SecretSpecs (master-key, postgres-admin-password, db-connection)
        for (var i = 0; i < foundationSpecs.Length; i++)
        {
            if (foundationSpecs[i] is SecretSpec fSecret)
            {
                var val = fSecret.Value;
                if (providedSecrets is not null && providedSecrets.TryGetValue(fSecret.Name, out var pVal) && !string.IsNullOrEmpty(pVal))
                {
                    val = pVal;
                }
                else if (string.IsNullOrEmpty(val) && (fSecret.Name is InstallPlan.MasterKeySecret or InstallPlan.SandboxEnrollKeySecret))
                {
                    val = SecretGenerators.Generate(SecretGenerators.Base64(32));
                }
                else if (string.IsNullOrEmpty(val) && fSecret.Name == InstallPlan.PostgresPasswordSecret)
                {
                    val = SecretGenerators.Generate(SecretGenerators.Base64(24));
                }
                foundationSpecs[i] = fSecret with { Value = val, External = string.IsNullOrEmpty(val) };
            }
        }
        var resolvedVaultSecrets = foundationSpecs.OfType<SecretSpec>()
            .Concat(secretSpecs)
            .Where(s => !string.IsNullOrEmpty(s.Value))
            .ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);
        for (var i = 0; i < foundationSpecs.Length; i++)
        {
            if (foundationSpecs[i] is SecretSpec fSecret && fSecret.Value.Contains("{vault-secret:", StringComparison.Ordinal))
            {
                foundationSpecs[i] = fSecret with { Value = SecretSpec.Substitute(fSecret.Value, resolvedVaultSecrets) };
            }
        }
        var osAppSpec = EnforceSingleSiloScale(InstallPlan.OsApp(validated, variables, secrets));
        var cloudAppSpec = InstallPlan.CloudApp(validated, variables, secrets) is { } cloud
            ? EnforceSingleSiloScale(cloud)
            : null;
        var sandboxAppSpec = InstallPlan.SandboxApp(validated, variables, secrets);
        var allSpecs = new List<ResourceSpec>(foundationSpecs.Length + secretSpecs.Count + 3);
        allSpecs.AddRange(foundationSpecs);
        allSpecs.AddRange(secretSpecs);
        allSpecs.Add(osAppSpec);
        if (cloudAppSpec is not null) { allSpecs.Add(cloudAppSpec); }
        if (sandboxAppSpec is not null) { allSpecs.Add(sandboxAppSpec); }

        return new PulumiStackPlan(
            validated,
            allSpecs,
            osAppSpec,
            variables,
            secrets,
            RenderPulumiYaml(validated, foundationSpecs, secretSpecs, osAppSpec, cloudAppSpec, sandboxAppSpec),
            RenderStackConfigYaml(validated));
    }

    internal static ContainerAppSpec EnforceSingleSiloScale(ContainerAppSpec app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.StopBeforeUpdate
            ? app with { MinReplicas = 1, MaxReplicas = 1 }
            : app;
    }

    public static async Task WriteWorkspaceAsync(
        PulumiStackPlan plan,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        var utf8NoBom = new UTF8Encoding(false);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "Pulumi.yaml"),
            plan.PulumiYaml,
            utf8NoBom,
            cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, $"Pulumi.{plan.Config.Env}.yaml"),
            plan.StackConfigYaml,
            utf8NoBom,
            cancellationToken);
        var scriptsDirectory = Path.Combine(outputDirectory, "scripts");
        Directory.CreateDirectory(scriptsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(scriptsDirectory, "az-retry.sh"),
            PulumiAssets.AzRetryScript,
            utf8NoBom,
            cancellationToken);
    }

    public static JsonObject ExportImportEntries(PulumiStackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var c = plan.Config;
        var resources = new JsonArray();
        foreach (var spec in plan.Resources)
        {
            if (spec is SecretSpec or RoleGrantSpec or FileShareSpec or BlobContainerSpec)
            {
                continue;
            }
            var typeToken = PulumiTypeToken(spec);
            if (typeToken is null)
            {
                continue;
            }
            resources.Add(new JsonObject
            {
                ["type"] = typeToken,
                ["name"] = LogicalResourceName(spec),
                ["id"] = ResourceIds.For(c.Subscription, spec),
            });
        }
        return new JsonObject
        {
            ["nameTable"] = new JsonObject(),
            ["resources"] = resources,
        };
    }

    // The synthesized stack always includes the OS Container App (config.OsImage), which hosts OperatingSystemModules.Modules.
    // External hosts may pass only their own control-plane modules; merging OperatingSystemModules.Modules
    // guarantees OS manifest and integration secrets are never dropped from ContainerApp configuration.secrets on update.
    internal static OsModule[] ResolveModules(IEnumerable<OsModule> modules)
        => [.. modules.Concat(OperatingSystemModules.Modules).DistinctBy(static m => m.Type)];

    public static string LogicalResourceName(ResourceSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var raw = $"{spec.Kind}-{spec.Name}".ToLowerInvariant();
        return new string([.. raw.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')]).Trim('-');
    }

    private static string? PulumiTypeToken(ResourceSpec spec) => spec switch
    {
        ResourceGroupSpec => "azure-native:resources:ResourceGroup",
        IdentitySpec => "azure-native:managedidentity:UserAssignedIdentity",
        LogWorkspaceSpec => "azure-native:operationalinsights:Workspace",
        StorageAccountSpec => "azure-native:storage:StorageAccount",
        RegistrySpec => "azure-native:containerregistry:Registry",
        KeyVaultSpec => "azure-native:keyvault:Vault",
        PostgresSpec => "azure-native:dbforpostgresql:Server",
        PostgresDatabaseSpec => "azure-native:dbforpostgresql:Database",
        FoundrySpec => "azure-native:cognitiveservices:Account",
        FoundryDeploymentSpec => "azure-native:cognitiveservices:Deployment",
        ManagedEnvironmentSpec => "azure-native:app:ManagedEnvironment",
        ContainerAppSpec => "azure-native:app:ContainerApp",
        _ => null,
    };
}

public sealed record PulumiStackPlan(
    InstallConfig Config,
    IReadOnlyList<ResourceSpec> Resources,
    ContainerAppSpec OsApp,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<KeyVaultSecretRef> Secrets,
    string PulumiYaml,
    string StackConfigYaml);
