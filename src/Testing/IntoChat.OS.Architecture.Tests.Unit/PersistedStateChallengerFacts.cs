using System;
using System.Collections.Generic;
using System.Linq;
using IntoChat.OS.Modules.Apps.Contracts.Testing;
using IntoChat.OS.Modules.Apps.Testing;
using Orleans;
using Xunit;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed class PersistedStateChallengerFacts
{
    [Fact]
    public void TestVerificationState_IsDiscoveredAsPersistedState()
    {
        var states = PersistedStateRules.Discover(ProductionArchitecture.Types);
        Assert.Contains(states, t => t == typeof(TestVerificationState));
    }

    [Fact]
    public void TestVerificationState_IsNotSuppressedInUnmigratedConcreteCollections()
    {
        Assert.DoesNotContain("IntoChat.OS.Modules.Apps.Testing.TestVerificationState.Runs",
            PersistedStateRules.UnmigratedConcreteCollections);
        Assert.All(PersistedStateRules.UnmigratedConcreteCollections,
            entry => Assert.DoesNotContain("TestVerification", entry));
    }

    [Fact]
    public void TestVerificationState_HasZeroShapeViolations()
    {
        var violations = PersistedStateRules.ShapeViolations(typeof(TestVerificationState)).ToArray();
        Assert.Empty(violations);
    }

    [Fact]
    public void PersistedStateRules_CatchesIllegalListInVerificationState_IfReintroduced()
    {
        var violations = PersistedStateRules.ShapeViolations(typeof(AdversarialListVerificationState)).ToArray();
        Assert.Single(violations);
        Assert.Contains("persisted collections must be arrays", violations[0]);
    }

    [Fact]
    public void PersistedStateFacts_PersistedStateHasUniqueIdsAndConcreteCollections_ExecutesWithoutError()
    {
        var facts = new PersistedStateFacts();
        facts.PersistedStateHasUniqueIdsAndConcreteCollections();
    }

    [GenerateSerializer]
    internal sealed class AdversarialListVerificationState
    {
        [Id(0)] public string SuiteId { get; set; } = string.Empty;
        [Id(1)] public List<TestResultSummary> Runs { get; set; } = [];
    }
}
