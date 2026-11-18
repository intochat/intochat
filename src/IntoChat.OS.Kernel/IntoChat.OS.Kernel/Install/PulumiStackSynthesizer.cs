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
            if (providedSecrets is not null && providedSecrets.Count > 0 && !hasProvidedValue)
            {
                continue;
            }
            secrets.Add(integrationSecret);
            if (!secretSpecs.Any(s => string.Equals(s.Name, integrationSecret.VaultSecret, StringComparison.Ordinal)))
            {
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

        var foundationSpecs = InstallPlan.Foundation(validated);
        var foundationSecretNames = foundationSpecs.OfType<SecretSpec>().Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
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
        var osAppSpec = InstallPlan.OsApp(validated, variables, secrets);
        var cloudAppSpec = InstallPlan.CloudApp(validated, variables, secrets);
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
        ContainerAppSpec osApp,
        ContainerAppSpec? cloudApp = null,
        ContainerAppSpec? sandboxApp = null)
    {
        var sb = new StringBuilder();
        var rgLogical = LogicalResourceName(new ResourceGroupSpec { Name = c.GroupName });
        var logLogical = LogicalResourceName(new LogWorkspaceSpec { Name = c.LogsName });
        var stLogical = LogicalResourceName(new StorageAccountSpec { Name = c.StorageName });
        sb.AppendLine($"name: {StackProjectName}");
        sb.AppendLine("runtime: yaml");
        sb.AppendLine($"description: IntoChat OS ({c.Env}) Azure infrastructure synthesized from InstallConfig and ModuleManifests (zero SessionPool).");
        sb.AppendLine();
        sb.AppendLine("variables:");
        sb.AppendLine("  clientConfig:");
        sb.AppendLine("    fn::invoke:");
        sb.AppendLine("      function: azure-native:authorization:getClientConfig");
        sb.AppendLine("      arguments: {}");
        sb.AppendLine("  logSharedKeys:");
        sb.AppendLine("    fn::invoke:");
        sb.AppendLine("      function: azure-native:operationalinsights:getSharedKeys");
        sb.AppendLine("      arguments:");
        sb.AppendLine($"        resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine($"        workspaceName: \"${{{logLogical}.name}}\"");
        sb.AppendLine("  storageKeys:");
        sb.AppendLine("    fn::invoke:");
        sb.AppendLine("      function: azure-native:storage:listStorageAccountKeys");
        sb.AppendLine("      arguments:");
        sb.AppendLine($"        resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine($"        accountName: \"${{{stLogical}.name}}\"");
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

        var allSecretSpecs = foundationSpecs.OfType<SecretSpec>()
            .Concat(moduleSecretSpecs)
            .Where(s => !s.External && !string.IsNullOrEmpty(s.Value))
            .ToArray();
        var roleGrantSpecs = foundationSpecs.OfType<RoleGrantSpec>().ToArray();

        AppendContainerAppYaml(sb, c, osApp, allSecretSpecs, roleGrantSpecs);
        if (cloudApp is not null)
        {
            AppendContainerAppYaml(sb, c, cloudApp, allSecretSpecs, roleGrantSpecs);
        }
        if (sandboxApp is not null)
        {
            AppendContainerAppYaml(sb, c, sandboxApp, allSecretSpecs, roleGrantSpecs);
        }

        var envLogical = LogicalResourceName(new ManagedEnvironmentSpec { Name = c.EnvironmentName });
        var osLogical = LogicalResourceName(osApp);
        sb.AppendLine();
        sb.AppendLine("outputs:");
        sb.AppendLine("  resourceGroupName:");
        sb.AppendLine($"    value: \"{c.GroupName}\"");
        sb.AppendLine("  vaultName:");
        sb.AppendLine($"    value: \"{c.VaultName}\"");
        sb.AppendLine("  osAppName:");
        sb.AppendLine($"    value: \"{osApp.Name}\"");
        sb.AppendLine("  osFqdn:");
        sb.AppendLine($"    value: \"${{{osLogical}.configuration.ingress.fqdn}}\"");
        sb.AppendLine("  environmentDefaultDomain:");
        sb.AppendLine($"    value: \"${{{envLogical}.defaultDomain}}\"");
        sb.AppendLine("  environmentStaticIp:");
        sb.AppendLine($"    value: \"${{{envLogical}.staticIp}}\"");
        sb.AppendLine("  customDomainVerificationId:");
        sb.AppendLine($"    value: \"${{{envLogical}.customDomainConfiguration.customDomainVerificationId}}\"");
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
        var rgLogical = LogicalResourceName(new ResourceGroupSpec { Name = c.GroupName });
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
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case LogWorkspaceSpec log:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:operationalinsights:Workspace");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      workspaceName: \"{log.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
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
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      kind: StorageV2");
                sb.AppendLine("      minimumTlsVersion: TLS1_2");
                sb.AppendLine("      allowBlobPublicAccess: false");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{storage.Sku}\"");
                AppendTags(sb, tags, indent: 6);
                break;

            case FileShareSpec share:
                var shareStLogical = LogicalResourceName(new StorageAccountSpec { Name = share.StorageAccount });
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:storage:FileShare");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      shareName: \"{share.Name}\"");
                sb.AppendLine($"      accountName: \"${{{shareStLogical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      shareQuota: {share.QuotaGb}");
                break;

            case BlobContainerSpec blob:
                var blobStLogical = LogicalResourceName(new StorageAccountSpec { Name = blob.StorageAccount });
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:storage:BlobContainer");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      containerName: \"{blob.Name}\"");
                sb.AppendLine($"      accountName: \"${{{blobStLogical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine("      publicAccess: None");
                break;

            case RegistrySpec acr:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:containerregistry:Registry");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      registryName: \"{acr.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
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
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      properties:");
                sb.AppendLine("        enableRbacAuthorization: true");
                sb.AppendLine($"        tenantId: \"{(string.IsNullOrEmpty(kv.TenantId) ? "${clientConfig.tenantId}" : kv.TenantId)}\"");
                sb.AppendLine("        sku:");
                sb.AppendLine("          family: A");
                sb.AppendLine("          name: standard");
                AppendTags(sb, tags, indent: 6);
                break;

            case PostgresSpec pg:
                var pgPassLogical = LogicalResourceName(new SecretSpec { Name = pg.AdminPasswordSecret });
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:Server");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      serverName: \"{pg.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine($"      administratorLogin: \"{pg.AdminLogin}\"");
                sb.AppendLine($"      administratorLoginPassword: \"${{{pgPassLogical}.properties.value}}\"");
                sb.AppendLine($"      version: \"{pg.Version}\"");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{pg.Sku}\"");
                sb.AppendLine($"        tier: \"{pg.Tier}\"");
                sb.AppendLine("      storage:");
                sb.AppendLine($"        storageSizeGB: {pg.StorageGb}");
                sb.AppendLine("      network:");
                sb.AppendLine("        publicNetworkAccess: Enabled");
                AppendTags(sb, tags, indent: 6);
                sb.AppendLine($"  {logical}-fw-azure:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:FirewallRule");
                sb.AppendLine("    properties:");
                sb.AppendLine("      firewallRuleName: \"AllowAllAzureServicesAndResourcesWithinAzureIps\"");
                sb.AppendLine($"      serverName: \"${{{logical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine("      startIpAddress: \"0.0.0.0\"");
                sb.AppendLine("      endIpAddress: \"0.0.0.0\"");
                break;

            case PostgresDatabaseSpec pgDb:
                var pgServerLogical = LogicalResourceName(new PostgresSpec { Name = pgDb.Server });
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:Database");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      databaseName: \"{pgDb.Name}\"");
                sb.AppendLine($"      serverName: \"${{{pgServerLogical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                break;

            case ManagedEnvironmentSpec env:
                var logLogical = LogicalResourceName(new LogWorkspaceSpec { Name = env.LogWorkspace });
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:app:ManagedEnvironment");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      environmentName: \"{env.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      appLogsConfiguration:");
                sb.AppendLine("        destination: log-analytics");
                sb.AppendLine("        logAnalyticsConfiguration:");
                sb.AppendLine($"          customerId: \"${{{logLogical}.customerId}}\"");
                sb.AppendLine("          sharedKey: \"${logSharedKeys.primarySharedKey}\"");
                sb.AppendLine("      workloadProfiles:");
                sb.AppendLine("        - name: Consumption");
                sb.AppendLine("          workloadProfileType: Consumption");
                AppendTags(sb, tags, indent: 6);
                foreach (var st in env.Storages)
                {
                    var stLogical = LogicalResourceName(new StorageAccountSpec { Name = st.StorageAccount });
                    var shareLogical = LogicalResourceName(new FileShareSpec { Name = st.ShareName });
                    sb.AppendLine($"  {logical}-storage-{st.Name}:");
                    sb.AppendLine("    type: azure-native:app:ManagedEnvironmentsStorage");
                    sb.AppendLine("    options:");
                    sb.AppendLine($"      dependsOn: [\"${{{shareLogical}}}\"]");
                    sb.AppendLine("    properties:");
                    sb.AppendLine($"      environmentName: \"${{{logical}.name}}\"");
                    sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                    sb.AppendLine($"      storageName: \"{st.Name}\"");
                    sb.AppendLine("      properties:");
                    sb.AppendLine("        azureFile:");
                    sb.AppendLine($"          accountName: \"${{{stLogical}.name}}\"");
                    sb.AppendLine("          accountKey: \"${storageKeys.keys[0].value}\"");
                    sb.AppendLine($"          shareName: \"{st.ShareName}\"");
                    sb.AppendLine("          accessMode: ReadWrite");
                }
                break;

            case RoleGrantSpec grant:
                var idLogical = LogicalResourceName(new IdentitySpec { Name = grant.Principal });
                var principalExpr = Guid.TryParse(grant.Principal, out _) ? $"\"{grant.Principal}\"" : $"\"${{{idLogical}.principalId}}\"";
                var grantGuid = DeterministicGuid($"{grant.Scope}|{grant.Principal}|{grant.RoleDefinitionId}");
                var scopeDepLogical = grant.Scope switch
                {
                    var s when s.Contains("/Microsoft.KeyVault/vaults/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new KeyVaultSpec { Name = c.VaultName }),
                    var s when s.Contains("/Microsoft.Storage/storageAccounts/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new StorageAccountSpec { Name = c.StorageName }),
                    var s when s.Contains("/Microsoft.ContainerRegistry/registries/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new RegistrySpec { Name = c.RegistryName }),
                    _ => rgLogical,
                };
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:authorization:RoleAssignment");
                sb.AppendLine("    options:");
                sb.AppendLine($"      dependsOn: [\"${{{scopeDepLogical}}}\"]");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      roleAssignmentName: \"{grantGuid}\"");
                sb.AppendLine($"      scope: \"{grant.Scope}\"");
                sb.AppendLine($"      principalId: {principalExpr}");
                sb.AppendLine("      principalType: ServicePrincipal");
                sb.AppendLine($"      roleDefinitionId: \"/subscriptions/{c.Subscription}/providers/Microsoft.Authorization/roleDefinitions/{grant.RoleDefinitionId}\"");
                break;

            case SecretSpec secret when !secret.External && !string.IsNullOrEmpty(secret.Value):
                var kvLogical = LogicalResourceName(new KeyVaultSpec { Name = secret.Vault });
                var installerGrantSpec = c.InstallerPrincipalId.Length > 0
                    ? new RoleGrantSpec { Name = $"{c.OsName}-installer-secrets" }
                    : null;
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:keyvault:Secret");
                if (installerGrantSpec is not null)
                {
                    sb.AppendLine("    options:");
                    sb.AppendLine($"      dependsOn: [\"${{{LogicalResourceName(installerGrantSpec)}}}\"]");
                }
                sb.AppendLine("    properties:");
                sb.AppendLine($"      secretName: \"{secret.Name}\"");
                sb.AppendLine($"      vaultName: \"${{{kvLogical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine("      properties:");
                sb.AppendLine($"        value: {YamlQuote(secret.Value)}");
                break;
        }
    }

    private static void AppendContainerAppYaml(
        StringBuilder sb,
        InstallConfig c,
        ContainerAppSpec app,
        IReadOnlyList<SecretSpec> writtenSecrets,
        IReadOnlyList<RoleGrantSpec> roleGrants)
    {
        var logical = LogicalResourceName(app);
        var rgLogical = LogicalResourceName(new ResourceGroupSpec { Name = c.GroupName });
        var identityLogical = LogicalResourceName(new IdentitySpec { Name = app.Identity });
        var envLogical = LogicalResourceName(new ManagedEnvironmentSpec { Name = app.Environment });
        var identityResourceId = ResourceIds.For(c.Subscription, ResourceKinds.Identity, c.GroupName, app.Identity);
        var tags = AzureTags.Merge(app);

        var dependsOn = new List<string> { identityLogical, envLogical };
        foreach (var mount in app.VolumeMounts)
        {
            dependsOn.Add($"{envLogical}-storage-{mount.EnvironmentStorage}");
        }
        foreach (var grant in roleGrants)
        {
            dependsOn.Add(LogicalResourceName(grant));
        }
        foreach (var sec in writtenSecrets)
        {
            dependsOn.Add(LogicalResourceName(sec));
        }

        sb.AppendLine($"  {logical}:");
        sb.AppendLine("    type: azure-native:app:ContainerApp");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [{string.Join(", ", dependsOn.Distinct(StringComparer.Ordinal).Select(d => $"\"${{{d}}}\""))}]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      containerAppName: \"{app.Name}\"");
        sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine($"      location: \"{c.Location}\"");
        sb.AppendLine($"      environmentId: \"${{{envLogical}.id}}\"");
        sb.AppendLine("      workloadProfileName: Consumption");
        sb.AppendLine("      identity:");
        sb.AppendLine("        type: UserAssigned");
        sb.AppendLine("        userAssignedIdentities:");
        sb.AppendLine($"          - \"{identityResourceId}\"");
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
            var resolved = value.Replace(ContainerAppSpec.IdentityClientIdToken, $"${{{identityLogical}.clientId}}", StringComparison.Ordinal);
            sb.AppendLine($"              - name: \"{name}\"");
            sb.AppendLine($"                value: {YamlQuote(resolved)}");
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

    private static string DeterministicGuid(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
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
