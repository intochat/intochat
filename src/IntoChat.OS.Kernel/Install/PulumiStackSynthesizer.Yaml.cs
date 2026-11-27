using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain.Modules.Configuration;
using IntoChat.OS.Host.ServiceDefaults;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Modules.Microsoft.Azure;
using Orleans.Configuration;

namespace IntoChat.OS.Kernel.Install;

public static partial class PulumiStackSynthesizer
{
    private static string RenderStackConfigYaml(InstallConfig c)
    {
        return PulumiAssets.StackConfigYamlTemplate
            .Replace("{{stackProjectName}}", StackProjectName, StringComparison.Ordinal)
            .Replace("{{subscription}}", c.Subscription, StringComparison.Ordinal)
            .Replace("{{location}}", c.Location, StringComparison.Ordinal)
            .Replace("{{env}}", c.Env, StringComparison.Ordinal)
            .Replace("{{groupName}}", c.GroupName, StringComparison.Ordinal)
            .Replace("{{osImage}}", c.OsImage, StringComparison.Ordinal);
    }

    private static string RenderPulumiYaml(
        InstallConfig c,
        IReadOnlyList<ResourceSpec> foundationSpecs,
        IReadOnlyList<SecretSpec> moduleSecretSpecs,
        ContainerAppSpec osApp,
        ContainerAppSpec? cloudApp = null,
        ContainerAppSpec? sandboxApp = null,
        ContainerAppsJobSpec? devSlotJob = null)
    {
        var rgLogical = LogicalResourceName(new ResourceGroupSpec { Name = c.GroupName });
        var logLogical = LogicalResourceName(new LogWorkspaceSpec { Name = c.LogsName });
        var stLogical = LogicalResourceName(new StorageAccountSpec { Name = c.StorageName });
        var envLogical = LogicalResourceName(new ManagedEnvironmentSpec { Name = c.EnvironmentName });
        var osLogical = LogicalResourceName(osApp);

        var resources = new StringBuilder();
        var allSecretSpecs = foundationSpecs.OfType<SecretSpec>()
            .Concat(moduleSecretSpecs)
            .Where(s => !s.External && !string.IsNullOrEmpty(s.Value))
            .ToArray();
        var secretValuesByName = allSecretSpecs.ToDictionary(s => s.Name, s => s.Value, StringComparer.Ordinal);

        var previousFoundryDeploymentByAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var spec in foundationSpecs)
        {
            string? foundryDependencyLogical = null;
            if (spec is FoundryDeploymentSpec foundryDeployment)
            {
                foundryDependencyLogical = previousFoundryDeploymentByAccount.TryGetValue(foundryDeployment.Account, out var prevLogical)
                    ? prevLogical
                    : LogicalResourceName(new FoundrySpec { Name = foundryDeployment.Account });
                previousFoundryDeploymentByAccount[foundryDeployment.Account] = LogicalResourceName(foundryDeployment);
            }
            AppendResourceYaml(resources, c, spec, secretValuesByName, foundryDependencyLogical);
        }

        foreach (var secretSpec in moduleSecretSpecs.Where(s => !s.External && !string.IsNullOrEmpty(s.Value)))
        {
            AppendResourceYaml(resources, c, secretSpec, secretValuesByName);
        }

        var roleGrantSpecs = foundationSpecs.OfType<RoleGrantSpec>().ToArray();

        AppendContainerAppYaml(resources, c, osApp, allSecretSpecs, roleGrantSpecs);
        if (cloudApp is not null)
        {
            AppendContainerAppYaml(resources, c, cloudApp, allSecretSpecs, roleGrantSpecs);
        }
        if (sandboxApp is not null)
        {
            AppendContainerAppYaml(resources, c, sandboxApp, allSecretSpecs, roleGrantSpecs);
        }
        if (devSlotJob is not null)
        {
            AppendContainerAppsJobYaml(resources, c, devSlotJob, roleGrantSpecs);
        }

