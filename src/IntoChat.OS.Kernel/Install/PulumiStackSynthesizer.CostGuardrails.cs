using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Kernel.Install;

public static partial class PulumiStackSynthesizer
{
    public const string PolicyDenyAcaSessionPools = "intochat-deny-aca-session-pools";
    public const string PolicyDenyAcaDedicatedAndScale = "intochat-deny-aca-dedicated-and-runaway-scale";
    public const string PolicyDenyExpensiveComputeAndDbSkus = "intochat-deny-expensive-compute-and-db-skus";
    public const string CostBreakerRunbookName = "Stop-IntoChatCostBreaker";

    private const string ContributorRoleDefinitionId = "b24988ac-6180-42a0-ab88-20f7382dd24c";
    private const string BudgetStartDateUtc = "2026-11-01T00:00:00Z";
    private const string BudgetEndDateUtc = "2031-11-01T00:00:00Z";
    private const string WebhookExpiryTimeUtc = "2030-12-31T00:00:00Z";

    private static void AppendCostGuardrailPoliciesYaml(StringBuilder sb, InstallConfig c, CostGuardrailSpec spec)
    {
        var subscriptionScope = $"/subscriptions/{c.Subscription}";
        var notScopes = ResolveProtectedNotScopes(c.Subscription, spec.ProtectedResourceGroups);

        // Rule A: Permanently ban Microsoft.App/sessionPools across the entire subscription (0 exceptions, no notScopes).
        const string ruleADefLogical = $"policy-def-{PolicyDenyAcaSessionPools}";
        const string ruleAAssignLogical = $"policy-assign-{PolicyDenyAcaSessionPools}";
        sb.AppendLine($"  {ruleADefLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyDefinition");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyDefinitionName: \"{PolicyDenyAcaSessionPools}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny ACA Dynamic Session Pools\"");
        sb.AppendLine("      description: \"Permanently bans Microsoft.App/sessionPools across the entire subscription (zero exceptions).\"");
        sb.AppendLine("      mode: All");
        sb.AppendLine("      policyType: Custom");
        sb.AppendLine("      policyRule:");
        sb.AppendLine("        if:");
        sb.AppendLine("          field: type");
        sb.AppendLine("          equals: Microsoft.App/sessionPools");
        sb.AppendLine("        then:");
        sb.AppendLine("          effect: deny");

