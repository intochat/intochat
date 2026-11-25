using DigitalBrain;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.Roslyn;

namespace IntoChat.OS.Tests.Unit.Modules.Catalog;

public sealed class CodeOrchestrationRepairAndResultFacts
{
    private const string ValidScript = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using IntoChat.Sdk;

        public static class OrchestrationScript
        {
            public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
            {
                await os.Notifications.NotifyAsync("Done", "Processed input", NotificationPriority.Normal, ct);
                return new OrchestrationResult(
                    Status: "succeeded",
                    Summary: "Completed orchestration",
                    Artifacts: [new OrchestrationArtifact("summary.txt", "text", "ok")],
                    Metrics: new Dictionary<string, double> { ["count"] = 2 });
            }
        }
        """;

    private sealed class ScriptedOrchestrationModel(params string[] responses) : ICodeOrchestrationModel
    {
        private int _index;
        public List<CodeOrchestrationPrompt> Prompts { get; } = [];

        public Task<string> GenerateScriptAsync(CodeOrchestrationPrompt prompt, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            var reply = _index < responses.Length ? responses[_index] : responses[^1];
            _index++;
            return Task.FromResult(reply);
        }
    }

    private static CodeOrchestrationCompiler CreateCompiler()
        => new(new AppPackageCompiler(), new CodeOrchestrationAstSanitizer());

    [Fact]
    public async Task CompilesCleanScriptOnInitialAttemptWithZeroRepairRounds()
    {
        var compiler = CreateCompiler();
        var model = new ScriptedOrchestrationModel("```csharp\n" + ValidScript + "\n```");
        var contracts = NeuronContractDiscovery.Discover(filterAliases: ["notifications"]);

        var outcome = await compiler.CompileWithRepairAsync(
            brainId: new BrainId("brain-unit"),
            appKey: CodeOrchestrationScriptWrapper.AppKey("run-0"),
            title: "Clean Run",
            goal: "Notify user",
            inputJson: "{}",
            availableContracts: contracts,
            allowedContracts: ["notifications"],
            preauthoredScript: null,
            model: model,
            ct: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Diagnostics));
        Assert.Equal(0, outcome.RepairRoundsUsed);
        Assert.Single(model.Prompts);
        Assert.NotNull(outcome.CompiledPackage);
        Assert.NotEmpty(outcome.CompiledPackage!.AssemblyBytes);
        Assert.Contains("notifications", outcome.RequiredContracts, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepairsAstViolationAndCompilerErrorWithinTwoRepairRounds()
    {
        const string forbiddenSocketsScript = """
            using System.Net.Sockets;
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    using var tcp = new TcpClient();
                    return Task.FromResult(new OrchestrationResult("succeeded", "bad"));
                }
            }
            """;

        const string compilerErrorScript = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    int wrongType = "not-an-int";
                    return Task.FromResult(new OrchestrationResult("succeeded", wrongType.ToString()));
                }
            }
            """;

        var compiler = CreateCompiler();
        var model = new ScriptedOrchestrationModel(forbiddenSocketsScript, compilerErrorScript, ValidScript);
        var contracts = NeuronContractDiscovery.Discover(filterAliases: ["notifications"]);

        var outcome = await compiler.CompileWithRepairAsync(
            brainId: new BrainId("brain-unit"),
            appKey: CodeOrchestrationScriptWrapper.AppKey("run-repair-2"),
            title: "Repair Run",
            goal: "Notify user safely",
            inputJson: "{}",
            availableContracts: contracts,
            allowedContracts: ["notifications"],
            preauthoredScript: null,
            model: model,
            ct: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Diagnostics));
        Assert.Equal(2, outcome.RepairRoundsUsed);
        Assert.Equal(3, model.Prompts.Count);

        // Round 1 receives AST diagnostic from round 0.
        Assert.Equal(1, model.Prompts[1].RepairRound);
        Assert.Contains(model.Prompts[1].PreviousDiagnostics, d => d.Contains("AST002", StringComparison.Ordinal));

        // Round 2 receives Roslyn compiler diagnostic (CS0029) from round 1.
        Assert.Equal(2, model.Prompts[2].RepairRound);
        Assert.Contains(model.Prompts[2].PreviousDiagnostics, d => d.Contains("CS0029", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailsCleanlyAfterExhaustingTwoRepairRounds()
    {
        const string broken = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    return Task.FromResult(UnknownIdentifier);
                }
            }
            """;

        var compiler = CreateCompiler();
        var model = new ScriptedOrchestrationModel(broken, broken, broken);
        var contracts = NeuronContractDiscovery.Discover(filterAliases: ["notifications"]);

        var outcome = await compiler.CompileWithRepairAsync(
            brainId: new BrainId("brain-unit"),
            appKey: CodeOrchestrationScriptWrapper.AppKey("run-fail"),
            title: "Failing Run",
            goal: "Broken script",
            inputJson: "{}",
            availableContracts: contracts,
            allowedContracts: ["notifications"],
            preauthoredScript: null,
            model: model,
            ct: TestContext.Current.CancellationToken);

        Assert.False(outcome.Succeeded);
        Assert.Equal(CodeOrchestrationCompiler.MaxRepairRounds, outcome.RepairRoundsUsed);
        Assert.Equal(3, model.Prompts.Count);
        Assert.Contains(outcome.Diagnostics, d => d.Contains("CS0103", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RejectsScriptReferencingContractOutsideAllowedScope()
    {
        const string folderScript = """
            using System.Threading;
            using System.Threading.Tasks;
            using IntoChat.Sdk;

            public static class OrchestrationScript
            {
                public static async Task<OrchestrationResult> RunAsync(ISandboxBrain os, string inputJson, CancellationToken ct)
                {
                    _ = await os.Folder("docs").ListAsync("", ct);
                    return new OrchestrationResult("succeeded", "listed folder");
                }
            }
            """;

        var compiler = CreateCompiler();
        var model = new ScriptedOrchestrationModel(folderScript, ValidScript);
        var contracts = NeuronContractDiscovery.Discover(filterAliases: ["notifications"]);

        var outcome = await compiler.CompileWithRepairAsync(
            brainId: new BrainId("brain-unit"),
            appKey: CodeOrchestrationScriptWrapper.AppKey("run-scope-repair"),
            title: "Scoped Run",
            goal: "Only use notifications",
            inputJson: "{}",
            availableContracts: contracts,
            allowedContracts: ["notifications"],
            preauthoredScript: null,
            model: model,
            ct: TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, string.Join(Environment.NewLine, outcome.Diagnostics));
        Assert.Equal(1, outcome.RepairRoundsUsed);
        Assert.Contains(model.Prompts[1].PreviousDiagnostics, d => d.Contains("SCOPE001", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtractsStructuredResultJsonWithStatusSummaryArtifactsAndMetrics()
    {
        // Sandbox command dispatch returns JSON-serialized string result.
        const string innerJson = """
            {
              "status": "succeeded",
              "summary": "Triaged 4 PRs",
              "artifacts": [
                { "name": "triage.md", "kind": "markdown", "content": "# Triage" }
              ],
              "metrics": {
                "prsReviewed": 4,
                "elapsedMs": 128.5
              }
            }
            """;
        var wireDoubleEncoded = System.Text.Json.JsonSerializer.Serialize(innerJson);

        var parsed = CodeOrchestrationScriptWrapper.ParseResultJson(wireDoubleEncoded);
        Assert.Equal("succeeded", parsed.Status);
        Assert.Equal("Triaged 4 PRs", parsed.Summary);
        var artifact = Assert.Single(parsed.Artifacts);
        Assert.Equal("triage.md", artifact.Name);
        Assert.Equal("markdown", artifact.Kind);
        Assert.Equal("# Triage", artifact.Content);
        Assert.Contains(parsed.Metrics, m => m.Name == "prsReviewed" && m.Value == 4);
        Assert.Contains(parsed.Metrics, m => m.Name == "elapsedMs" && m.Value == 128.5);

        var roundTripped = CodeOrchestrationScriptWrapper.ParseResultJson(
            CodeOrchestrationScriptWrapper.SerializeResultJson(parsed));
        Assert.Equal(parsed.Status, roundTripped.Status);
        Assert.Equal(parsed.Summary, roundTripped.Summary);
        Assert.Single(roundTripped.Artifacts);
        Assert.Equal(2, roundTripped.Metrics.Length);
    }
}
