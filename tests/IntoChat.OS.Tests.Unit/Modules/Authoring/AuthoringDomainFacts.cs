using IntoChat.OS.Modules.Authoring;

namespace IntoChat.OS.Tests.Unit.Modules.Authoring;

public sealed class AuthoringDomainFacts
{
    [Fact]
    public void AuthoringRequestValidatesGoalNonEmpty()
    {
        var valid = new AuthoringRequest("build x watch", "x.watch");
        valid.Validate();

        var emptyGoal = new AuthoringRequest("");
        Assert.Throws<ArgumentException>(emptyGoal.Validate);

        var whitespaceGoal = new AuthoringRequest("   ");
        Assert.Throws<ArgumentException>(whitespaceGoal.Validate);
    }

    [Fact]
    public void AuthoringRequestValidatesAliasFormatWhenProvided()
    {
        var invalidAlias = new AuthoringRequest("goal", "invalid_alias_no_dot");
        Assert.Throws<InvalidDataException>(invalidAlias.Validate);

        var valid = new AuthoringRequest("goal", "unit.watch");
        valid.Validate();
    }

    [Fact]
    public void AuthoringSpecValidatesInvariants()
    {
        var valid = new AuthoringSpec(
            Alias: "demo.unit",
            Title: "Demo Unit",
            Spec: "# Demo Unit\nWhen tick then increment.",
            State: new Dictionary<string, string> { ["Count"] = "int" },
            Signals: [new AuthoringSignal("demo.incremented", new Dictionary<string, string> { ["Value"] = "int" })],
            Needs: new AuthoringNeeds([new AuthoringSlotNeed("weather", "http", ["api.weather.com"])], "every 10m", true),
            Scenarios: [new AuthoringScenario("counts up", null, ["tick 10m"], ["state demo.unit Count = 1"])]);

        valid.Validate();

        // Invalid alias
        var badAlias = valid with { Alias = "BadAlias" };
        Assert.Throws<InvalidDataException>(badAlias.Validate);

        // Empty title
        var noTitle = valid with { Title = "" };
        Assert.Throws<InvalidDataException>(noTitle.Validate);

        // Empty spec
        var noSpec = valid with { Spec = "" };
        Assert.Throws<InvalidDataException>(noSpec.Validate);

        // No scenarios
        var noScenarios = valid with { Scenarios = [] };
        Assert.Throws<InvalidDataException>(noScenarios.Validate);

        // Bad slot capability
        var badSlot = valid with { Needs = new AuthoringNeeds([new AuthoringSlotNeed("s1", "invalid_cap", [])], null, false) };
        Assert.Throws<InvalidDataException>(badSlot.Validate);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("http@1")]
    [InlineData("tool")]
    [InlineData("tool@1")]
    [InlineData("storage")]
    [InlineData("storage@1")]
    public void AuthoringSpecAllowsStorageToolAndHttpCapabilities(string capability)
    {
        var spec = new AuthoringSpec(
            Alias: "demo.unit",
            Title: "Demo Unit",
            Spec: "# Demo Unit\nWhen tick then increment.",
            State: new Dictionary<string, string> { ["Count"] = "int" },
            Signals: [],
            Needs: new AuthoringNeeds([new AuthoringSlotNeed("slot1", capability, [])], null, true),
            Scenarios: [new AuthoringScenario("counts up", null, ["tick 10m"], ["state demo.unit Count = 1"])]);

        spec.Validate();
    }

    [Fact]
    public async Task AuthorNeuronPrepareSpecPreservesPriorScenariosFromExistingFiles()
    {
        var earlierScenarios = """
            [
              {"name": "original scenario 1", "when": ["tick 5m"], "then": ["state test.app Count = 1"]},
              {"name": "original scenario 2", "when": ["publish test.ping"], "then": ["state test.app Count = 2"]}
            ]
            """;

        var request = new AuthoringRequest(
            Goal: "refine unit to add reset button",
            Alias: "test.app",
            ExistingFiles: new Dictionary<string, string>
            {
                ["scenarios.json"] = earlierScenarios
            });

        var neuron = new AuthorNeuron();
        var spec = await neuron.PrepareSpec(request, TestContext.Current.CancellationToken);

        Assert.Equal("test.app", spec.Alias);
        Assert.Equal(2, spec.Scenarios.Count);
        Assert.Equal("original scenario 1", spec.Scenarios[0].Name);
        Assert.Equal("original scenario 2", spec.Scenarios[1].Name);
    }

