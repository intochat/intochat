using DigitalBrain.Capabilities;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Connectors;
using DigitalBrain.Capabilities.Connections.Sealing;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Connections.Platform.Connections;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Framework.Modules.Platform.Identity;

namespace IntoChat.OS.Authority;

[Collection("EdgeGrantsCache")]
public sealed class GrantRulesChallengerFacts
{
    private static CapabilityCaller CreateAppCaller(string appKey)
        => CapabilityCallers.Of(AppPrincipal.Id(appKey), "test-user", "test-brain", CallerKind.App, appKey);

    [Fact]
    public void EmptySemanticTypes_DeniedForForeignNeuronsWithoutGrant()
    {
        var caller = CreateAppCaller("local/apps/my-app");

        string[] foreignTargets =
        [
            "local/connection/postgres/main",
            "local/apps/other-app",
            "system/settings",
            "local/connection/github/personal",
        ];

        foreach (var target in foreignTargets)
        {
            var request = new CallRequest
            {
                Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                TargetNeuron = target,
                Operation = "Query",
                SemanticTypeIds = [],
            };

            var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
            Assert.NotNull(decision);
            Assert.False(decision!.Allowed, $"Target '{target}' should have been denied for caller '{caller.AppId}'");
            Assert.Equal(CallDenial.MissingGrant, decision.Denial);
        }
    }

    [Theory]
    [InlineData("local/apps/calculator", "local/apps/calculator")]
    [InlineData("local/apps/calculator", "neuron://local/apps/calculator")]
    [InlineData("local/apps/calculator", "local/apps/calculator/worker")]
    [InlineData("local/apps/calc", "local/apps/calc")]
    [InlineData("local/apps/calc", "local/apps/calc/subneuron")]
    [InlineData("local/apps/calc", "local/apps/calc/subneuron/deep")]
    [InlineData("local/apps/calc", "neuron://local/apps/calc")]
    [InlineData("local/apps/calc", "neuron://local/apps/calc/subneuron")]
    public void EmptySemanticTypes_AllowedForOwnNeuronKeyAndExactSubpaths(string appKey, string target)
    {
        var caller = CreateAppCaller(appKey);

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = target,
            Operation = "Compute",
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(decision);
        Assert.True(decision!.Allowed, $"Target '{target}' should have been allowed for own app caller '{caller.AppId}'");
    }

    [Fact]
    public void EmptySemanticTypes_AllowedWhenBoundToFriendConnection()
    {
        var appKey = "local/apps/friend-app-challenger";
        var caller = CreateAppCaller(appKey);
        var connectionKey = "local/connection/github/team-challenger";

        EdgeGrantsCache.Current.BindFriendConnection(connectionKey, appKey);

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = connectionKey,
            Operation = "Read",
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(decision);
        Assert.True(decision!.Allowed);
    }