        sb.AppendLine($"  {ruleAAssignLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyAssignment");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyAssignmentName: \"{PolicyDenyAcaSessionPools}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny ACA Dynamic Session Pools\"");
        sb.AppendLine("      description: \"Subscription-wide hard deny on Microsoft.App/sessionPools with zero exceptions.\"");
        sb.AppendLine($"      scope: \"{subscriptionScope}\"");
        sb.AppendLine($"      policyDefinitionId: \"${{{ruleADefLogical}.id}}\"");
        sb.AppendLine("      enforcementMode: Default");

        // Rule B: Deny non-Consumption ACA workload profiles and runaway ContainerApp scale/CPU.
        const string ruleBDefLogical = $"policy-def-{PolicyDenyAcaDedicatedAndScale}";
        const string ruleBAssignLogical = $"policy-assign-{PolicyDenyAcaDedicatedAndScale}";
        sb.AppendLine($"  {ruleBDefLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyDefinition");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyDefinitionName: \"{PolicyDenyAcaDedicatedAndScale}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny Dedicated ACA Profiles and Runaway Scale\"");
        sb.AppendLine("      description: \"Blocks non-Consumption ACA workload profiles and ContainerApps exceeding maxReplicas=2 or cpu=1.0.\"");
        sb.AppendLine("      mode: All");
        sb.AppendLine("      policyType: Custom");
        sb.AppendLine("      policyRule:");
        sb.AppendLine("        if:");
        sb.AppendLine("          anyOf:");
        sb.AppendLine("            - allOf:");
        sb.AppendLine("                - field: type");
        sb.AppendLine("                  equals: Microsoft.App/managedEnvironments");
        sb.AppendLine("                - count:");
        sb.AppendLine("                    field: Microsoft.App/managedEnvironments/workloadProfiles[*]");
        sb.AppendLine("                    where:");
        sb.AppendLine("                      field: Microsoft.App/managedEnvironments/workloadProfiles[*].workloadProfileType");
        sb.AppendLine("                      notEquals: Consumption");
        sb.AppendLine("                  greater: 0");
        sb.AppendLine("            - allOf:");
        sb.AppendLine("                - field: type");
        sb.AppendLine("                  equals: Microsoft.App/containerApps");
        sb.AppendLine("                - anyOf:");
        sb.AppendLine("                    - field: Microsoft.App/containerApps/template.scale.maxReplicas");
        sb.AppendLine("                      greater: 2");
        sb.AppendLine("                    - count:");
        sb.AppendLine("                        field: Microsoft.App/containerApps/template.containers[*]");
        sb.AppendLine("                        where:");
        sb.AppendLine("                          field: Microsoft.App/containerApps/template.containers[*].resources.cpu");
        sb.AppendLine("                          greater: 1");
        sb.AppendLine("                      greater: 0");
        sb.AppendLine("        then:");
        sb.AppendLine("          effect: deny");

        sb.AppendLine($"  {ruleBAssignLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyAssignment");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyAssignmentName: \"{PolicyDenyAcaDedicatedAndScale}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny Dedicated ACA Profiles and Runaway Scale\"");
        sb.AppendLine("      description: \"Enforces Consumption-only ACA environments and bounded ContainerApp scale/CPU, excluding protected resource groups.\"");
        sb.AppendLine($"      scope: \"{subscriptionScope}\"");
        sb.AppendLine($"      policyDefinitionId: \"${{{ruleBDefLogical}.id}}\"");
        sb.AppendLine("      enforcementMode: Default");
        AppendNotScopes(sb, notScopes);

        // Rule C: Deny expensive non-Burstable PostgreSQL SKUs, AKS, VMs, VMSS, and Provisioned Throughput Cognitive Services deployments.
        const string ruleCDefLogical = $"policy-def-{PolicyDenyExpensiveComputeAndDbSkus}";
        const string ruleCAssignLogical = $"policy-assign-{PolicyDenyExpensiveComputeAndDbSkus}";
        sb.AppendLine($"  {ruleCDefLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyDefinition");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyDefinitionName: \"{PolicyDenyExpensiveComputeAndDbSkus}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny Expensive Compute and DB SKUs\"");
        sb.AppendLine("      description: \"Blocks non-Burstable PostgreSQL Flexible Servers, AKS, VMs, VMSS, and Provisioned Throughput AI deployments.\"");
        sb.AppendLine("      mode: All");
        sb.AppendLine("      policyType: Custom");
        sb.AppendLine("      policyRule:");
        sb.AppendLine("        if:");
        sb.AppendLine("          anyOf:");
        sb.AppendLine("            - allOf:");
        sb.AppendLine("                - field: type");
        sb.AppendLine("                  equals: Microsoft.DBforPostgreSQL/flexibleServers");
        sb.AppendLine("                - field: Microsoft.DBforPostgreSQL/flexibleServers/sku.tier");
        sb.AppendLine("                  notEquals: Burstable");
        sb.AppendLine("            - field: type");
        sb.AppendLine("              in:");
        sb.AppendLine("                - Microsoft.ContainerService/managedClusters");
        sb.AppendLine("                - Microsoft.Compute/virtualMachines");
        sb.AppendLine("                - Microsoft.Compute/virtualMachineScaleSets");
        sb.AppendLine("            - allOf:");
        sb.AppendLine("                - field: type");
        sb.AppendLine("                  equals: Microsoft.CognitiveServices/accounts/deployments");
        sb.AppendLine("                - field: Microsoft.CognitiveServices/accounts/deployments/sku.name");
        sb.AppendLine("                  in:");
        sb.AppendLine("                    - ProvisionedManaged");
        sb.AppendLine("                    - GlobalProvisionedManaged");
        sb.AppendLine("        then:");
        sb.AppendLine("          effect: deny");

        sb.AppendLine($"  {ruleCAssignLogical}:");
        sb.AppendLine("    type: azure-native:authorization:PolicyAssignment");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      policyAssignmentName: \"{PolicyDenyExpensiveComputeAndDbSkus}\"");
        sb.AppendLine("      displayName: \"IntoChat: Deny Expensive Compute and DB SKUs\"");
        sb.AppendLine("      description: \"Enforces Burstable PostgreSQL and blocks expensive compute SKUs while excluding protected resource groups.\"");
        sb.AppendLine($"      scope: \"{subscriptionScope}\"");
        sb.AppendLine($"      policyDefinitionId: \"${{{ruleCDefLogical}.id}}\"");
        sb.AppendLine("      enforcementMode: Default");
        AppendNotScopes(sb, notScopes);
    }

    private static void AppendBudgetKillSwitchYaml(
        StringBuilder sb,
        InstallConfig c,
        BudgetKillSwitchSpec spec,
        string rgLogical,
        IReadOnlyDictionary<string, string> tags)
    {
        var subscriptionScope = $"/subscriptions/{c.Subscription}";
        var intoChatRgScope = $"/subscriptions/{c.Subscription}/resourceGroups/{c.GroupName}";
        var aaName = string.IsNullOrWhiteSpace(spec.AutomationAccountName)
            ? ResourceNames.For(c.Prefix, c.Env, "cost-aa")
            : spec.AutomationAccountName;
        var runbookName = string.IsNullOrWhiteSpace(spec.RunbookName) ? CostBreakerRunbookName : spec.RunbookName;
        var actionGroupName = string.IsNullOrWhiteSpace(spec.ActionGroupName)
            ? ResourceNames.For(c.Prefix, c.Env, "cost-ag")
            : spec.ActionGroupName;
        var budgetName = string.IsNullOrWhiteSpace(spec.BudgetName) ? "intochat-hard-cap-185eur" : spec.BudgetName;

        var aaLogical = $"automation-account-{SanitizeLogicalName(aaName)}";
        var rbacLogical = $"role-grant-{SanitizeLogicalName(aaName)}-rg-contributor";
        var runbookLogical = $"automation-runbook-{SanitizeLogicalName(runbookName)}";
        var runbookPublishLogical = $"{runbookLogical}-publish";
        var webhookUriLogical = $"automation-webhook-{SanitizeLogicalName(aaName)}-uri";
        var webhookLogical = $"automation-webhook-{SanitizeLogicalName(aaName)}";
        var agLogical = $"action-group-{SanitizeLogicalName(actionGroupName)}";
        var budgetLogical = $"consumption-budget-{SanitizeLogicalName(budgetName)}";

        // 1. Automation Account with System-Assigned Managed Identity (inside IntoChat RG).
        sb.AppendLine($"  {aaLogical}:");
        sb.AppendLine("    type: azure-native:automation:AutomationAccount");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      automationAccountName: \"{aaName}\"");
        sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine($"      location: \"{c.Location}\"");
        sb.AppendLine("      sku:");
        sb.AppendLine("        name: Basic");
        sb.AppendLine("      identity:");
        sb.AppendLine("        type: SystemAssigned");
        AppendTags(sb, tags, indent: 6);

        // 2. Contributor RBAC strictly scoped to the IntoChat resource group (NEVER subscription scope, NEVER LeedGenerator or sv).
        var rbacGuid = DeterministicGuid($"{intoChatRgScope}|{aaName}|{ContributorRoleDefinitionId}");
        sb.AppendLine($"  {rbacLogical}:");
        sb.AppendLine("    type: azure-native:authorization:RoleAssignment");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{rgLogical}}}\", \"${{{aaLogical}}}\"]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      roleAssignmentName: \"{rbacGuid}\"");
        sb.AppendLine($"      scope: \"{intoChatRgScope}\"");
        sb.AppendLine($"      principalId: \"${{{aaLogical}.identity.principalId}}\"");
        sb.AppendLine("      principalType: ServicePrincipal");
        sb.AppendLine($"      roleDefinitionId: \"/subscriptions/{c.Subscription}/providers/Microsoft.Authorization/roleDefinitions/{ContributorRoleDefinitionId}\"");

        // 3. PowerShell 7.2 Kill-Switch Runbook resource.
        sb.AppendLine($"  {runbookLogical}:");
        sb.AppendLine("    type: azure-native:automation:Runbook");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{aaLogical}}}\"]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      runbookName: \"{runbookName}\"");
        sb.AppendLine($"      automationAccountName: \"${{{aaLogical}.name}}\"");
        sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine($"      location: \"{c.Location}\"");
        sb.AppendLine("      runbookType: PowerShell72");
        sb.AppendLine("      logProgress: true");
        sb.AppendLine("      logVerbose: false");
        sb.AppendLine("      description: \"Automated IntoChat hard cost-cap kill-switch. Strictly scoped to intochat-* RGs with managed-by=intochat; excludes LeedGenerator, sv, and NetworkWatcherRG.\"");
        sb.AppendLine("      draft: {}");
        AppendTags(sb, tags, indent: 6);

        // 4. Upload and publish the PowerShell 7.2 Runbook script whenever its content hash changes.
        var runbookScript = BuildCostBreakerRunbookScript(c.Subscription, c.GroupName, spec.ProtectedResourceGroups);
        var scriptHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runbookScript)));
        var publishCommand = BuildRunbookPublishShellScript(c.Subscription, c.GroupName, aaName, runbookName, runbookScript);

        sb.AppendLine($"  {runbookPublishLogical}:");
        sb.AppendLine("    type: command:local:Command");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{runbookLogical}}}\"]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      triggers: [\"{scriptHash}\"]");
        sb.AppendLine("      create: |");
        sb.AppendLine(PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(publishCommand), 8));

        // 5. Generate Azure Automation webhook URI once per Automation Account lifecycle (encrypt stdout as a Pulumi secret).
        var generateWebhookUriCommand = BuildGenerateWebhookUriShellScript(c.Subscription, c.GroupName, aaName);
        sb.AppendLine($"  {webhookUriLogical}:");
        sb.AppendLine("    type: command:local:Command");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{aaLogical}}}\"]");
        sb.AppendLine("      additionalSecretOutputs:");
        sb.AppendLine("        - stdout");
        sb.AppendLine("    properties:");
        sb.AppendLine("      create: |");
        sb.AppendLine(PulumiAssets.Indent(PulumiAssets.EscapePulumiYaml(generateWebhookUriCommand), 8));

        // 6. Azure Automation Webhook linked to Stop-IntoChatCostBreaker.
        sb.AppendLine($"  {webhookLogical}:");
        sb.AppendLine("    type: azure-native:automation:Webhook");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{runbookPublishLogical}}}\", \"${{{webhookUriLogical}}}\"]");
        sb.AppendLine("      ignoreChanges:");
        sb.AppendLine("        - uri");
        sb.AppendLine("        - expiryTime");
        sb.AppendLine("    properties:");
        sb.AppendLine("      webhookName: \"cost-breaker-webhook\"");
        sb.AppendLine("      name: \"cost-breaker-webhook\"");
        sb.AppendLine($"      automationAccountName: \"${{{aaLogical}.name}}\"");
        sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine("      isEnabled: true");
        sb.AppendLine($"      expiryTime: \"{WebhookExpiryTimeUtc}\"");
        sb.AppendLine($"      uri: \"${{{webhookUriLogical}.stdout}}\"");
        sb.AppendLine("      runbook:");
        sb.AppendLine($"        name: \"${{{runbookLogical}.name}}\"");

        // 7. Azure Monitor Action Group (email + Automation Runbook receiver).
        sb.AppendLine($"  {agLogical}:");
        sb.AppendLine("    type: azure-native:monitor:ActionGroup");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{webhookLogical}}}\"]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      actionGroupName: \"{actionGroupName}\"");
        sb.AppendLine($"      resourceGroupName: \"${{{rgLogical}.name}}\"");
        sb.AppendLine("      location: Global");
        sb.AppendLine("      enabled: true");
        sb.AppendLine("      groupShortName: \"IntochatCap\"");
        sb.AppendLine("      emailReceivers:");
        sb.AppendLine("        - name: \"owner-email\"");
        sb.AppendLine($"          emailAddress: {YamlQuote(spec.AlertEmail)}");
        sb.AppendLine("          useCommonAlertSchema: true");
        sb.AppendLine("      automationRunbookReceivers:");
        sb.AppendLine("        - name: \"cost-breaker-runbook\"");
        sb.AppendLine($"          automationAccountId: \"${{{aaLogical}.id}}\"");
        sb.AppendLine($"          runbookName: \"${{{runbookLogical}.name}}\"");
        sb.AppendLine($"          webhookResourceId: \"${{{webhookLogical}.id}}\"");
        sb.AppendLine($"          serviceUri: \"${{{webhookUriLogical}.stdout}}\"");
        sb.AppendLine("          isGlobalRunbook: false");
        sb.AppendLine("          useCommonAlertSchema: true");
        AppendTags(sb, tags, indent: 6);

        // 8. Subscription-scoped Azure Consumption Budget (50%, 80%, 90% Forecasted email; 95% & 100% Actual email + ActionGroup kill-switch).
        sb.AppendLine($"  {budgetLogical}:");
        sb.AppendLine("    type: azure-native:consumption:Budget");
        sb.AppendLine("    options:");
        sb.AppendLine($"      dependsOn: [\"${{{agLogical}}}\"]");
        sb.AppendLine("    properties:");
        sb.AppendLine($"      budgetName: \"{budgetName}\"");
        sb.AppendLine($"      scope: \"{subscriptionScope}\"");
        sb.AppendLine("      category: Cost");
        sb.AppendLine($"      amount: {spec.MonthlyBudgetEur.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("      timeGrain: Monthly");
        sb.AppendLine("      timePeriod:");
        sb.AppendLine($"        startDate: \"{BudgetStartDateUtc}\"");
        sb.AppendLine($"        endDate: \"{BudgetEndDateUtc}\"");
        sb.AppendLine("      notifications:");
        AppendBudgetNotification(sb, "actual-50pct", threshold: 50, thresholdType: "Actual", spec.AlertEmail, actionGroupLogical: null);
        AppendBudgetNotification(sb, "actual-80pct", threshold: 80, thresholdType: "Actual", spec.AlertEmail, actionGroupLogical: null);
        AppendBudgetNotification(sb, "forecasted-90pct", threshold: 90, thresholdType: "Forecasted", spec.AlertEmail, actionGroupLogical: null);
        AppendBudgetNotification(sb, "actual-95pct-kill", threshold: 95, thresholdType: "Actual", spec.AlertEmail, actionGroupLogical: agLogical);
        AppendBudgetNotification(sb, "actual-100pct-kill", threshold: 100, thresholdType: "Actual", spec.AlertEmail, actionGroupLogical: agLogical);
    }

    private static void AppendBudgetNotification(
        StringBuilder sb,
        string key,
        int threshold,
        string thresholdType,
        string alertEmail,
        string? actionGroupLogical)
    {
        sb.AppendLine($"        {key}:");
        sb.AppendLine("          enabled: true");
        sb.AppendLine("          operator: GreaterThanOrEqualTo");
        sb.AppendLine($"          threshold: {threshold}");
        sb.AppendLine($"          thresholdType: {thresholdType}");
        sb.AppendLine("          contactEmails:");
        sb.AppendLine($"            - {YamlQuote(alertEmail)}");
        if (!string.IsNullOrEmpty(actionGroupLogical))
        {
            sb.AppendLine("          contactGroups:");
            sb.AppendLine($"            - \"${{{actionGroupLogical}.id}}\"");
        }
    }

    private static void AppendNotScopes(StringBuilder sb, IReadOnlyList<string> notScopes)
    {
        sb.AppendLine("      notScopes:");
        foreach (var notScope in notScopes)
        {
            sb.AppendLine($"        - \"{notScope}\"");
        }
    }

    private static string[] ResolveProtectedNotScopes(string subscription, IReadOnlyList<string>? protectedResourceGroups)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var required in CostGuardrailConfig.DefaultProtectedResourceGroups)
        {
            if (seen.Add(required))
            {
                names.Add(required);
            }
        }
        if (protectedResourceGroups is not null)
        {
            foreach (var rg in protectedResourceGroups)
            {
                var trimmed = rg?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                {
                    names.Add(trimmed);
                }
            }
        }

        return [.. names.Select(rg => $"/subscriptions/{subscription}/resourceGroups/{rg}")];
    }

    private static string SanitizeLogicalName(string raw)
        => new string([.. raw.ToLowerInvariant().Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-')]).Trim('-');

    internal static string BuildCostBreakerRunbookScript(string subscriptionId, string targetResourceGroup, IReadOnlyList<string>? protectedResourceGroups)
    {
        var excluded = new List<string> { "LeedGenerator", "leedgenerator", "sv", "NetworkWatcherRG" };
        var seen = new HashSet<string>(excluded, StringComparer.Ordinal);
        if (protectedResourceGroups is not null)
        {
            foreach (var rg in protectedResourceGroups)
            {
                var trimmed = rg?.Trim();
                if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
                {
                    excluded.Add(trimmed);
                }
            }
        }
        var excludedLiteral = string.Join(", ", excluded.Select(rg => $"'{rg.Replace("'", "''", StringComparison.Ordinal)}'"));

        return $$"""
            param(
                [Parameter(Mandatory = $false)]
                [object]$WebhookData
            )

            $ErrorActionPreference = 'Stop'

            $SubscriptionId = '{{subscriptionId}}'
            $TargetResourceGroup = '{{targetResourceGroup}}'
            $ExcludedResourceGroups = @({{excludedLiteral}})

            function Test-IsExcludedResourceGroup {
                param([string]$Name)
                foreach ($excludedName in $ExcludedResourceGroups) {
                    if ([string]::Equals($Name, $excludedName, [System.StringComparison]::OrdinalIgnoreCase)) {
                        return $true
                    }
                }
                return $false
            }

            if ([string]::IsNullOrWhiteSpace($env:IDENTITY_ENDPOINT) -or [string]::IsNullOrWhiteSpace($env:IDENTITY_HEADER)) {
                throw 'System-Assigned Managed Identity environment variables (IDENTITY_ENDPOINT / IDENTITY_HEADER) are not available.'
            }

            $tokenUri = $env:IDENTITY_ENDPOINT + '?resource=https://management.azure.com/&api-version=2019-08-01'
            $tokenResponse = Invoke-RestMethod -Method GET -Uri $tokenUri -Headers @{ 'X-IDENTITY-HEADER' = $env:IDENTITY_HEADER }
            $accessToken = $tokenResponse.access_token
            if ([string]::IsNullOrWhiteSpace($accessToken)) {
                throw 'Failed to acquire ARM access token from Automation Account System-Assigned Managed Identity.'
            }

            $headers = @{
                'Authorization' = 'Bearer ' + $accessToken
                'Content-Type'  = 'application/json'
            }

            $rgUri = 'https://management.azure.com/subscriptions/' + $SubscriptionId + '/resourceGroups/' + $TargetResourceGroup + '?api-version=2024-03-01'
            $rg = Invoke-RestMethod -Method GET -Uri $rgUri -Headers $headers
            $rgName = [string]$rg.name

            if (Test-IsExcludedResourceGroup -Name $rgName) {
                Write-Output ('SKIP protected resource group: ' + $rgName)
                return
            }

            if ($rgName -notmatch '^intochat-') {
                Write-Output ('SKIP non-intochat resource group (prefix allowlist): ' + $rgName)
                return
            }

            $rgManagedBy = ''
            if ($null -ne $rg.tags -and $null -ne $rg.tags.'managed-by') {
                $rgManagedBy = [string]$rg.tags.'managed-by'
            }
            $rgHasManagedByTag = [string]::Equals($rgManagedBy, 'intochat', [System.StringComparison]::OrdinalIgnoreCase)

            $stoppedResources = New-Object System.Collections.Generic.List[string]

            # 1. Stop all Microsoft.App/containerApps in the matching intochat-* RG.
            $appsUri = 'https://management.azure.com/subscriptions/' + $SubscriptionId + '/resourceGroups/' + $rgName + '/providers/Microsoft.App/containerApps?api-version=2025-01-01'
            $appsResponse = Invoke-RestMethod -Method GET -Uri $appsUri -Headers $headers
            foreach ($app in $appsResponse.value) {
                $appManagedBy = ''
                if ($null -ne $app.tags -and $null -ne $app.tags.'managed-by') {
                    $appManagedBy = [string]$app.tags.'managed-by'
                }
                $isManaged = $rgHasManagedByTag -or [string]::Equals($appManagedBy, 'intochat', [System.StringComparison]::OrdinalIgnoreCase)
                if (-not $isManaged) {
                    Write-Output ('SKIP untagged ContainerApp (missing managed-by=intochat): ' + $app.id)
                    continue
                }
                $stopAppUri = 'https://management.azure.com' + $app.id + '/stop?api-version=2025-01-01'
                Write-Output ('STOPPING ContainerApp: ' + $app.id)
                Invoke-RestMethod -Method POST -Uri $stopAppUri -Headers $headers | Out-Null
                $stoppedResources.Add([string]$app.id)
            }

            # 2. Stop all running Microsoft.DBforPostgreSQL/flexibleServers in the matching intochat-* RG.
            $pgUri = 'https://management.azure.com/subscriptions/' + $SubscriptionId + '/resourceGroups/' + $rgName + '/providers/Microsoft.DBforPostgreSQL/flexibleServers?api-version=2024-08-01'
            $pgResponse = Invoke-RestMethod -Method GET -Uri $pgUri -Headers $headers
            foreach ($server in $pgResponse.value) {
                $pgManagedBy = ''
                if ($null -ne $server.tags -and $null -ne $server.tags.'managed-by') {
                    $pgManagedBy = [string]$server.tags.'managed-by'
                }
                $isManaged = $rgHasManagedByTag -or [string]::Equals($pgManagedBy, 'intochat', [System.StringComparison]::OrdinalIgnoreCase)
                if (-not $isManaged) {
                    Write-Output ('SKIP untagged PostgreSQL server (missing managed-by=intochat): ' + $server.id)
                    continue
                }
                $state = [string]$server.properties.state
                if ([string]::Equals($state, 'Stopped', [System.StringComparison]::OrdinalIgnoreCase)) {
                    Write-Output ('ALREADY STOPPED PostgreSQL server: ' + $server.id)
                    continue
                }
                $stopPgUri = 'https://management.azure.com' + $server.id + '/stop?api-version=2024-08-01'
                Write-Output ('STOPPING PostgreSQL server: ' + $server.id)
                Invoke-RestMethod -Method POST -Uri $stopPgUri -Headers $headers | Out-Null
                $stoppedResources.Add([string]$server.id)
            }

            Write-Output ('IntoChat Cost Breaker complete. Stopped ' + $stoppedResources.Count + ' resource(s):')
            foreach ($rid in $stoppedResources) {
                Write-Output ('  STOPPED: ' + $rid)
            }
            """.ReplaceLineEndings("\n");
    }

    private static string BuildRunbookPublishShellScript(
        string subscriptionId,
        string resourceGroup,
        string automationAccountName,
        string runbookName,
        string runbookScript)
    {
        var baseUri = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Automation/automationAccounts/{automationAccountName}/runbooks/{runbookName}";
        return $$"""
            set -eu
            _tmp="$(mktemp)"
            trap 'rm -f "$_tmp"' EXIT
            cat <<'INTOCHAT_RUNBOOK_EOF' > "$_tmp"
            {{runbookScript}}
            INTOCHAT_RUNBOOK_EOF
            az rest --method PUT --uri "{{baseUri}}/draft/content?api-version=2023-11-01" --headers "Content-Type=text/powershell" --body @"$_tmp" >/dev/null
            az rest --method POST --uri "{{baseUri}}/publish?api-version=2023-11-01" >/dev/null
            echo "Published runbook {{runbookName}}"
            """.ReplaceLineEndings("\n");
    }

    private static string BuildGenerateWebhookUriShellScript(
        string subscriptionId,
        string resourceGroup,
        string automationAccountName)
    {
        var uri = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Automation/automationAccounts/{automationAccountName}/webhooks/generateUri?api-version=2015-10-31";
        return $$"""
            set -eu
            az rest --method POST --uri "{{uri}}" --output tsv | tr -d '\r\n'
            """.ReplaceLineEndings("\n");
    }
}