    [Fact]
    public async Task AuthorNeuronVerifyBuildFlagsDroppedPriorScenarios()
    {
        var spec = new AuthoringSpec(
            Alias: "test.app",
            Title: "Test App",
            Spec: "# Test\nReactions.",
            State: new Dictionary<string, string> { ["Count"] = "int" },
            Signals: [],
            Needs: new AuthoringNeeds([], null, true),
            Scenarios:
            [
                new AuthoringScenario("prior pass 1", null, ["tick 5m"], ["state test.app Count = 1"]),
                new AuthoringScenario("prior pass 2", null, ["publish test.seen"], ["state test.app Count = 0"])
            ]);

        var filesMissingScenario = new Dictionary<string, string>
        {
            ["App.cs"] = """
                namespace Units;
                [State("test.app")]
                public record TestState(int Count = 0);
                public static class AppUnit
                {
                    [Synapse("test.seen", "test.app")]
                    public static TestState Seen(TestState s) => s with { Count = 0 };
                }
                """,
            ["scenarios.json"] = """
                [
                  {"name": "prior pass 1", "when": ["tick 5m"], "then": ["state test.app Count = 1"]}
                ]
                """
        };

        var neuron = new AuthorNeuron();
        var result = await neuron.VerifyBuild(spec, filesMissingScenario, TestContext.Current.CancellationToken);

        Assert.False(result.Passed);
        Assert.Contains(result.Failures, f => f.Contains("prior pass 2"));
    }

    [Fact]
    public async Task AuthorNeuronVerifyBuildSucceedsWhenAllScenariosPreservedOrSuperseded()
    {
        var spec = new AuthoringSpec(
            Alias: "test.app",
            Title: "Test App",
            Spec: "# Test\nReactions.",
            State: new Dictionary<string, string> { ["Count"] = "int" },
            Signals: [],
            Needs: new AuthoringNeeds([], null, true),
            Scenarios:
            [
                new AuthoringScenario("prior pass 1", null, ["tick 5m"], ["state test.app Count = 1"]),
                new AuthoringScenario("prior pass 2", null, ["publish test.seen"], ["state test.app Count = 0"])
            ]);

        var validFiles = new Dictionary<string, string>
        {
            ["App.cs"] = """
                namespace Units;
                [State("test.app")]
                public record TestState(int Count = 0);
                public static class AppUnit
                {
                    [Synapse("test.seen", "test.app")]
                    public static TestState Seen(TestState s) => s with { Count = 0 };
                }
                """,
            ["scenarios.json"] = """
                [
                  {"name": "prior pass 1", "when": ["tick 5m"], "then": ["state test.app Count = 1"]},
                  {"name": "refined pass 2", "supersedes": "prior pass 2", "when": ["publish test.reset"], "then": ["state test.app Count = 0"]}
                ]
                """
        };

        var neuron = new AuthorNeuron();
        var result = await neuron.VerifyBuild(spec, validFiles, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
    }

    [Fact]
    public void AuthoringVerificationFormatsCleanReport()
    {
        var success = AuthoringVerification.Success();
        Assert.True(success.Passed);
        Assert.Equal("Verification passed successfully.", success.FormatReport());

        var failed = AuthoringVerification.Failure(
            [new AuthoringDiagnostic("Unit.cs", "CS1002", 42, "; expected")],
            ["scenario 'test' failed: count was 0 expected 1"]);

        Assert.False(failed.Passed);
        var report = failed.FormatReport();
        Assert.Contains("Verification failed:", report, StringComparison.Ordinal);
        Assert.Contains("[CS1002] Unit.cs(42): ; expected", report, StringComparison.Ordinal);
        Assert.Contains("[Failure] scenario 'test' failed", report, StringComparison.Ordinal);
    }

    [Fact]
    public void UnitBuildOutcomePropertiesHoldExpectedValues()
    {
        var outcome = new UnitBuildOutcome(
            Success: true,
            Alias: "x.watch",
            Hash: "abc123hash",
            Files: new Dictionary<string, string> { ["XWatch.cs"] = "class XWatch {}" },
            Status: "Installed",
            Detail: "Installed successfully");

        Assert.True(outcome.Success);
        Assert.Equal("x.watch", outcome.Alias);
        Assert.Equal("abc123hash", outcome.Hash);
        Assert.Single(outcome.Files);
        Assert.Equal("Installed", outcome.Status);
        Assert.Equal("Installed successfully", outcome.Detail);
    }
}
