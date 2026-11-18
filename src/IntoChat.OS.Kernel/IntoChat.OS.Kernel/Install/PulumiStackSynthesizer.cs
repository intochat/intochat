using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Install;

/// <summary>
/// Synthesizes a Pulumi YAML Infrastructure-as-Code stack from <see cref="InstallConfig"/>,
/// <see cref="InstallPlan"/>, and composed <see cref="OsModule"/> manifests/integration schemas.
/// Every module's <c>Settings</c>, <c>Secrets</c>, and <c>IntegrationSchema</c> secret fields
/// map deterministically to Key Vault secrets and Container App environment variables,
/// and zero Azure Container Apps Session Pools (<c>Microsoft.App/sessionPools</c>) are ever emitted.
/// </summary>
public static class PulumiStackSynthesizer
{
    public const string StackProjectName = "intochat-os";
    private const string ArmApiVersionResources = "2024-03-01";
    private const string ArmApiVersionManagedIdentity = "2023-01-31";
    private const string ArmApiVersionOperationalInsights = "2023-09-01";
    private const string ArmApiVersionStorage = "2023-05-01";
    private const string ArmApiVersionContainerRegistry = "2023-07-01";
    private const string ArmApiVersionKeyVault = "2023-07-01";
    private const string ArmApiVersionAuthorization = "2022-04-01";
    private const string ArmApiVersionPostgreSql = "2023-12-01-preview";
    private const string ArmApiVersionApp = "2024-03-01";

    public static PulumiStackPlan Synthesize(
        InstallConfig config,
        IEnumerable<OsModule> modules,
        IReadOnlyDictionary<string, string>? providedSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(modules);

        var validated = config.Validated();
        var moduleList = modules.ToArray();
        var manifests = ManifestEnvironment.ManifestsOf(moduleList);
        var (settingVariables, manifestSecrets) = ManifestEnvironment.Render(validated, manifests);

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
            string? secretValue = null;
            var hasProvidedValue = providedSecrets is not null
                && providedSecrets.TryGetValue(integrationSecret.VaultSecret, out secretValue)
                && !string.IsNullOrEmpty(secretValue);
            secrets.Add(integrationSecret);
            secretSpecs.Add(new SecretSpec
            {
                Name = integrationSecret.VaultSecret,
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

        var foundationSpecs = InstallPlan.Foundation(validated);
        var osAppSpec = InstallPlan.OsApp(validated, variables, secrets);
        var allSpecs = new List<ResourceSpec>(foundationSpecs.Length + secretSpecs.Count + 1);
        allSpecs.AddRange(foundationSpecs);
        allSpecs.AddRange(secretSpecs);
        allSpecs.Add(osAppSpec);

        return new PulumiStackPlan(
            validated,
            allSpecs,
            osAppSpec,
            variables,
            secrets,
            RenderPulumiYaml(validated, foundationSpecs, secretSpecs, osAppSpec),
            RenderStackConfigYaml(validated));
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
        ManagedEnvironmentSpec => "azure-native:app:ManagedEnvironment",
        ContainerAppSpec => "azure-native:app:ContainerApp",
        _ => null,
    };

    private static string RenderStackConfigYaml(InstallConfig c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Generated by IntoChat.OS PulumiStackSynthesizer from deploy/" + c.Env + ".json");
        sb.AppendLine("config:");
        sb.AppendLine($"  azure-native:subscriptionId: \"{c.Subscription}\"");
        sb.AppendLine($"  azure-native:location: \"{c.Location}\"");
        sb.AppendLine($"  {StackProjectName}:env: \"{c.Env}\"");
        sb.AppendLine($"  {StackProjectName}:resourceGroup: \"{c.GroupName}\"");
        sb.AppendLine($"  {StackProjectName}:osImage: \"{c.OsImage}\"");
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string RenderPulumiYaml(
        InstallConfig c,
        IReadOnlyList<ResourceSpec> foundationSpecs,
        IReadOnlyList<SecretSpec> moduleSecretSpecs,
        ContainerAppSpec osApp)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"name: {StackProjectName}");
        sb.AppendLine("runtime: yaml");
        sb.AppendLine($"description: IntoChat OS ({c.Env}) Azure infrastructure synthesized from InstallConfig and ModuleManifests (zero SessionPool).");
        sb.AppendLine();
        sb.AppendLine("resources:");

        foreach (var spec in foundationSpecs)
        {
            AppendResourceYaml(sb, c, spec);
        }

        foreach (var secretSpec in moduleSecretSpecs.Where(s => !s.External && !string.IsNullOrEmpty(s.Value)))
        {
            AppendResourceYaml(sb, c, secretSpec);
        }

        AppendContainerAppYaml(sb, c, osApp);

        sb.AppendLine();
        sb.AppendLine("outputs:");
        sb.AppendLine("  resourceGroupName:");
        sb.AppendLine($"    value: \"{c.GroupName}\"");
        sb.AppendLine("  vaultName:");
        sb.AppendLine($"    value: \"{c.VaultName}\"");
        sb.AppendLine("  osAppName:");
        sb.AppendLine($"    value: \"{osApp.Name}\"");
        sb.AppendLine("  osImage:");
        sb.AppendLine($"    value: \"{osApp.Image}\"");
        sb.AppendLine("  moduleSecretsCount:");
        sb.AppendLine($"    value: {osApp.Secrets.Length}");
        sb.AppendLine("  moduleVariablesCount:");
        sb.AppendLine($"    value: {osApp.Variables.Count}");

        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void AppendResourceYaml(StringBuilder sb, InstallConfig c, ResourceSpec spec)
    {
        var logical = LogicalResourceName(spec);
        var tags = AzureTags.Merge(spec);
        switch (spec)
        {
            case ResourceGroupSpec rg:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:resources:ResourceGroup");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      resourceGroupName: \"{rg.Name}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case IdentitySpec id:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:managedidentity:UserAssignedIdentity");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      resourceName: \"{id.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case LogWorkspaceSpec log:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:operationalinsights:Workspace");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      workspaceName: \"{log.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine($"      retentionInDays: {log.RetentionDays}");
                sb.AppendLine("      sku:");
                sb.AppendLine("        name: PerGB2018");
                AppendTags(sb, tags, indent: 6);
                break;

            case StorageAccountSpec storage:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:storage:StorageAccount");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      accountName: \"{storage.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      kind: StorageV2");
                sb.AppendLine("      minimumTlsVersion: TLS1_2");
                sb.AppendLine("      allowBlobPublicAccess: false");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{storage.Sku}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case FileShareSpec share:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:storage:FileShare");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      shareName: \"{share.Name}\"");
                sb.AppendLine($"      accountName: \"{share.StorageAccount}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      shareQuota: {share.QuotaGb}");
                break;

