using System;
using System.Reflection;
using DigitalBrain.Capabilities;
using IntoChat.OS.Authority.Identity.Grants;
using IntoChat.OS.Connections.Identity;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Modules.Platform.Identity;
using IntoChat.OS.Modules.Circles;
using Xunit;

namespace IntoChat.OS.Modules.Circles.Tests.Unit;

[Collection("EdgeGrantsCache")]
public sealed class CircleZeroLeakFacts
{
    private const string SharerBrain = "sharer-alice";
    private const string CircleBrain = "circle-team";

    [Fact]
    public void CrossBrainInvocation_ThroughCircleReferenceGrant_SucceedsWithoutAssemblyDuplication()
    {
        const string appName = "calculator";
        var connectionKey = $"{SharerBrain}/apps/{appName}";
        var circleAppId = $"{CircleBrain}/apps/{appName}";

        // 1. Structural Zero-Duplication Check:
        // CircleBinding contains only reference strings, ZERO binary byte[] assembly payloads
        var binding = new CircleBinding("user-alice", appName, string.Empty, connectionKey);
        Assert.Equal("user-alice", binding.AccountId);
        Assert.Equal(appName, binding.AppName);
        Assert.Equal(string.Empty, binding.Slot);
        Assert.Equal(connectionKey, binding.ConnectionKey);

        var bindingProps = typeof(CircleBinding).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(bindingProps, p => p.PropertyType == typeof(byte[]));

        var recordProps = typeof(CircleRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(recordProps, p => p.PropertyType == typeof(byte[]));

        // 2. Establish reference grant via capability broker
        try
        {
            EdgeGrantsCache.Current.BindFriendConnection(connectionKey, circleAppId);

            var caller = CapabilityCallers.Of(AppPrincipal.Id(circleAppId), "user-bob", CircleBrain, CallerKind.App, circleAppId);
            var request = new CallRequest
            {
                Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                TargetNeuron = connectionKey,
                Operation = "Add",
                SemanticTypeIds = [],
            };

            // Cross-brain call evaluation: allowed through reference grant without possessing local assembly
            var decision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
            Assert.NotNull(decision);
            Assert.True(decision.Allowed, $"Cross-brain call to '{connectionKey}' should be allowed via reference grant.");
        }
        finally
        {
            EdgeGrantsCache.Current.UnbindFriendConnection(connectionKey, circleAppId);
        }
    }

    [Fact]
    public void CrossBrainInvocation_ZeroTokenLeakage_MediatedThroughCapabilityBroker()
    {
        var connectionKey = $"{SharerBrain}/connection/google/drive-main";
        var circleAppId = $"{CircleBrain}/apps/drive-viewer";

        try
        {
            EdgeGrantsCache.Current.BindFriendConnection(connectionKey, circleAppId);
            var caller = CapabilityCallers.Of(AppPrincipal.Id(circleAppId), "user-bob", CircleBrain, CallerKind.App, circleAppId);

            // 1. Direct access to vault/secrets/credentials must be blocked by ZeroTokenGuard
            string[] forbiddenTargets =
            [
                $"{SharerBrain}/vault",
                $"{SharerBrain}/identity.vault/ada",
                "system/vault",
                "secrets",
                "oauth-exchange"
            ];

            foreach (var forbiddenTarget in forbiddenTargets)
            {
                var vaultRequest = new CallRequest
                {
                    Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                    TargetNeuron = forbiddenTarget,
                    Operation = "Read",
                    SemanticTypeIds = [],
                };

                var guardDecision = GrantRules.EvaluateZeroTokenGuard(vaultRequest);
                Assert.NotNull(guardDecision);
                Assert.False(guardDecision.Allowed);
                Assert.Equal(CallDenial.UntrustedCaller, guardDecision.Denial);
                Assert.Contains("Zero-token auth enforcement", guardDecision.Explanation);
            }

            // 2. Direct access to raw credential operations must also be denied
            string[] forbiddenOperations =
            [
                "OpenSecret",
                "CurrentValues",
                "OAuthTokenExchange.Exchange",
                "IVault.Resolve"
            ];

            foreach (var forbiddenOp in forbiddenOperations)
            {
                var secretOpRequest = new CallRequest
                {
                    Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                    TargetNeuron = connectionKey,
                    Operation = forbiddenOp,
                    SemanticTypeIds = [],
                };

                var opGuardDecision = GrantRules.EvaluateZeroTokenGuard(secretOpRequest);
                Assert.NotNull(opGuardDecision);
                Assert.False(opGuardDecision.Allowed);
                Assert.Equal(CallDenial.UntrustedCaller, opGuardDecision.Denial);
            }

            // 3. Mediated business capability call (e.g. ListFiles) through capability broker is permitted
            var businessRequest = new CallRequest
            {
                Caller = IntoChat.OS.Tests.TestEdge.From(caller),
                TargetNeuron = connectionKey,
                Operation = "ListFiles",
                SemanticTypeIds = [],
            };

            var businessDecision = GrantRules.Evaluate(businessRequest, null, [], PrincipalSet.For(caller));
            Assert.NotNull(businessDecision);
            Assert.True(businessDecision.Allowed);
        }
        finally
        {
            EdgeGrantsCache.Current.UnbindFriendConnection(connectionKey, circleAppId);
        }
    }

    [Fact]
    public void CircleGrantRevocation_InstantlySeversCrossBrainInvocation()
    {
        var targetNeuron = $"{SharerBrain}/apps/notes";
        var circleAppId = $"{CircleBrain}/apps/notes";
        var caller = CapabilityCallers.Of(AppPrincipal.Id(circleAppId), "user-bob", CircleBrain, CallerKind.App, circleAppId);

        var request = new CallRequest
        {
            Caller = IntoChat.OS.Tests.TestEdge.From(caller),
            TargetNeuron = targetNeuron,
            Operation = "ReadNote",
            SemanticTypeIds = [],
        };

        // Phase 1: Without grant, invocation is denied
        var initialDecision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(initialDecision);
        Assert.False(initialDecision.Allowed);
        Assert.Equal(CallDenial.MissingGrant, initialDecision.Denial);

        // Phase 2: Grant is issued when shared into circle -> invocation succeeds
        EdgeGrantsCache.Current.BindFriendConnection(targetNeuron, circleAppId);

        var activeDecision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(activeDecision);
        Assert.True(activeDecision.Allowed);

        // Phase 3: Circle grant is revoked (e.g., app unshared or circle deleted) -> invocation instantly severed
        EdgeGrantsCache.Current.UnbindFriendConnection(targetNeuron, circleAppId);

        var revokedDecision = GrantRules.Evaluate(request, null, [], PrincipalSet.For(caller));
        Assert.NotNull(revokedDecision);
        Assert.False(revokedDecision.Allowed);
        Assert.Equal(CallDenial.MissingGrant, revokedDecision.Denial);
    }
}