    [Theory]
    [InlineData("system/identity.vault/ada")]
    [InlineData("SYSTEM/IDENTITY.VAULT/ADA")]
    [InlineData("local/vault/key")]
    [InlineData("VAULT/KEY")]
    [InlineData("secrets")]
    [InlineData("SECRETS")]
    [InlineData("local/connection/secrets/work")]
    [InlineData("oauth-exchange")]
    [InlineData("OAUTH-EXCHANGE")]
    [InlineData("IVault")]
    [InlineData("ivault")]
    [InlineData("ISecretValueSource")]
    [InlineData("VaultSecretValueSource")]
    [InlineData("IBrainSecretSealer")]
    [InlineData("BrainSecretSealer")]
    [InlineData("ISecretSealer")]
    [InlineData("SecretSealer")]
    [InlineData("OAuthTokenExchange")]
    [InlineData("IOAuthTokenExchange")]
    public void ZeroTokenGuard_ForbiddenTargetsEnforced_CaseInsensitive(string target)
    {
        var caller = CreateAppCaller("local/apps/untrusted-app");

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = target,
            Operation = "AnyOperation",
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, decision.Denial);
    }

    [Theory]
    [InlineData("ExportFor")]
    [InlineData("exportfor")]
    [InlineData("EXPORTFOR")]
    [InlineData("IConnection.ExportFor")]
    [InlineData("iconnection.exportfor")]
    [InlineData("AcceptGrant")]
    [InlineData("acceptgrant")]
    [InlineData("OpenSecret")]
    [InlineData("opensecret")]
    [InlineData("CurrentValues")]
    [InlineData("currentvalues")]
    [InlineData("IVault.Resolve")]
    [InlineData("ivault.resolve")]
    [InlineData("ISecretValueSource.CurrentValues")]
    [InlineData("OAuthTokenExchange.Exchange")]
    [InlineData("IOAuthTokenExchange.Exchange")]
    public void ZeroTokenGuard_ForbiddenOperationsEnforced_CaseInsensitive(string operation)
    {
        var caller = CreateAppCaller("local/apps/untrusted-app");

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = "local/apps/untrusted-app",
            Operation = operation,
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, decision.Denial);
    }

    [Fact]
    public void ZeroTokenGuard_PermitsBenignTargetsWithSubstringOfForbiddenWords()
    {
        var caller = CreateAppCaller("local/apps/vault-helper");

        var benignTargets = new[]
        {
            "local/apps/vault-helper",
            "local/connection/my-secrets-service",
            "local/apps/vaultkeeper",
        };

        foreach (var target in benignTargets)
        {
            var request = new CallRequest
            {
                Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                TargetNeuron = target,
                Operation = "DoSomethingBenign",
                SemanticTypeIds = [],
            };

            var guardDecision = GrantRules.EvaluateZeroTokenGuard(request);
            Assert.Null(guardDecision);
        }
    }

    [Fact]
    public void AppIdentitySpoofing_DeniedWithUntrustedCaller()
    {
        // Attacker claims AppId "local/apps/victim", but its principal is "app:local/apps/attacker"
        var spoofedCaller = CapabilityCallers.Of("app:local/apps/attacker", "test-user", "test-brain", CallerKind.App, "local/apps/victim");

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(spoofedCaller),
            TargetNeuron = "local/apps/victim",
            Operation = "Run",
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(spoofedCaller));
        Assert.NotNull(decision);
        Assert.False(decision!.Allowed);
        Assert.Equal(CallDenial.UntrustedCaller, decision.Denial);
        Assert.Contains("must identify its app", decision.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonEmptySemanticTypes_RequiresMatchingLiveGrant()
    {
        var caller = CreateAppCaller("local/apps/weather");
        var target = "test-brain/connection/weather/sensors";

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = target,
            Operation = "ReadTemperature",
            SemanticTypeIds = ["weather.temperature"],
        };

        // 1. Without grants -> Denied
        var noGrantDecision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(noGrantDecision);
        Assert.False(noGrantDecision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, noGrantDecision.Denial);

        // 2. With unrelated grant -> Denied
        var unrelatedGrant = new Grant
        {
            WorkspaceId = caller.BrainId,
            AppId = caller.AppId!,
            SemanticTypeId = "weather.humidity",
            TargetNeuronKey = target,
            Mode = GrantMode.Always,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        var unrelatedDecision = GrantRules.Evaluate(request, null, [unrelatedGrant], PrincipalSet.For(caller));
        Assert.NotNull(unrelatedDecision);
        Assert.False(unrelatedDecision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, unrelatedDecision.Denial);

        // 3. With revoked grant -> Denied
        var revokedGrant = new Grant
        {
            WorkspaceId = caller.BrainId,
            AppId = caller.AppId!,
            SemanticTypeId = "weather.temperature",
            TargetNeuronKey = target,
            Mode = GrantMode.Always,
            Revoked = true,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        var revokedDecision = GrantRules.Evaluate(request, null, [revokedGrant], PrincipalSet.For(caller));
        Assert.NotNull(revokedDecision);
        Assert.False(revokedDecision!.Allowed);
        Assert.Equal(CallDenial.MissingGrant, revokedDecision.Denial);

        // 4. With active matching grant -> Allowed
        var matchingGrant = new Grant
        {
            WorkspaceId = caller.BrainId,
            AppId = caller.AppId!,
            SemanticTypeId = "weather.temperature",
            TargetNeuronKey = target,
            Mode = GrantMode.Always,
            Revoked = false,
            GrantedAt = DateTimeOffset.UtcNow,
        };
        var allowedDecision = GrantRules.Evaluate(request, null, [matchingGrant], PrincipalSet.For(caller));
        Assert.NotNull(allowedDecision);
        Assert.True(allowedDecision!.Allowed);
    }

    [Theory]
    [InlineData("local/apps/calc", "local/apps/calculator")]
    [InlineData("local/apps/calc", "local/apps/calculator/subneuron")]
    [InlineData("local/apps/calc", "neuron://local/apps/calculator")]
    [InlineData("local/apps/calc", "neuron://local/apps/calculator/subneuron")]
    [InlineData("local/apps/calc", "local/apps/calc-2")]
    [InlineData("local/apps/calc", "local/apps/calc_2")]
    [InlineData("local/apps/calc", "local/apps/calc.2")]
    [InlineData("local/apps/a", "local/apps/admin")]
    [InlineData("local/apps/a", "local/apps/admin/super")]
    [InlineData("local/apps/fin", "local/apps/finance")]
    [InlineData("local/apps/fin", "local/apps/finance/ledger")]
    [InlineData("local/apps/note", "local/apps/notes")]
    [InlineData("local/apps/note", "local/apps/notes/items")]
    public void EmptySemanticTypes_AdversarialPrefixSubstringCollision_BehaviorCheck(string attackerAppKey, string victimTargetNeuron)
    {
        var caller = CreateAppCaller(attackerAppKey);

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = victimTargetNeuron,
            Operation = "ExecutePrivilegedAction",
            SemanticTypeIds = [],
        };

        var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(decision);
        // Fail-closed security requires that attacker app CANNOT access foreign victim app
        Assert.False(decision!.Allowed, $"Empirical Bug: foreign target '{victimTargetNeuron}' was allowed for app '{attackerAppKey}' because of substring matching target.Contains(app) in GrantRules.cs:137!");
        Assert.Equal(CallDenial.MissingGrant, decision.Denial);
    }
}