        var (osMinReplicas, osMaxReplicas) = ResolveScale(osApp);
        var resourcesYaml = resources.ToString().ReplaceLineEndings("\n").TrimEnd('\n');
        return PulumiAssets.PulumiYamlTemplate
            .Replace("{{stackProjectName}}", StackProjectName, StringComparison.Ordinal)
            .Replace("{{env}}", c.Env, StringComparison.Ordinal)
            .Replace("{{rgLogical}}", rgLogical, StringComparison.Ordinal)
            .Replace("{{logLogical}}", logLogical, StringComparison.Ordinal)
            .Replace("{{storageLogical}}", stLogical, StringComparison.Ordinal)
            .Replace("{{resources}}", resourcesYaml, StringComparison.Ordinal)
            .Replace("{{groupName}}", c.GroupName, StringComparison.Ordinal)
            .Replace("{{vaultName}}", c.VaultName, StringComparison.Ordinal)
            .Replace("{{osAppName}}", osApp.Name, StringComparison.Ordinal)
            .Replace("{{osLogical}}", osLogical, StringComparison.Ordinal)
            .Replace("{{envLogical}}", envLogical, StringComparison.Ordinal)
            .Replace("{{osImage}}", osApp.Image, StringComparison.Ordinal)
            .Replace("{{osMinReplicas}}", osMinReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{osMaxReplicas}}", osMaxReplicas.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{moduleSecretsCount}}", osApp.Secrets.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{moduleVariablesCount}}", osApp.Variables.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static void AppendResourceYaml(StringBuilder sb, InstallConfig c, ResourceSpec spec, IReadOnlyDictionary<string, string> secretValuesByName, string? foundryDependencyLogical = null)
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
                if (log.DailyQuotaGb > 0)
                {
                    sb.AppendLine("      workspaceCapping:");
                    sb.AppendLine($"        dailyQuotaGb: {log.DailyQuotaGb.ToString(CultureInfo.InvariantCulture)}");
                }
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
                var pgPassValue = secretValuesByName.GetValueOrDefault(pg.AdminPasswordSecret, "");
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:dbforpostgresql:Server");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      serverName: \"{pg.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine($"      administratorLogin: \"{pg.AdminLogin}\"");
                sb.AppendLine($"      administratorLoginPassword: {YamlQuote(pgPassValue)}");
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

            case FoundrySpec foundry:
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:cognitiveservices:Account");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      accountName: \"{foundry.Name}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine($"      location: \"{c.Location}\"");
                sb.AppendLine("      kind: AIServices");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{foundry.Sku}\"");
                sb.AppendLine("      properties:");
                sb.AppendLine($"        customSubDomainName: \"{foundry.Name}\"");
                sb.AppendLine("        publicNetworkAccess: Enabled");
                sb.AppendLine("        allowProjectManagement: true");
                sb.AppendLine("        disableLocalAuth: true");
                AppendTags(sb, tags, indent: 6);
                break;

            case FoundryDeploymentSpec deployment:
                var foundryLogical = LogicalResourceName(new FoundrySpec { Name = deployment.Account });
                var deploymentDep = foundryDependencyLogical ?? foundryLogical;
                sb.AppendLine($"  {logical}:");
                sb.AppendLine("    type: azure-native:cognitiveservices:Deployment");
                sb.AppendLine("    options:");
                sb.AppendLine($"      dependsOn: [\"${{{deploymentDep}}}\"]");
                sb.AppendLine("    properties:");
                sb.AppendLine($"      deploymentName: \"{deployment.Name}\"");
                sb.AppendLine($"      accountName: \"${{{foundryLogical}.name}}\"");
                sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
                sb.AppendLine("      sku:");
                sb.AppendLine($"        name: \"{deployment.SkuName}\"");
                sb.AppendLine($"        capacity: {deployment.Capacity}");
                sb.AppendLine("      properties:");
                sb.AppendLine("        model:");
                sb.AppendLine($"          format: \"{deployment.ModelFormat}\"");
                sb.AppendLine($"          name: \"{deployment.ModelName}\"");
                if (!string.IsNullOrWhiteSpace(deployment.ModelVersion))
                {
                    sb.AppendLine($"          version: \"{deployment.ModelVersion}\"");
                }
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
                var isGuidPrincipal = Guid.TryParse(grant.Principal, out _);
                var principalExpr = isGuidPrincipal ? $"\"{grant.Principal}\"" : $"\"${{{idLogical}.principalId}}\"";
                var grantGuid = DeterministicGuid($"{grant.Scope}|{grant.Principal}|{grant.RoleDefinitionId}");
                var scopeDepLogical = grant.Scope switch
                {
                    var s when s.Contains("/Microsoft.KeyVault/vaults/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new KeyVaultSpec { Name = c.VaultName }),
                    var s when s.Contains("/Microsoft.Storage/storageAccounts/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new StorageAccountSpec { Name = c.StorageName }),
                    var s when s.Contains("/Microsoft.ContainerRegistry/registries/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new RegistrySpec { Name = c.RegistryName }),
                    var s when s.Contains("/Microsoft.CognitiveServices/accounts/", StringComparison.OrdinalIgnoreCase) => LogicalResourceName(new FoundrySpec { Name = c.FoundryName }),
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
                if (!isGuidPrincipal)
                {
                    sb.AppendLine("      principalType: ServicePrincipal");
                }
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

            case CostGuardrailSpec guardrail:
                AppendCostGuardrailPoliciesYaml(sb, c, guardrail);
                break;

            case BudgetKillSwitchSpec killSwitch:
                AppendBudgetKillSwitchYaml(sb, c, killSwitch, rgLogical, tags);
                break;
        }
    }

    private static void AppendContainerAppYaml(
        StringBuilder output,
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

        var sb = new StringBuilder();
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
        var targetPort = app.Ingress.TargetPort > 0 ? app.Ingress.TargetPort : app.IngressPort;
        sb.AppendLine("      configuration:");
        sb.AppendLine($"        activeRevisionsMode: {(app.StopBeforeUpdate ? "Multiple" : "Single")}");
        sb.AppendLine("        ingress:");
        sb.AppendLine($"          external: {(app.Ingress.External ? "true" : "false")}");
        sb.AppendLine($"          targetPort: {targetPort}");
        sb.AppendLine("          transport: Auto");
        if (app.StopBeforeUpdate)
        {
            sb.AppendLine("          traffic:");
            sb.AppendLine("            - latestRevision: true");
            sb.AppendLine("              weight: 100");
        }
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
            foreach (var secret in app.Secrets.DistinctBy(s => s.Name, StringComparer.Ordinal).OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                sb.AppendLine($"          - name: \"{secret.Name}\"");
                sb.AppendLine($"            keyVaultUrl: \"https://{secret.Vault}.vault.azure.net/secrets/{secret.VaultSecret}\"");
                sb.AppendLine($"            identity: \"{identityResourceId}\"");
            }
        }
        var (minReplicas, maxReplicas) = ResolveScale(app);
        var terminationGracePeriodSeconds = ResolveTerminationGracePeriodSeconds(app);
        sb.AppendLine("      template:");
        sb.AppendLine("        scale:");
        sb.AppendLine($"          minReplicas: {minReplicas}");
        sb.AppendLine($"          maxReplicas: {maxReplicas}");
        sb.AppendLine($"        terminationGracePeriodSeconds: {terminationGracePeriodSeconds}");
        sb.AppendLine("        containers:");
        sb.AppendLine("          - name: app");
        sb.AppendLine($"            image: \"{app.Image}\"");
        sb.AppendLine("            resources:");
        sb.AppendLine($"              cpu: {app.Cpu.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"              memory: \"{app.Memory}\"");
        if (app.ProbePaths.Length > 0)
        {
            ReadOnlySpan<string> probeTypes = ["Liveness", "Readiness", "Startup"];
            sb.AppendLine("            probes:");
            for (var i = 0; i < app.ProbePaths.Length && i < probeTypes.Length; i++)
            {
                var probeType = probeTypes[i];
                var failureThreshold = string.Equals(probeType, "Startup", StringComparison.Ordinal) ? 30 : 3;
                sb.AppendLine($"              - type: {probeType}");
                sb.AppendLine("                httpGet:");
                sb.AppendLine($"                  path: {YamlQuote(app.ProbePaths[i])}");
                sb.AppendLine($"                  port: {targetPort}");
                sb.AppendLine("                  scheme: HTTP");
                sb.AppendLine("                periodSeconds: 10");
                sb.AppendLine($"                failureThreshold: {failureThreshold}");
            }
        }
        sb.AppendLine("            env:");
        foreach (var (name, value) in app.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var resolved = value.Replace(ContainerAppSpec.IdentityClientIdToken, $"${{{identityLogical}.clientId}}", StringComparison.Ordinal);
            sb.AppendLine($"              - name: \"{name}\"");
            sb.AppendLine($"                value: {YamlQuote(resolved)}");
        }
        foreach (var secret in app.Secrets.DistinctBy(s => s.Variable, StringComparer.Ordinal).OrderBy(s => s.Variable, StringComparer.Ordinal))
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
        var properties = sb.ToString();

        var trigger = app.StopBeforeUpdate ? TemplateTrigger(properties) : null;
        if (trigger is not null)
        {
            AppendStopBeforeUpdate(output, c, app, logical + "-stop", trigger);
            dependsOn.Add(logical + "-stop");
        }
        output.AppendLine($"  {logical}:");
        output.AppendLine("    type: azure-native:app:ContainerApp");
        output.AppendLine("    options:");
        output.AppendLine($"      dependsOn: [{string.Join(", ", dependsOn.Distinct(StringComparer.Ordinal).Select(d => $"\"${{{d}}}\""))}]");
        output.AppendLine("      ignoreChanges:");
        output.AppendLine("        - configuration.ingress.customDomains");
        output.Append(properties);
        if (trigger is not null)
        {
            AppendStartAfterUpdate(output, c, app, logical + "-start", trigger, [logical, logical + "-stop"]);
        }
    }

    private static void AppendContainerAppsJobYaml(
        StringBuilder output,
        InstallConfig c,
        ContainerAppsJobSpec job,
        IReadOnlyList<RoleGrantSpec> roleGrants)
    {
        var logical = LogicalResourceName(job);
        var rgLogical = LogicalResourceName(new ResourceGroupSpec { Name = c.GroupName });
        var identityLogical = LogicalResourceName(new IdentitySpec { Name = job.Identity });
        var envLogical = LogicalResourceName(new ManagedEnvironmentSpec { Name = job.Environment });
        var identityResourceId = ResourceIds.For(c.Subscription, ResourceKinds.Identity, c.GroupName, job.Identity);
        var tags = AzureTags.Merge(job);

        var dependsOn = new List<string> { envLogical };
        if (job.RegistryServer.Length > 0 && job.Identity.Length > 0)
        {
            dependsOn.Add(identityLogical);
            foreach (var grant in roleGrants.Where(g => g.Scope.Contains("/Microsoft.ContainerRegistry/registries/", StringComparison.OrdinalIgnoreCase)))
            {
                dependsOn.Add(LogicalResourceName(grant));
            }
        }

        output.AppendLine($"  {logical}:");
        output.AppendLine("    type: azure-native:app:Job");
        output.AppendLine("    options:");
        output.AppendLine($"      dependsOn: [{string.Join(", ", dependsOn.Distinct(StringComparer.Ordinal).Select(d => $"\"${{{d}}}\""))}]");
        output.AppendLine("    properties:");
        output.AppendLine($"      jobName: \"{job.Name}\"");
        output.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        output.AppendLine($"      location: \"{c.Location}\"");
        output.AppendLine($"      environmentId: \"${{{envLogical}.id}}\"");
        output.AppendLine("      workloadProfileName: Consumption");
        if (job.RegistryServer.Length > 0 && job.Identity.Length > 0)
        {
            output.AppendLine("      identity:");
            output.AppendLine("        type: UserAssigned");
            output.AppendLine("        userAssignedIdentities:");
            output.AppendLine($"          - \"{identityResourceId}\"");
        }
        output.AppendLine("      configuration:");
        output.AppendLine("        triggerType: Manual");
        output.AppendLine($"        replicaTimeout: {job.ReplicaTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"        replicaRetryLimit: {job.ReplicaRetryLimit.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine("        manualTriggerConfig:");
        output.AppendLine($"          parallelism: {job.Parallelism.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"          replicaCompletionCount: {job.ReplicaCompletionCount.ToString(CultureInfo.InvariantCulture)}");
        if (job.RegistryServer.Length > 0 && job.Identity.Length > 0)
        {
            output.AppendLine("        registries:");
            output.AppendLine($"          - server: \"{job.RegistryServer}\"");
            output.AppendLine($"            identity: \"{identityResourceId}\"");
        }
        output.AppendLine("      template:");
        output.AppendLine("        containers:");
        output.AppendLine("          - name: devslot");
        output.AppendLine($"            image: \"{job.Image}\"");
        output.AppendLine("            resources:");
        output.AppendLine($"              cpu: {job.Cpu.ToString(CultureInfo.InvariantCulture)}");
        output.AppendLine($"              memory: \"{job.Memory}\"");
        if (job.Variables.Count > 0)
        {
            output.AppendLine("            env:");
            foreach (var (name, value) in job.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                output.AppendLine($"              - name: \"{name}\"");
                output.AppendLine($"                value: {YamlQuote(value)}");
            }
        }
        AppendTags(output, tags, indent: 6);
    }

    // Single-silo ContainerApps (StopBeforeUpdate = true) run Orleans with SingleSiloGuard:
    // enforce minReplicas: 1 and maxReplicas: 1 so Azure Container Apps never autoscales a conflicting replica.
    internal static (int MinReplicas, int MaxReplicas) ResolveScale(ContainerAppSpec app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.StopBeforeUpdate ? (1, 1) : (app.MinReplicas, app.MaxReplicas);
    }

    internal static readonly string DrainDelayVariable = ConfigurationKeyGrammar.EnvironmentName(HealthProbes.DrainDelayKey);

    // ACA must never send SIGKILL before the OS host finishes draining, stopping Kestrel, deactivating Orleans grains,
    // and writing Dead to cluster membership (ServerHostingExtensions.ShutdownTimeout).
    internal static int ResolveTerminationGracePeriodSeconds(ContainerAppSpec app)
    {
        ArgumentNullException.ThrowIfNull(app);
        TimeSpan? drainDelay = app.Variables.TryGetValue(DrainDelayVariable, out var rawDrain)
            && TimeSpan.TryParse(rawDrain, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        var minimum = (int)Math.Ceiling(ServerHostingExtensions.ShutdownTimeout(drainDelay, GrainCollectionOptions.DEFAULT_DEACTIVATION_TIMEOUT).TotalSeconds);
        return Math.Max(app.TerminationGracePeriodSeconds, minimum);
    }

    // Only a template change makes a new revision, so only the template section (not the tags) triggers the stop and start.
    private static string TemplateTrigger(string properties)
    {
        var start = properties.IndexOf("      template:", StringComparison.Ordinal);
        var tags = properties.IndexOf("      tags:", start, StringComparison.Ordinal);
        var template = tags < 0 ? properties[start..] : properties[start..tags];
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(template.ReplaceLineEndings("\n"))));
    }

    // Stop-then-start for an app that must run one silo (ContainerAppSpec.StopBeforeUpdate). Pulumi replaces this command
    // whenever the app's template changes, and the app depends on it, so the running revision is deactivated and its
    // replicas gone before the update creates the new revision; the start command after the app makes sure a revision runs
    // again. Before the first install the app does not exist and both do nothing. They need the Azure CLI, signed in to the
    // subscription, where pulumi runs.
    private static void AppendStopBeforeUpdate(StringBuilder sb, InstallConfig c, ContainerAppSpec app, string logical, string trigger)
    {
        var waits = (ResolveTerminationGracePeriodSeconds(app) + 60) / 5;
        var rendered = PulumiAssets.CommandStopYamlTemplate
            .Replace("{{logical}}", logical, StringComparison.Ordinal)
            .Replace("{{trigger}}", trigger, StringComparison.Ordinal)
            .Replace("{{appName}}", app.Name, StringComparison.Ordinal)
            .Replace("{{groupName}}", c.GroupName, StringComparison.Ordinal)
            .Replace("{{waits}}", waits.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{azRetry}}", PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(PulumiAssets.AzRetryFunction), 8), StringComparison.Ordinal)
            .Replace("{{stopBody}}", PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(PulumiAssets.StopRevisionsBody), 8), StringComparison.Ordinal);
        sb.Append(rendered);
        if (!rendered.EndsWith('\n'))
        {
            sb.Append('\n');
        }
    }

    // The other half of stop-then-start, after the app: it shares the stop's trigger, so it runs in every `pulumi up` that ran
    // the stop, and again in the next one when that run failed before reaching it. It activates the latest revision when the
    // stop left it inactive (an up that changed nothing else, or a run that stopped the app and went no further), waits for
    // it to become ready, and otherwise deactivates it, activates the last ready revision and fails the run.
    private static void AppendStartAfterUpdate(StringBuilder sb, InstallConfig c, ContainerAppSpec app, string logical, string trigger, string[] after)
    {
        var dependsOn = string.Join(", ", after.Select(d => $"\"${{{d}}}\""));
        var rendered = PulumiAssets.CommandStartYamlTemplate
            .Replace("{{logical}}", logical, StringComparison.Ordinal)
            .Replace("{{dependsOn}}", dependsOn, StringComparison.Ordinal)
            .Replace("{{trigger}}", trigger, StringComparison.Ordinal)
            .Replace("{{appName}}", app.Name, StringComparison.Ordinal)
            .Replace("{{groupName}}", c.GroupName, StringComparison.Ordinal)
            .Replace("{{azRetry}}", PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(PulumiAssets.AzRetryFunction), 8), StringComparison.Ordinal)
            .Replace("{{startBody}}", PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(PulumiAssets.StartRevisionBody), 8), StringComparison.Ordinal);
        sb.Append(rendered);
        if (!rendered.EndsWith('\n'))
        {
            sb.Append('\n');
        }
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
        => JsonSerializer.Serialize(value, PulumiYamlJsonContext.Default.String);
}

[JsonSerializable(typeof(string))]
internal sealed partial class PulumiYamlJsonContext : JsonSerializerContext;