            case BlobContainerSpec blob:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:storage:BlobContainer");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      containerName: \"{blob.Name}\"");
                sb.AppendLine($"      accountName: \"{blob.StorageAccount}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine("      publicAccess: None");
                break;

            case RegistrySpec acr:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:containerregistry:Registry");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      registryName: \"{acr.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      adminUserEnabled: false");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{acr.Sku}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case KeyVaultSpec kv:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:keyvault:Vault");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      vaultName: \"{kv.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      properties:");
                sb.AppendLine("        enableRbacAuthorization: true");
                sb.AppendLine($"        tenantId: \"{(string.IsNullOrEmpty(kv.TenantId) ? "${azure-native:authorization:getClientConfig().tenantId}" : kv.TenantId)}\"");
                sb.AppendLine("        sku:");
                sb.AppendLine("          family: A");
                sb.AppendLine("          name: standard");
                AppendTags(sb, tags, indent: 6);
                break;

            case PostgresSpec pg:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:Server");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      serverName: \"{pg.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine($"      administratorLogin: \"{pg.AdminLogin}\"");
                sb.AppendLine($"      version: \"{pg.Version}\"");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{pg.Sku}\"");
                sb.AppendLine($"        tier: \"{pg.Tier}\"");
                sb.AppendLine("      storage:");
                sb.AppendLine($"        storageSizeGB: {pg.StorageGb}");
                AppendTags(sb, tags, indent: 6);
                break;

            case PostgresDatabaseSpec pgDb:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:Database");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      databaseName: \"{pgDb.Name}\"");
                sb.AppendLine($"      serverName: \"{pgDb.Server}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                break;

            case ManagedEnvironmentSpec env:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:app:ManagedEnvironment");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      environmentName: \"{env.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      workloadProfiles:");
                sb.AppendLine("        - name: Consumption");
                sb.AppendLine("          workloadProfileType: Consumption");
                AppendTags(sb, tags, indent: 6);
                break;

            case SecretSpec secret when !secret.External && !string.IsNullOrEmpty(secret.Value):
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:keyvault:Secret");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      secretName: \"{secret.Name}\"");
                sb.AppendLine($"      vaultName: \"{secret.Vault}\"");
                sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
                sb.AppendLine("      properties:");
                sb.AppendLine($"        value: {YamlQuote(secret.Value)}");
                break;
        }
    }

    private static void AppendContainerAppYaml(StringBuilder sb, InstallConfig c, ContainerAppSpec app)
    {
        var logical = LogicalResourceName(app);
        var identityResourceId = ResourceIds.For(c.Subscription, ResourceKinds.Identity, c.GroupName, app.Identity);
        var environmentResourceId = ResourceIds.For(c.Subscription, ResourceKinds.ManagedEnvironment, c.GroupName, app.Environment);
        var tags = AzureTags.Merge(app);

        sb.AppendLine($"  {logical}:");
        sb.AppendLine("    type: azure-native:app:ContainerApp");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      containerAppName: \"{app.Name}\"");
        sb.AppendLine($"      resourceGroupName: \"{c.GroupName}\"");
        sb.AppendLine($"      location: \"{c.Location}\"");
        sb.AppendLine($"      environmentId: \"{environmentResourceId}\"");
        sb.AppendLine("      workloadProfileName: Consumption");
        sb.AppendLine("      identity:");
        sb.AppendLine("        type: UserAssigned");
        sb.AppendLine("        userAssignedIdentities:");
        sb.AppendLine($"          \"{identityResourceId}\": {{}}");
        sb.AppendLine("      configuration:");
        sb.AppendLine("        activeRevisionsMode: Single");
        sb.AppendLine("        ingress:");
        sb.AppendLine($"          external: {(app.Ingress.External ? "true" : "false")}");
        sb.AppendLine($"          targetPort: {(app.Ingress.TargetPort > 0 ? app.Ingress.TargetPort : app.IngressPort)}");
        sb.AppendLine("          transport: Auto");
        if (app.InternalPorts.Length > 0)
        {
            sb.AppendLine("          additionalPortMappings:");
            foreach (var port in app.InternalPorts)
            {
                sb.AppendLine("            - external: false");
                sb.AppendLine($"              targetPort: {port}");
                sb.AppendLine($"              exposedPort: {port}");
            }
        }
        if (app.RegistryServer.Length > 0)
        {
            sb.AppendLine("        registries:");
            sb.AppendLine($"          - server: \"{app.RegistryServer}\"");
            sb.AppendLine($"            identity: \"{identityResourceId}\"");
        }
        if (app.Secrets.Length > 0)
        {
            sb.AppendLine("        secrets:");
            foreach (var secret in app.Secrets.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"          - name: \"{secret.Name}\"");
                sb.AppendLine($"            keyVaultUrl: \"https://{secret.Vault}.vault.azure.net/secrets/{secret.VaultSecret}\"");
                sb.AppendLine($"            identity: \"{identityResourceId}\"");
            }
        }
        sb.AppendLine("      template:");
        sb.AppendLine("        scale:");
        sb.AppendLine($"          minReplicas: {app.MinReplicas}");
        sb.AppendLine($"          maxReplicas: {app.MaxReplicas}");
        sb.AppendLine("        containers:");
        sb.AppendLine("          - name: app");
        sb.AppendLine($"            image: \"{app.Image}\"");
        sb.AppendLine("            resources:");
        sb.AppendLine($"              cpu: {app.Cpu.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        sb.AppendLine($"              memory: \"{app.Memory}\"");
        sb.AppendLine("            env:");
        foreach (var (name, value) in app.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"              - name: \"{name}\"");
            sb.AppendLine($"                value: {YamlQuote(value)}");
        }
        foreach (var secret in app.Secrets.OrderBy(s => s.Variable, StringComparer.Ordinal))
        {
            sb.AppendLine($"              - name: \"{secret.Variable}\"");
            sb.AppendLine($"                secretRef: \"{secret.Name}\"");
        }
        if (app.VolumeMounts.Length > 0)
        {
            sb.AppendLine("            volumeMounts:");
            foreach (var mount in app.VolumeMounts)
            {
                sb.AppendLine($"              - volumeName: \"{mount.Volume}\"");
                sb.AppendLine($"                mountPath: \"{mount.MountPath}\"");
            }
            sb.AppendLine("        volumes:");
            foreach (var mount in app.VolumeMounts)
            {
                sb.AppendLine($"          - name: \"{mount.Volume}\"");
                sb.AppendLine("            storageType: AzureFile");
                sb.AppendLine($"            storageName: \"{mount.EnvironmentStorage}\"");
                sb.AppendLine($"            mountOptions: \"{mount.MountOptions}\"");
            }
        }
        AppendTags(sb, tags, indent: 6);
    }

    private static void AppendTags(StringBuilder sb, IReadOnlyDictionary<string, string> tags, int indent)
    {
        if (tags.Count == 0)
        {
            return;
        }
        var pad = new string(' ', indent);
        sb.AppendLine($"{pad}tags:");
        foreach (var (k, v) in tags.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"{pad}  \"{k}\": {YamlQuote(v)}");
        }
    }

    private static string YamlQuote(string value)
        => JsonSerializer.Serialize(value);
}

public sealed record PulumiStackPlan(
    InstallConfig Config,
    IReadOnlyList<ResourceSpec> Resources,
    ContainerAppSpec OsApp,
    IReadOnlyDictionary<string, string> Variables,
    IReadOnlyList<KeyVaultSecretRef> Secrets,
    string PulumiYaml,
    string StackConfigYaml);
