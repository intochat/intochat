using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using IntoChat.OS.Modules.Microsoft.Roslyn;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps.Tests.Unit.Flows;

public sealed class MultiModelEscalationFacts : IDisposable
{
    private const string LocalModel = "qwen2.5-coder:14b";
    private const string LocalProfile = "local-coder";

    private const string BrokenSource = """
        namespace Demo;

        public static class Calculator
        {
            public static int Broken() => 42
        }
        """;

    private const string FixedSource = """
        namespace Demo;

        public static class Calculator
        {
            public static int Fixed() => 42;
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "escalation-facts-" + Guid.NewGuid().ToString("N")[..8]);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // DecideFrontier walks the configured cloud role, the stock cloud default, then the catalog in order;
    // with only an Anthropic key the first tool-capable Anthropic card wins.
    private static string ExpectedFrontier => KnownModels.All
        .First(static c => c.Provider == AiProvider.Anthropic && (c.Can & (Abilities.Text | Abilities.Tools)) == (Abilities.Text | Abilities.Tools))
        .Id.Value;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task HarnessEscalatesToFrontierModelAfterTwoFailedLocalRepairsAndRecordsTheTransition()
    {
        var wtDir = await CreateWorktreeAsync("frontier-wt");
        var inference = new RoutingInference(
            local: _ => throw new InvalidOperationException("The local model is served by the ICoder in this fact."),
            frontier:
            [
                _ => new AiMessage("assistant", [new AiToolCall("f-1", "write_file", ToolArgs("code/Calculator.cs", FixedSource))]),
                _ => new AiMessage("assistant", [new AiText("Added the missing semicolon.")]),
            ]);
        await using var services = BuildServices(inference, frontierKeyed: true);
        var coder = new AlwaysBrokenLocalCoder();
        var progress = new List<OperationProgressed>();

        var harness = InoNativeHarness.CreateDefault(services, coder: coder, roslyn: new WorktreeRoslynAnalyzer());
        var result = await harness.ExecuteAsync(Request(wtDir), sig => Record(progress, sig), Ct);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.Equal(1 + InoNativeHarness.MaxSelfRepairTurns, coder.Invocations);
        Assert.Equal(InoNativeHarness.MaxSelfRepairTurns + 1, result.SelfRepairAttempts);
        Assert.Equal(ExpectedFrontier, result.ModelId);
        Assert.Equal("completed", Assert.Single(result.CompletedSlices).Status);

        var escalation = Assert.Single(progress, static p => p.Phase == InoNativeHarness.FrontierEscalationPhase);
        Assert.Equal(
            $"Escalating CS1002 in code/Calculator.cs from local model '{LocalModel}' to frontier model '{ExpectedFrontier}' after {InoNativeHarness.MaxSelfRepairTurns} failed local repair(s)",
            escalation.Detail);
        Assert.Contains(result.ProgressEvents, static p => p.Phase == InoNativeHarness.FrontierEscalationPhase);

        var frontierCall = Assert.Single(inference.Prompts);
        Assert.Contains("[Intra-Slice Frontier Escalation", frontierCall, StringComparison.Ordinal);
        Assert.Contains("CS1002", frontierCall, StringComparison.Ordinal);
        Assert.All(inference.FrontierSelections, static s => Assert.Equal(ExpectedFrontier, s));
        Assert.Contains("Fixed()", await File.ReadAllTextAsync(Path.Combine(wtDir, "code", "Calculator.cs"), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HarnessWithoutFrontierCredentialsKeepsTheLocalFailureWithoutEscalating()
    {
        var wtDir = await CreateWorktreeAsync("no-frontier-wt");
        var inference = new RoutingInference(local: _ => throw new InvalidOperationException("unused"), frontier: []);
        await using var services = BuildServices(inference, frontierKeyed: false);
        var coder = new AlwaysBrokenLocalCoder();
        var progress = new List<OperationProgressed>();

        var harness = InoNativeHarness.CreateDefault(services, coder: coder, roslyn: new WorktreeRoslynAnalyzer());
        var result = await harness.ExecuteAsync(Request(wtDir), sig => Record(progress, sig), Ct);

        Assert.False(result.Succeeded);
        Assert.StartsWith("Roslyn error CS1002", result.FailureDetail, StringComparison.Ordinal);
        Assert.Equal(1 + InoNativeHarness.MaxSelfRepairTurns, coder.Invocations);
        Assert.Equal(InoNativeHarness.MaxSelfRepairTurns, result.SelfRepairAttempts);
        Assert.Equal(LocalModel, result.ModelId);
        Assert.DoesNotContain(progress, static p => p.Phase == InoNativeHarness.FrontierEscalationPhase);
        Assert.Empty(inference.Prompts);
    }

    [Fact]
    public async Task HarnessHonoursTheEscalationSwitch()
    {
        var wtDir = await CreateWorktreeAsync("switch-off-wt");
        var inference = new RoutingInference(local: _ => throw new InvalidOperationException("unused"), frontier: []);
        await using var services = BuildServices(
            inference,
            frontierKeyed: true,
            new Dictionary<string, string?> { ["IntoChatOS:Coder:EscalateToFrontier"] = "false" });
        var coder = new AlwaysBrokenLocalCoder();

        var harness = InoNativeHarness.CreateDefault(services, coder: coder, roslyn: new WorktreeRoslynAnalyzer());
        var result = await harness.ExecuteAsync(Request(wtDir), onProgress: null, Ct);

        Assert.False(result.Succeeded);
        Assert.Equal(InoNativeHarness.MaxSelfRepairTurns, result.SelfRepairAttempts);
        Assert.DoesNotContain(result.ProgressEvents, static p => p.Phase == InoNativeHarness.FrontierEscalationPhase);
        Assert.Empty(inference.Prompts);
    }

    [Fact]
    public async Task LocalLlmCoderEscalatesASingleTaskToTheFrontierModelWhenTheLocalModelFails()
    {
        var treeDir = await CreateWorktreeAsync("coder-wt");
        var inference = new RoutingInference(
            local: _ => new AiMessage("assistant", [new AiText("Thinking about it.")]),
            frontier:
            [
                _ => new AiMessage("assistant", [new AiToolCall("f-1", "write_file", ToolArgs("code/Calculator.cs", FixedSource))]),
                _ => new AiMessage("assistant", [new AiText("Fixed Calculator.")]),
            ]);
        await using var services = BuildServices(inference, frontierKeyed: true);

        var coder = new LocalLlmCoder(services, options: new LocalLlmCoderOptions { MaxToolRounds = 2 });
        var edit = await coder.EditAsync(Lease(treeDir), "Fix Calculator", Ct);

        Assert.Equal(ExpectedFrontier, edit.ModelId);
        Assert.Equal(["code/Calculator.cs"], edit.TouchedFiles);
        Assert.Equal(2, inference.LocalCalls);
        Assert.Equal([ExpectedFrontier, ExpectedFrontier], inference.FrontierSelections);
    }

    [Fact]
    public async Task LocalLlmCoderWithoutFrontierCredentialsOrWithEscalationOffRethrowsTheLocalFailure()
    {
        var treeDir = await CreateWorktreeAsync("coder-no-frontier-wt");
        var inference = new RoutingInference(local: _ => new AiMessage("assistant", [new AiText("Thinking.")]), frontier: []);

        await using (var unkeyed = BuildServices(inference, frontierKeyed: false))
        {
            var coder = new LocalLlmCoder(unkeyed, options: new LocalLlmCoderOptions { MaxToolRounds = 1 });
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => coder.EditAsync(Lease(treeDir), "Fix Calculator", Ct));
            Assert.Contains(LocalModel, ex.Message, StringComparison.Ordinal);
        }

        await using (var keyed = BuildServices(inference, frontierKeyed: true))
        {
            var coder = new LocalLlmCoder(keyed, options: new LocalLlmCoderOptions { MaxToolRounds = 1, EscalateToFrontierOnRepeatedFailure = false });
            await Assert.ThrowsAsync<InvalidOperationException>(() => coder.EditAsync(Lease(treeDir), "Fix Calculator", Ct));
        }

        Assert.Empty(inference.FrontierSelections);
        Assert.False(LocalLlmCoder.BindOptions(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["IntoChatOS:Coder:EscalateToFrontier"] = "false" })
            .Build()).EscalateToFrontierOnRepeatedFailure);
        Assert.True(LocalLlmCoder.BindOptions(null).EscalateToFrontierOnRepeatedFailure);
    }

    private static ServiceProvider BuildServices(
        IModelInference inference,
        bool frontierKeyed,
        Dictionary<string, string?>? settings = null)
    {
        var credentials = new FixedAiCredentials();
        if (frontierKeyed)
        {
            credentials.Ready(AiIntegrations.IdOf(AiProvider.Anthropic), "test-key-not-real");
        }

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build());
        services.AddOptions<AIOptions>().Configure(o =>
        {
            o.ModelProfiles[LocalProfile] = new AIModelProfileOptions
            {
                Provider = nameof(AiProvider.Ollama),
                Model = LocalModel,
                Endpoint = "http://127.0.0.1:11434",
                Capabilities = Abilities.Text | Abilities.Tools,
            };
            o.Roles.Device[ModelRole.Think] = $"profile:{LocalProfile}";
            o.Roles.Cloud[ModelRole.Think] = $"profile:{LocalProfile}";
        });
        services.AddSingleton<IAiCredentials>(credentials);
        services.AddSingleton<ModelCatalog>();
        services.AddSingleton(inference);
        return services.BuildServiceProvider();
    }

    private async Task<string> CreateWorktreeAsync(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "code"));
        await File.WriteAllTextAsync(Path.Combine(dir, ".git"), "gitdir: C:/fake/wt\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "code", "Calculator.cs"), "namespace Demo;\npublic static class Calculator {}\n", Ct);
        return dir;
    }

    private static string ToolArgs(string path, string content)
        => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path, ["content"] = content });

    private static Task Record(List<OperationProgressed> sink, OperationProgressed signal)
    {
        lock (sink)
        {
            sink.Add(signal);
        }

        return Task.CompletedTask;
    }

    private static EngineeringHarnessRequest Request(string worktree)
    {
        var slice = new EngineerTaskSlice(
            SliceId: "slice-fix",
            Title: "Fix Calculator",
            OwnedPaths: ["code/Calculator.cs"],
            DependsOnSliceIds: [],
            AssignedAgentKey: "ino.slice-fix",
            Language: "csharp",
            Instructions: "Make Calculator compile.");
        return new EngineeringHarnessRequest(
            FlowId: "owner/flow.engineer/escalate",
            RunId: "escalate",
            RepositoryId: "os",
            Goal: "Fix Calculator",
            WorktreeId: "wt-0",
            WorktreePath: worktree,
            InstanceIndex: 0,
            TotalInstances: 1,
            Strategy: "surgical-patch",
            StrategyLabel: "Minimal Surgical Patch",
            StrategyPromptLens: "Minimal diff.",
            Harness: EngineeringHarnessKind.InoNative,
            RatifiedPlan: new RatifiedPlan("Fix Calculator", "Single slice", [], [slice]));
    }

    private static WorkTreeLease Lease(string tree) => new(
        RunId: "escalate-coder",
        RepositoryId: "os",
        SourceRepoPath: tree,
        TreePath: tree,
        RealGitDirPath: tree,
        GitPointerSha256Hex: new string('b', 64),
        BaseBranch: "main",
        BaseCommitSha: new string('a', 40),
        BranchName: "ino/escalate-coder",
        GitHubRepo: "intochat/os");

    // Stands in for the local model: every turn leaves a missing-semicolon error behind.
    private sealed class AlwaysBrokenLocalCoder : ICoder
    {
        private int _invocations;

        public int Invocations => Volatile.Read(ref _invocations);

        public async Task<CoderEditResult> EditAsync(WorkTreeLease lease, string goal, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _invocations);
            await File.WriteAllTextAsync(Path.Combine(lease.TreePath, "code", "Calculator.cs"), BrokenSource, ct);
            return new CoderEditResult("Still missing a semicolon.", ["code/Calculator.cs"], LocalModel);
        }
    }

    // Answers local-profile turns from one script and frontier (card id) turns from another.
    private sealed class RoutingInference(Func<InferenceRequest, AiMessage> local, Func<InferenceRequest, AiMessage>[] frontier) : IModelInference
    {
        private readonly Queue<Func<InferenceRequest, AiMessage>> _frontier = new(frontier);
        private readonly Lock _gate = new();

        public List<string> FrontierSelections { get; } = [];

        public List<string> Prompts { get; } = [];

        public int LocalCalls { get; private set; }

        public bool HasProfile(string name) => string.Equals(name, LocalProfile, StringComparison.OrdinalIgnoreCase);

        public Task<ModelDescriptor> Describe(ModelSelection? selection, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => Task.FromResult(IsLocal(selection)
                ? new ModelDescriptor("Ollama", LocalModel, LocalProfile, "local", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported)
                : new ModelDescriptor("Anthropic", selection!.Model!, "default", "cloud", CapabilitySupport.Supported, CapabilitySupport.Unsupported, CapabilitySupport.Supported));

        public Task<InferenceResult> Generate(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
        {
            lock (_gate)
            {
                if (IsLocal(request.Model))
                {
                    LocalCalls++;
                    return Task.FromResult(new InferenceResult([local(request)], "stop", null, null, LocalModel));
                }

                FrontierSelections.Add(request.Model!.Model!);
                if (request.Messages.FirstOrDefault(static m => m.Role == "user") is { } user && FrontierSelections.Count == 1)
                {
                    Prompts.Add(string.Concat(user.Content.OfType<AiText>().Select(static t => t.Text)));
                }

                return Task.FromResult(new InferenceResult([_frontier.Dequeue()(request)], "stop", null, null, request.Model.Model!));
            }
        }

        public IAsyncEnumerable<InferenceUpdate> GenerateStreaming(InferenceRequest request, ModelId? model, CancellationToken cancellationToken, string? brainId)
            => throw new NotSupportedException();

        private static bool IsLocal(ModelSelection? selection)
            => selection?.Model is null || selection.Model.StartsWith("profile:", StringComparison.Ordinal);
    }
}
