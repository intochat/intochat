using System.Text.RegularExpressions;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Owner-approved Two-Layer Azure Hard Cost Cap & Pulumi Guardrails (INT-2561 / Item #371):
// - Zero Microsoft.App/sessionPools resources synthesized; permanently banned via subscription-wide Deny policy.
// - Default Postgres SKU is Burstable Standard_B1ms for both test and prod; Log Analytics dailyQuotaGb = 0.2.
// - Layer 1 SKU/scale policies exclude LeedGenerator, sv, and NetworkWatcherRG via notScopes.
// - Layer 2 Automation Account Managed Identity RBAC is strictly scoped to the IntoChat RG (never subscription, never LeedGenerator/sv).
// - Kill-switch PowerShell 7.2 runbook enforces ^intochat- RG prefix, managed-by=intochat tag, and hard-coded exclusions.
public sealed partial class PulumiCostGuardrailsFacts
{
    private const string SubscriptionId = "2a985480-6bb9-44ff-959d-edd5efe2cdb2";

    [GeneratedRegex(@"^  ([a-z0-9][a-z0-9-]*):$", RegexOptions.Multiline)]
    private static partial Regex ResourceHeaderRegex();

    [GeneratedRegex(@"(?<!\$)\$\{([^}]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PulumiInterpolationRegex();

    [GeneratedRegex(@"^\s+groupShortName:\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex GroupShortNameRegex();

    [GeneratedRegex(@"^\s+policyDefinitionName:\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex PolicyDefinitionNameRegex();

    [GeneratedRegex(@"^\s+policyAssignmentName:\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex PolicyAssignmentNameRegex();

    [GeneratedRegex(@"^\s+budgetName:\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex BudgetNameRegex();

    [Fact]
    public void UniversalConfigDefaultsPostgresToBurstableInTestAndProdAndEnablesCostGuardrails()
    {
        foreach (var env in new[] { "test", "prod" })
        {
            var config = InstallConfig.Universal(env: env, subscription: SubscriptionId);

            Assert.True(config.Postgres.Enabled);
            Assert.Equal("Standard_B1ms", config.Postgres.Sku);
            Assert.Equal("Burstable", config.Postgres.Tier);

            Assert.True(config.CostGuardrails.Enabled);
            Assert.Equal(185.0, config.CostGuardrails.MonthlyBudgetEur);
            Assert.Equal("vlad@intochat.io", config.CostGuardrails.AlertEmail);
            Assert.Contains("LeedGenerator", config.CostGuardrails.ProtectedResourceGroups);
            Assert.Contains("sv", config.CostGuardrails.ProtectedResourceGroups);
            Assert.Contains("NetworkWatcherRG", config.CostGuardrails.ProtectedResourceGroups);
        }

        // Omitting costGuardrails from JSON still enables defaults and preserves LeedGenerator + sv protection.
        var parsed = InstallConfig.Parse("""
            {
              "subscription": "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
              "location": "polandcentral",
              "prefix": "intochat",
              "env": "prod",
              "osImage": "acrintochatprod.azurecr.io/os:latest"
            }
            """);

        Assert.True(parsed.CostGuardrails.Enabled);
        Assert.Equal(185.0, parsed.CostGuardrails.MonthlyBudgetEur);
        Assert.Equal("vlad@intochat.io", parsed.CostGuardrails.AlertEmail);
        Assert.Contains("LeedGenerator", parsed.CostGuardrails.ProtectedResourceGroups);
        Assert.Contains("sv", parsed.CostGuardrails.ProtectedResourceGroups);
        Assert.Equal(0.2, parsed.LogWorkspace.DailyQuotaGb);

        // Explicit logWorkspace.dailyQuotaGb override round-trips cleanly into workspaceCapping.dailyQuotaGb.
        var overridden = InstallConfig.Parse("""
            {
              "subscription": "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
              "location": "polandcentral",
              "prefix": "intochat",
              "env": "prod",
              "osImage": "acrintochatprod.azurecr.io/os:latest",
              "logWorkspace": { "dailyQuotaGb": 0.5 }
            }
            """);
        Assert.Equal(0.5, overridden.LogWorkspace.DailyQuotaGb);
        var overriddenYaml = PulumiStackSynthesizer.Synthesize(overridden, OperatingSystemModules.Modules).PulumiYaml;
        var overriddenLogBlock = ResourceBlock(overriddenYaml, $"log-workspace-{overridden.LogsName}");
        Assert.Contains("      workspaceCapping:\n        dailyQuotaGb: 0.5\n", overriddenLogBlock, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => InstallConfig.Parse("""
            {
              "subscription": "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
              "location": "polandcentral",
              "prefix": "intochat",
              "env": "prod",
              "osImage": "acrintochatprod.azurecr.io/os:latest",
              "logWorkspace": { "dailyQuotaGb": 0 }
            }
            """));
    }

    [Fact]
    public void SynthesizedStackEmitsLogWorkspaceDailyQuotaAndZeroSessionPoolResources()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: SubscriptionId,
            includeCloudApp: true,
            includeSandboxApp: true);

        var plan = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules);
        var yaml = plan.PulumiYaml;

        // Zero SessionPool resources synthesized.
        Assert.DoesNotContain(plan.Resources, static r => r.Kind.Contains("sessionPool", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("type: azure-native:app:SessionPool", yaml, StringComparison.OrdinalIgnoreCase);

        // LogWorkspace daily ingestion hard cap = 0.2 GB/day.
        var logBlock = ResourceBlock(yaml, $"log-workspace-{config.LogsName}");
        Assert.Contains("type: azure-native:operationalinsights:Workspace", logBlock, StringComparison.Ordinal);
        Assert.Contains("      workspaceCapping:\n        dailyQuotaGb: 0.2\n", logBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void Layer1AzurePoliciesEnforceDenyRulesAndProtectLeedGeneratorAndSv()
    {
        var config = InstallConfig.Universal(env: "test", subscription: SubscriptionId) with
        {
            CostGuardrails = new CostGuardrailConfig
            {
                Enabled = true,
                ProtectedResourceGroups = ["CustomProtectedRG"],
            },
        };

        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;
        var subScope = $"/subscriptions/{SubscriptionId}";
        var leedScope = $"{subScope}/resourceGroups/LeedGenerator";
        var svScope = $"{subScope}/resourceGroups/sv";
        var nwScope = $"{subScope}/resourceGroups/NetworkWatcherRG";
        var customScope = $"{subScope}/resourceGroups/CustomProtectedRG";

        var ruleAName = PulumiStackSynthesizer.PolicyName(config, PulumiStackSynthesizer.PolicyDenyAcaSessionPoolsSuffix);
        var ruleBName = PulumiStackSynthesizer.PolicyName(config, PulumiStackSynthesizer.PolicyDenyAcaDedicatedAndScaleSuffix);
        var ruleCName = PulumiStackSynthesizer.PolicyName(config, PulumiStackSynthesizer.PolicyDenyExpensiveComputeAndDbSkusSuffix);

        // Rule A: Subscription-wide Deny on Microsoft.App/sessionPools with 0 exceptions (no notScopes).
        var ruleADef = ResourceBlock(yaml, $"policy-def-{ruleAName}");
        Assert.Contains("type: azure-native:authorization:PolicyDefinition", ruleADef, StringComparison.Ordinal);
        Assert.Contains("equals: Microsoft.App/sessionPools", ruleADef, StringComparison.Ordinal);
        Assert.Contains("effect: deny", ruleADef, StringComparison.Ordinal);

        var ruleAAssign = ResourceBlock(yaml, $"policy-assign-{ruleAName}");
        Assert.Contains("type: azure-native:authorization:PolicyAssignment", ruleAAssign, StringComparison.Ordinal);
        Assert.Contains($"scope: \"{subScope}\"", ruleAAssign, StringComparison.Ordinal);
        Assert.Contains($"policyDefinitionId: \"${{policy-def-{ruleAName}.id}}\"", ruleAAssign, StringComparison.Ordinal);
        Assert.DoesNotContain("notScopes:", ruleAAssign, StringComparison.Ordinal);

        // Rule B: Deny non-Consumption ACA profiles, runaway ContainerApp scale/CPU, and non-compliant Microsoft.App/jobs; excludes LeedGenerator + sv + NetworkWatcherRG + custom.
        var ruleBDef = ResourceBlock(yaml, $"policy-def-{ruleBName}");
        Assert.Contains("type: azure-native:authorization:PolicyDefinition", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.App/managedEnvironments/workloadProfiles[*].workloadProfileType", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("notEquals: Consumption", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.App/containerApps/template.scale.maxReplicas", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("greater: 2", ruleBDef, StringComparison.Ordinal);
        Assert.Contains(
            "field: Microsoft.App/containerApps/template.containers[*].resources.cpu\n" +
            "                          notIn:\n" +
            "                            - 0.25\n" +
            "                            - 0.5\n" +
            "                            - 0.75\n" +
            "                            - 1\n",
            ruleBDef,
            StringComparison.Ordinal);
        // All 7 Security-approved Policy Rule B ceilings on Microsoft.App/jobs:
        Assert.Contains("equals: Microsoft.App/jobs", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/workloadProfileName\n                      notEquals: Consumption", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/configuration.triggerType\n                      notEquals: Manual", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/configuration.manualTriggerConfig.parallelism\n                      greater: 2", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/configuration.replicaTimeout\n                      greater: 900", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/configuration.replicaRetryLimit\n                      notEquals: 0", ruleBDef, StringComparison.Ordinal);
        Assert.Contains(
            "field: Microsoft.App/jobs/template.containers[*].resources.cpu\n" +
            "                          notIn:\n" +
            "                            - 0.25\n" +
            "                            - 0.5\n" +
            "                            - 0.75\n" +
            "                            - 1\n" +
            "                            - 1.25\n" +
            "                            - 1.5\n" +
            "                            - 1.75\n" +
            "                            - 2\n",
            ruleBDef,
            StringComparison.Ordinal);
        // Regression guard against Azure Policy InvalidPolicyEvaluation (float/int type mismatch on greater/less):
        Assert.DoesNotContain("resources.cpu\n                          greater:", ruleBDef, StringComparison.Ordinal);
        Assert.DoesNotContain("resources.cpu\n                          greaterOrEquals:", ruleBDef, StringComparison.Ordinal);
        Assert.DoesNotContain("resources.cpu\n                          less:", ruleBDef, StringComparison.Ordinal);
        Assert.DoesNotContain("resources.cpu\n                          lessOrEquals:", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("field: Microsoft.App/jobs/template.containers[*].resources.memory\n                          notIn:", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("- \"4Gi\"", ruleBDef, StringComparison.Ordinal);
        Assert.DoesNotContain("- \"8Gi\"", ruleBDef, StringComparison.Ordinal);
        Assert.Contains("effect: deny", ruleBDef, StringComparison.Ordinal);

        var ruleBAssign = ResourceBlock(yaml, $"policy-assign-{ruleBName}");
        Assert.Contains("type: azure-native:authorization:PolicyAssignment", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains($"scope: \"{subScope}\"", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains("notScopes:", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{leedScope}\"", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{svScope}\"", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{nwScope}\"", ruleBAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{customScope}\"", ruleBAssign, StringComparison.Ordinal);

        // Rule C: Deny non-Burstable PostgreSQL, AKS, VMs, VMSS, and Provisioned Throughput Cognitive Services deployments; excludes LeedGenerator + sv.
        var ruleCDef = ResourceBlock(yaml, $"policy-def-{ruleCName}");
        Assert.Contains("type: azure-native:authorization:PolicyDefinition", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DBforPostgreSQL/flexibleServers/sku.tier", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("notEquals: Burstable", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.ContainerService/managedClusters", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Compute/virtualMachines", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Compute/virtualMachineScaleSets", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("ProvisionedManaged", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("GlobalProvisionedManaged", ruleCDef, StringComparison.Ordinal);
        Assert.Contains("effect: deny", ruleCDef, StringComparison.Ordinal);

        var ruleCAssign = ResourceBlock(yaml, $"policy-assign-{ruleCName}");
        Assert.Contains("type: azure-native:authorization:PolicyAssignment", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains($"scope: \"{subScope}\"", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains("notScopes:", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{leedScope}\"", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{svScope}\"", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{nwScope}\"", ruleCAssign, StringComparison.Ordinal);
        Assert.Contains($"- \"{customScope}\"", ruleCAssign, StringComparison.Ordinal);

        // ARM naming limits: policy definition & assignment names must be <= 64 chars.
        foreach (Match match in PolicyDefinitionNameRegex().Matches(yaml))
        {
            Assert.True(match.Groups[1].Value.Length <= 64, $"PolicyDefinition name '{match.Groups[1].Value}' exceeds 64 chars.");
        }
        foreach (Match match in PolicyAssignmentNameRegex().Matches(yaml))
        {
            Assert.True(match.Groups[1].Value.Length <= 64, $"PolicyAssignment name '{match.Groups[1].Value}' exceeds 64 chars.");
        }
    }

    [Fact]
    public void Layer2BudgetKillSwitchEnforcesStrictRgRbacScopeActionGroupAndRunbookGuardrails()
    {
        var config = InstallConfig.Universal(env: "test", subscription: SubscriptionId);
        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        var subScope = $"/subscriptions/{SubscriptionId}";
        var intoChatRgScope = $"{subScope}/resourceGroups/{config.GroupName}";

        // 1. Automation Account with SystemAssigned identity inside IntoChat RG.
        var aaBlock = ResourceBlock(yaml, "automation-account-intochat-test-cost-aa");
        Assert.Contains("type: azure-native:automation:AutomationAccount", aaBlock, StringComparison.Ordinal);
        Assert.Contains("name: Basic", aaBlock, StringComparison.Ordinal);
        Assert.Contains("type: SystemAssigned", aaBlock, StringComparison.Ordinal);
        Assert.Contains($"resourceGroupName: \"${{resource-group-{config.GroupName}.name}}\"", aaBlock, StringComparison.Ordinal);

        // 2. RoleAssignment strictly scoped to IntoChat RG — NEVER subscription scope and NEVER LeedGenerator/sv.
        var rbacBlock = ResourceBlock(yaml, "role-grant-intochat-test-cost-aa-rg-contributor");
        Assert.Contains("type: azure-native:authorization:RoleAssignment", rbacBlock, StringComparison.Ordinal);
        Assert.Contains($"scope: \"{intoChatRgScope}\"", rbacBlock, StringComparison.Ordinal);
        Assert.DoesNotContain($"scope: \"{subScope}\"", rbacBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("LeedGenerator", rbacBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/resourceGroups/sv", rbacBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("principalId: \"${automation-account-intochat-test-cost-aa.identity.principalId}\"", rbacBlock, StringComparison.Ordinal);
        Assert.Contains("principalType: ServicePrincipal", rbacBlock, StringComparison.Ordinal);
        Assert.Contains("b24988ac-6180-42a0-ab88-20f7382dd24c", rbacBlock, StringComparison.Ordinal);

        // 3. Runbook + publish command + Webhook.
        var runbookBlock = ResourceBlock(yaml, "automation-runbook-stop-intochatcostbreaker");
        Assert.Contains("type: azure-native:automation:Runbook", runbookBlock, StringComparison.Ordinal);
        Assert.Contains("runbookName: \"Stop-IntoChatCostBreaker\"", runbookBlock, StringComparison.Ordinal);
        Assert.Contains("runbookType: PowerShell72", runbookBlock, StringComparison.Ordinal);

        var publishBlock = ResourceBlock(yaml, "automation-runbook-stop-intochatcostbreaker-publish");
        Assert.Contains("type: command:local:Command", publishBlock, StringComparison.Ordinal);
        Assert.Contains("$ExcludedResourceGroups = @('LeedGenerator', 'leedgenerator', 'sv', 'NetworkWatcherRG')", publishBlock, StringComparison.Ordinal);
        Assert.Contains("^intochat-", publishBlock, StringComparison.Ordinal);
        Assert.Contains("'managed-by'", publishBlock, StringComparison.Ordinal);
        Assert.Contains("'intochat'", publishBlock, StringComparison.Ordinal);
        Assert.Contains("IDENTITY_ENDPOINT", publishBlock, StringComparison.Ordinal);
        Assert.Contains("IDENTITY_HEADER", publishBlock, StringComparison.Ordinal);
        Assert.Contains("Microsoft.App/containerApps", publishBlock, StringComparison.Ordinal);
        Assert.Contains("/stop?api-version=2025-01-01", publishBlock, StringComparison.Ordinal);
        Assert.Contains("Microsoft.App/jobs", publishBlock, StringComparison.Ordinal);
        Assert.Contains("/executions?api-version=2025-01-01", publishBlock, StringComparison.Ordinal);
        Assert.Contains("/executions/' + $exec.name + '/stop?api-version=2025-01-01", publishBlock, StringComparison.Ordinal);
        Assert.Contains("DISABLING non-Manual trigger on ContainerApps Job", publishBlock, StringComparison.Ordinal);
        Assert.Contains("FAILED to disable non-Manual trigger on ContainerApps Job", publishBlock, StringComparison.Ordinal);
        Assert.Contains("Microsoft.DBforPostgreSQL/flexibleServers", publishBlock, StringComparison.Ordinal);
        Assert.Contains("/stop?api-version=2024-08-01", publishBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("/resourceGroups/NetworkWatcherRG", rbacBlock, StringComparison.OrdinalIgnoreCase);

        // Verify zero unescaped Pulumi YAML interpolations inside command create scripts.
        var publishCreateIdx = publishBlock.IndexOf("      create: |\n", StringComparison.Ordinal);
        Assert.True(publishCreateIdx > 0);
        Assert.DoesNotMatch(PulumiInterpolationRegex(), publishBlock[(publishCreateIdx + "      create: |\n".Length)..]);

        var webhookUriBlock = ResourceBlock(yaml, "automation-webhook-intochat-test-cost-aa-uri");
        Assert.Contains("type: command:local:Command", webhookUriBlock, StringComparison.Ordinal);
        Assert.Contains("      additionalSecretOutputs:\n        - stdout\n", webhookUriBlock, StringComparison.Ordinal);
        Assert.Contains("      logging: stderr\n", webhookUriBlock, StringComparison.Ordinal);

        var webhookBlock = ResourceBlock(yaml, "automation-webhook-intochat-test-cost-aa");
        Assert.Contains("type: azure-native:automation:Webhook", webhookBlock, StringComparison.Ordinal);
        Assert.Contains("isEnabled: true", webhookBlock, StringComparison.Ordinal);
        Assert.Contains("name: \"${automation-runbook-stop-intochatcostbreaker.name}\"", webhookBlock, StringComparison.Ordinal);

        // 4. Azure Monitor Action Group (verified token: azure-native:monitor:ActionGroup, groupShortName <= 12 chars).
        var agBlock = ResourceBlock(yaml, "action-group-intochat-test-cost-ag");
        Assert.Contains("type: azure-native:monitor:ActionGroup", agBlock, StringComparison.Ordinal);
        Assert.Contains("location: Global", agBlock, StringComparison.Ordinal);
        Assert.Contains("emailAddress: \"vlad@intochat.io\"", agBlock, StringComparison.Ordinal);
        Assert.Contains("automationRunbookReceivers:", agBlock, StringComparison.Ordinal);
        Assert.Contains("automationAccountId: \"${automation-account-intochat-test-cost-aa.id}\"", agBlock, StringComparison.Ordinal);
        Assert.Contains("webhookResourceId: \"${automation-webhook-intochat-test-cost-aa.id}\"", agBlock, StringComparison.Ordinal);

        var shortNameMatch = GroupShortNameRegex().Match(agBlock);
        Assert.True(shortNameMatch.Success, "ActionGroup missing groupShortName.");
        Assert.True(shortNameMatch.Groups[1].Value.Length <= 12, $"ActionGroup groupShortName '{shortNameMatch.Groups[1].Value}' exceeds 12-char ARM limit.");

        // 5. Resource-group-scoped Consumption Budget (185 EUR, 50/80/90 Forecasted/95/100 thresholds, ActionGroup on 95% & 100%).
        var budgetBlock = ResourceBlock(yaml, "consumption-budget-intochat-test-hard-cap-185eur");
        Assert.Contains("type: azure-native:consumption:Budget", budgetBlock, StringComparison.Ordinal);
        Assert.Contains($"dependsOn: [\"${{resource-group-{config.GroupName}}}\", \"${{action-group-intochat-test-cost-ag}}\"]", budgetBlock, StringComparison.Ordinal);
        Assert.Contains($"scope: \"{intoChatRgScope}\"", budgetBlock, StringComparison.Ordinal);
        Assert.DoesNotContain($"scope: \"{subScope}\"", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("amount: 185\n", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("timeGrain: Monthly", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("category: Cost", budgetBlock, StringComparison.Ordinal);

        var utcNow = DateTimeOffset.UtcNow;
        var expectedStartDate = $"{utcNow.Year:D4}-{utcNow.Month:D2}-01T00:00:00Z";
        var expectedEndDate = $"{utcNow.Year + 5:D4}-{utcNow.Month:D2}-01T00:00:00Z";
        Assert.Contains($"startDate: \"{expectedStartDate}\"", budgetBlock, StringComparison.Ordinal);
        Assert.Contains($"endDate: \"{expectedEndDate}\"", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("      ignoreChanges:\n        - timePeriod.startDate\n        - timePeriod.endDate\n", budgetBlock, StringComparison.Ordinal);

        Assert.Contains("actual-50pct:", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("threshold: 50\n", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("actual-80pct:", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("threshold: 80\n", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("forecasted-90pct:", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("threshold: 90\n", budgetBlock, StringComparison.Ordinal);
        Assert.Contains("thresholdType: Forecasted", budgetBlock, StringComparison.Ordinal);

        var kill95Idx = budgetBlock.IndexOf("actual-95pct-kill:", StringComparison.Ordinal);
        var kill100Idx = budgetBlock.IndexOf("actual-100pct-kill:", StringComparison.Ordinal);
        Assert.True(kill95Idx > 0 && kill100Idx > kill95Idx);
        var kill95Section = budgetBlock[kill95Idx..kill100Idx];
        var kill100Section = budgetBlock[kill100Idx..];

        Assert.Contains("threshold: 95\n", kill95Section, StringComparison.Ordinal);
        Assert.Contains("contactGroups:\n            - \"${action-group-intochat-test-cost-ag.id}\"", kill95Section, StringComparison.Ordinal);
        Assert.Contains("threshold: 100\n", kill100Section, StringComparison.Ordinal);
        Assert.Contains("contactGroups:\n            - \"${action-group-intochat-test-cost-ag.id}\"", kill100Section, StringComparison.Ordinal);

        // 6. Every Pulumi YAML ${ref} expression in the entire synthesized stack must reference a declared resource or variable.
        var declaredSymbols = ResourceHeaderRegex().Matches(yaml)
            .Select(m => m.Groups[1].Value)
            .Concat(["clientConfig", "logSharedKeys", "storageKeys"])
            .ToHashSet(StringComparer.Ordinal);

        foreach (Match match in PulumiInterpolationRegex().Matches(yaml))
        {
            var rootSymbol = match.Groups[1].Value.Split('.')[0];
            Assert.True(
                declaredSymbols.Contains(rootSymbol),
                $"Synthesized Pulumi.yaml contains unresolved interpolation '${{{match.Groups[1].Value}}}' (root symbol '{rootSymbol}' not declared).");
        }
    }

    [Fact]
    public void DisablingCostGuardrailsOmitsLayer1PoliciesAndLayer2KillSwitch()
    {
        var config = InstallConfig.Universal(env: "test", subscription: SubscriptionId) with
        {
            CostGuardrails = new CostGuardrailConfig { Enabled = false },
        };

        var yaml = PulumiStackSynthesizer.Synthesize(config, OperatingSystemModules.Modules).PulumiYaml;

        Assert.DoesNotContain("azure-native:authorization:PolicyDefinition", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-native:authorization:PolicyAssignment", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-native:automation:AutomationAccount", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-native:monitor:ActionGroup", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("azure-native:consumption:Budget", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TestAndProdStacksOnSameSubscriptionHaveZeroOverlappingPolicyOrBudgetNames()
    {
        var testConfig = InstallConfig.Universal(env: "test", subscription: SubscriptionId);
        var prodConfig = InstallConfig.Universal(env: "prod", subscription: SubscriptionId);

        var testYaml = PulumiStackSynthesizer.Synthesize(testConfig, OperatingSystemModules.Modules).PulumiYaml;
        var prodYaml = PulumiStackSynthesizer.Synthesize(prodConfig, OperatingSystemModules.Modules).PulumiYaml;

        static HashSet<string> Extract(Regex regex, string yaml)
            => regex.Matches(yaml).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var testPolicyDefs = Extract(PolicyDefinitionNameRegex(), testYaml);
        var prodPolicyDefs = Extract(PolicyDefinitionNameRegex(), prodYaml);
        Assert.Equal(3, testPolicyDefs.Count);
        Assert.Equal(3, prodPolicyDefs.Count);
        Assert.Empty(testPolicyDefs.Intersect(prodPolicyDefs, StringComparer.OrdinalIgnoreCase));
        Assert.All(testPolicyDefs.Concat(prodPolicyDefs), name => Assert.True(name.Length <= 64, $"PolicyDefinition name '{name}' exceeds 64 chars."));

        var testPolicyAssigns = Extract(PolicyAssignmentNameRegex(), testYaml);
        var prodPolicyAssigns = Extract(PolicyAssignmentNameRegex(), prodYaml);
        Assert.Equal(3, testPolicyAssigns.Count);
        Assert.Equal(3, prodPolicyAssigns.Count);
        Assert.Empty(testPolicyAssigns.Intersect(prodPolicyAssigns, StringComparer.OrdinalIgnoreCase));
        Assert.All(testPolicyAssigns.Concat(prodPolicyAssigns), name => Assert.True(name.Length <= 64, $"PolicyAssignment name '{name}' exceeds 64 chars."));

        var testBudgets = Extract(BudgetNameRegex(), testYaml);
        var prodBudgets = Extract(BudgetNameRegex(), prodYaml);
        Assert.Single(testBudgets);
        Assert.Single(prodBudgets);
        Assert.Empty(testBudgets.Intersect(prodBudgets, StringComparer.OrdinalIgnoreCase));
        Assert.All(testBudgets.Concat(prodBudgets), name => Assert.True(name.Length <= 63, $"Budget name '{name}' exceeds 63 chars."));
    }

    private static string ResourceBlock(string yaml, string logicalName)
    {
        var start = yaml.IndexOf("\n  " + logicalName + ":\n", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Resource '{logicalName}' missing from synthesized Pulumi YAML.");
        var next = Regex.Match(yaml[(start + 1)..], @"\n  \S|\n\S");
        return next.Success ? yaml[start..(start + 1 + next.Index)] : yaml[start..];
    }
}
