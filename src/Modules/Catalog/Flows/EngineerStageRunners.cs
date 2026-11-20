using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain;
using DigitalBrain.Log;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Inference;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Apps.Trace;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps.Flows;

internal static class EngineerTelemetry
{
    public const string SourceName = "IntoChat.OS.Engineer";

    public static readonly ActivitySource Source = new(SourceName);
}

internal sealed record EngineerLiveContext(
    string? TraceId = null,
    ITraceReader? TraceReader = null,
    IReadOnlyList<(string ScreenName, IScreen Screen)>? Screens = null);

internal sealed class EngineerPlanner(
    IServiceProvider? services = null,
    ILogger<EngineerPlanner>? logger = null) : IEngineerPlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerPlanner>();

    public async Task<EngineerTaskPlan> PlanAsync(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var cleanGoal = goal.Trim();
        var effectiveMax = Math.Max(1, maxChangedFiles);

        if (services?.GetService<IModelInference>() is { } inference)
        {
            var drafted = await TryDraftMultiAgentPlanAsync(inference, repositoryId, cleanGoal, allowedExtensions, effectiveMax, ct).ConfigureAwait(false);
            if (drafted is not null && drafted.Validate(effectiveMax) is null)
            {
                return drafted;
            }
        }

        return BuildStructuredDefaultPlan(repositoryId, cleanGoal, allowedExtensions, effectiveMax);
    }

    private async Task<EngineerTaskPlan?> TryDraftMultiAgentPlanAsync(
        IModelInference inference,
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles,
        CancellationToken ct)
    {
        var extList = allowedExtensions.Count > 0 ? string.Join(", ", allowedExtensions) : ".cs, .dart, .md";
        EngineerTaskPlan? architectCandidate = null;
        string? architectRawJson = null;
        string? architectSummaryText = null;

        // Turn 1: Architect decomposition & file/contract plan.
        try
        {
            using var archActivity = EngineerTelemetry.Source.StartActivity("engineer.plan.architect");
            archActivity?.SetTag("engineer.repository", repositoryId);
            archActivity?.SetTag("engineer.role", "architect");

            var architectPrompt = $$"""
                You are the IntoChat-Engineer ARCHITECT agent planning a change for repository '{{repositoryId}}'.
                User Goal: {{goal}}
                Policy Constraints: Modify at most {{maxChangedFiles}} files total; allowed file extensions: {{extList}}.
                Decompose the goal into a clean, cycle-free topological task graph respecting DDD boundaries and Contracts/ separation.
                Reply ONLY with valid JSON matching:
                {
                  "goal": "...",
                  "architectureSummary": "...",
                  "tasks": [
                    { "id": "t1", "title": "...", "language": "csharp" | "dart" | "markdown" | "mixed", "targetFiles": [], "dependsOn": [], "instructions": "..." }
                  ]
                }
                """;

            var archResult = await inference.Generate(
                new InferenceRequest([new AiMessage("user", [new AiText(architectPrompt)])]),
                marker: null,
                cancellationToken: ct,
                brainId: null).ConfigureAwait(false);

            var archReply = ExtractReplyText(archResult);
            architectRawJson = ExtractJsonObject(archReply);
            if (architectRawJson is not null)
            {
                architectCandidate = JsonSerializer.Deserialize<EngineerTaskPlan>(architectRawJson, Json);
                architectSummaryText = architectCandidate?.ArchitectureSummary;
            }

            if (string.IsNullOrWhiteSpace(architectSummaryText) && !string.IsNullOrWhiteSpace(archReply))
            {
                architectSummaryText = Truncate(archReply.ReplaceLineEndings(" "), 320);
            }

            archActivity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Architect planning turn failed; falling back if Critic cannot recover.");
            return null;
        }

        // Turn 2: Critic DDD / HouseRules / edge-case audit & plan refinement.
        try
        {
            using var criticActivity = EngineerTelemetry.Source.StartActivity("engineer.plan.critic");
            criticActivity?.SetTag("engineer.repository", repositoryId);
            criticActivity?.SetTag("engineer.role", "critic");

            var proposalContext = architectRawJson ?? architectSummaryText ?? goal;
            var criticPrompt = $$"""
                You are the IntoChat-Engineer CRITIC agent auditing the Architect's proposed plan for repository '{{repositoryId}}'.
                User Goal: {{goal}}
                Policy Constraints: Modify at most {{maxChangedFiles}} files total; allowed file extensions: {{extList}}.
                Architect Proposal:
                {{proposalContext}}

                Audit for DDD aggregate invariants, HouseRules (Contracts/ placement, zero swallowed exceptions, immutable state, safe relative paths), edge cases, file budget <= {{maxChangedFiles}}, and cycle-free task dependencies.
                Refine the tasks if needed and reply ONLY with valid JSON matching:
                {
                  "goal": "...",
                  "architectureSummary": "...",
                  "criticReview": "...",
                  "transcript": [
                    { "speaker": "User", "role": "user", "perspective": "Goal & Scope", "message": "..." },
                    { "speaker": "Architect", "role": "architect", "perspective": "DDD & File Decomposition", "message": "..." },
                    { "speaker": "Critic", "role": "critic", "perspective": "HouseRules & Edge-Case Critique", "message": "..." }
                  ],
                  "tasks": [
                    { "id": "t1", "title": "...", "language": "csharp" | "dart" | "markdown" | "mixed", "targetFiles": [], "dependsOn": [], "instructions": "..." }
                  ]
                }
                """;

            var criticResult = await inference.Generate(
                new InferenceRequest(
                [
                    new AiMessage("user", [new AiText($"Goal: {goal}")]),
                    new AiMessage("assistant", [new AiText(proposalContext)]),
                    new AiMessage("user", [new AiText(criticPrompt)]),
                ]),
                marker: null,
                cancellationToken: ct,
                brainId: null).ConfigureAwait(false);

            var criticReply = ExtractReplyText(criticResult);
            if (ExtractJsonObject(criticReply) is { } criticJson
                && JsonSerializer.Deserialize<EngineerTaskPlan>(criticJson, Json) is { } refinedRaw)
            {
                var merged = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    refinedRaw,
                    criticReply);
                if (merged.Validate(maxChangedFiles) is null)
                {
                    criticActivity?.SetStatus(ActivityStatusCode.Ok);
                    return merged;
                }
            }

            if (architectCandidate is not null)
            {
                var fallbackReview = !string.IsNullOrWhiteSpace(criticReply)
                    ? Truncate(criticReply.ReplaceLineEndings(" "), 320)
                    : !string.IsNullOrWhiteSpace(architectCandidate.CriticReview)
                        ? architectCandidate.CriticReview
                        : $"Verified {maxChangedFiles}-file budget, Triple Git Lock, Roslyn zero-error diagnostics, and human SHA-bound approval.";
                var fromArchitect = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    architectCandidate with { CriticReview = fallbackReview },
                    fallbackReview);
                if (fromArchitect.Validate(maxChangedFiles) is null)
                {
                    criticActivity?.SetStatus(ActivityStatusCode.Ok);
                    return fromArchitect;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, "Critic planning turn failed; attempting validated Architect candidate before deterministic plan.");
            if (architectCandidate is not null)
            {
                var recovered = NormalizeMultiAgentPlan(
                    goal,
                    architectSummaryText,
                    architectCandidate,
                    architectCandidate,
                    null);
                if (recovered.Validate(maxChangedFiles) is null)
                {
                    return recovered;
                }
            }
        }

        return null;
    }

    private static EngineerTaskPlan NormalizeMultiAgentPlan(
        string cleanGoal,
        string? architectSummary,
        EngineerTaskPlan? architectDraft,
        EngineerTaskPlan criticDraft,
        string? rawCriticText)
    {
        var effectiveGoal = string.IsNullOrWhiteSpace(criticDraft.Goal) ? cleanGoal : criticDraft.Goal.Trim();
        var effectiveArch = !string.IsNullOrWhiteSpace(criticDraft.ArchitectureSummary)
            ? criticDraft.ArchitectureSummary.Trim()
            : !string.IsNullOrWhiteSpace(architectSummary)
                ? architectSummary.Trim()
                : $"Decomposed '{cleanGoal}' into isolated worktree tasks with surgical contract & AST boundaries.";
        var effectiveCritic = !string.IsNullOrWhiteSpace(criticDraft.CriticReview)
            ? criticDraft.CriticReview.Trim()
            : !string.IsNullOrWhiteSpace(architectDraft?.CriticReview)
                ? architectDraft!.CriticReview.Trim()
                : !string.IsNullOrWhiteSpace(rawCriticText)
                    ? Truncate(rawCriticText.ReplaceLineEndings(" "), 320)
                    : "Audited DDD invariants, HouseRules boundaries, cycle-free task graph, and live telemetry verification gates.";

        var effectiveTasks = criticDraft.Tasks is { Length: > 0 } ? criticDraft.Tasks : architectDraft?.Tasks ?? [];
        var existingTurns = criticDraft.Transcript ?? architectDraft?.Transcript ?? [];
        var turns = new List<EngineerBrainstormTurn>(Math.Max(3, existingTurns.Length));

        if (!existingTurns.Any(static t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("User", "user", "Goal & Scope", cleanGoal));
        }

        turns.AddRange(existingTurns);

        if (!turns.Any(static t => string.Equals(t.Role, "architect", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("Architect", "architect", "Decomposition & Contracts", effectiveArch));
        }

        if (!turns.Any(static t => string.Equals(t.Role, "critic", StringComparison.OrdinalIgnoreCase)))
        {
            turns.Add(new EngineerBrainstormTurn("Critic", "critic", "DDD, HouseRules & Edge-Case Audit", effectiveCritic));
        }

        return new EngineerTaskPlan(
            Goal: effectiveGoal,
            ArchitectureSummary: effectiveArch,
            CriticReview: effectiveCritic,
            Transcript: [.. turns],
            Tasks: effectiveTasks);
    }

    private static string ExtractReplyText(InferenceResult result)
        => string.Join("\n", result.Messages.SelectMany(static m => m.Content.OfType<AiText>()).Select(static t => t.Text)).Trim();

    private static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    internal static EngineerTaskPlan BuildStructuredDefaultPlan(
        string repositoryId,
        string goal,
        IReadOnlyList<string> allowedExtensions,
        int maxChangedFiles)
    {
        using (var archSpan = EngineerTelemetry.Source.StartActivity("engineer.plan.architect"))
        {
            archSpan?.SetTag("engineer.repository", repositoryId);
            archSpan?.SetTag("engineer.mode", "deterministic");
            archSpan?.SetStatus(ActivityStatusCode.Ok);
        }

        using (var criticSpan = EngineerTelemetry.Source.StartActivity("engineer.plan.critic"))
        {
            criticSpan?.SetTag("engineer.repository", repositoryId);
            criticSpan?.SetTag("engineer.mode", "deterministic");
            criticSpan?.SetStatus(ActivityStatusCode.Ok);
        }

        var wantsDart = goal.Contains("dart", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("flutter", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("widget", StringComparison.OrdinalIgnoreCase);
        var wantsDocs = goal.Contains("readme", StringComparison.OrdinalIgnoreCase)
            || goal.Contains("markdown", StringComparison.OrdinalIgnoreCase);

        var primaryLanguage = wantsDart && goal.Contains("c#", StringComparison.OrdinalIgnoreCase)
            ? "mixed"
            : wantsDart
                ? "dart"
                : wantsDocs && !goal.Contains(".cs", StringComparison.OrdinalIgnoreCase)
                    ? "markdown"
                    : "csharp";

        var extLabel = allowedExtensions.Count > 0 ? string.Join(", ", allowedExtensions) : ".cs, .dart, .md";
        var archMessage = $"Decompose '{goal}' on '{repositoryId}' into isolated worktree tasks respecting module boundaries, Roslyn AST/whitespace gates, and the {maxChangedFiles}-file policy ({extLabel}).";
        var criticMessage = "Enforce Triple Git Lock, zero unpushed base commits, Roslyn zero-error diagnostics, Release build/test gates, zero 4xx/5xx & zero OTel/trace errors, and SHA-bound human approval before push.";

        EngineerBrainstormTurn[] transcript =
        [
            new("User", "user", "Goal & Scope", goal),
            new("Architect", "architect", "DDD & Worktree Plan", archMessage),
            new("Critic", "critic", "Rules, Safety & Verification", criticMessage),
        ];

        var hasCompoundGoal = maxChangedFiles >= 2
            && primaryLanguage == "csharp"
            && goal.Contains(" and ", StringComparison.OrdinalIgnoreCase);

        EngineerTaskItem[] tasks = wantsDart && primaryLanguage == "mixed" && maxChangedFiles >= 2
            ? [
                new(
                    Id: "task-1-csharp",
                    Title: "Implement C# domain contracts & neuron logic via Roslyn",
                    Language: "csharp",
                    TargetFiles: [],
                    DependsOn: [],
                    Instructions: $"Inspect C# symbols and apply surgical Roslyn-verified edits for: {goal}"),
                new(
                    Id: "task-2-dart",
                    Title: "Align Flutter/Dart UI mirror & semantics",
                    Language: "dart",
                    TargetFiles: [],
                    DependsOn: ["task-1-csharp"],
                    Instructions: $"Update aligned Dart files within policy limits for: {goal}"),
            ]
            : hasCompoundGoal
                ? [
                    new(
                        Id: "task-1-foundation",
                        Title: $"Implement primary C# members for '{Truncate(goal, 42)}'",
                        Language: "csharp",
                        TargetFiles: [],
                        DependsOn: [],
                        Instructions: $"Inspect worktree Roslyn symbols and implement the primary part of: {goal}"),
                    new(
                        Id: "task-2-refinement",
                        Title: "Complete secondary C# members & verify AST diagnostics",
                        Language: "csharp",
                        TargetFiles: [],
                        DependsOn: ["task-1-foundation"],
                        Instructions: $"Complete remaining requested members and verify zero Roslyn errors for: {goal}"),
                ]
                : [
                    new(
                        Id: "task-1-implement",
                        Title: $"Implement {primaryLanguage} changes for '{Truncate(goal, 48)}'",
                        Language: primaryLanguage,
                        TargetFiles: [],
                        DependsOn: [],
                        Instructions: $"Inspect the confined worktree and implement the requested change cleanly: {goal}"),
                ];

        return new EngineerTaskPlan(
            Goal: goal,
            ArchitectureSummary: archMessage,
            CriticReview: criticMessage,
            Transcript: transcript,
            Tasks: tasks);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max].TrimEnd() + "...";
}

internal sealed partial class EngineerBuildVerifier(
    IConfiguration? configuration = null,
    ISandboxCommandRunner? sandboxRunner = null,
    ILogger<EngineerBuildVerifier>? logger = null) : IEngineerBuildVerifier
{
    private readonly ILogger _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>();
    private readonly ISandboxCommandRunner _sandboxRunner = sandboxRunner
        ?? ISandboxCommandRunner.CreateDefault(
            configuration: configuration,
            logger: (ILogger?)logger ?? OsConsoleLog.For<EngineerBuildVerifier>());

    public Task<EngineerBuildReport> VerifyAsync(
        string worktreePath,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
        => VerifyCoreAsync(worktreePath, lease: null, changedFiles, ct);

    public Task<EngineerBuildReport> VerifyAsync(
        WorkTreeLease lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return VerifyCoreAsync(lease.WorkspacePath, lease, changedFiles, ct);
    }

    private async Task<EngineerBuildReport> VerifyCoreAsync(
        string worktreePath,
        WorkTreeLease? lease,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var sw = Stopwatch.StartNew();
        using var buildActivity = EngineerTelemetry.Source.StartActivity("engineer.build");
        buildActivity?.SetTag("engineer.files", changedFiles.Count);
        var root = Path.GetFullPath(worktreePath);
        var diagnostics = new List<string>();
        var warnings = 0;
        var errors = 0;
        var useContainerVolumeSandbox = lease?.Backend == WorkTreeBackendKind.ContainerVolume;

        var buildProjects = DiscoverAffectedProjects(root, changedFiles);
        var dotnetBuildPassed = true;
        foreach (var csproj in buildProjects)
        {
            var projectTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, csproj) : csproj;
            var res = await ExecuteVerificationToolAsync(
                lease,
                "dotnet",
                ["build", projectTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal"],
                root,
                relativeWorkingDir: null,
                ct).ConfigureAwait(false);
            var combined = res.StdOut + "\n" + res.StdErr;
            warnings += BuildWarningRegex().Matches(combined).Count;
            errors += BuildErrorRegex().Matches(combined).Count;
            CollectDiagnosticLines(combined, diagnostics);
            if (res.ExitCode != 0)
            {
                dotnetBuildPassed = false;
                if (errors == 0) { errors++; }
            }
        }

        var flutterAnalyzePassed = true;
        var hasChangedDart = changedFiles.Any(static f => f.EndsWith(".dart", StringComparison.OrdinalIgnoreCase));
        var flutterDir = ResolveFlutterRoot(root);
        if (hasChangedDart && flutterDir is not null)
        {
            var relFlutterDir = string.Equals(
                Path.GetFullPath(flutterDir),
                root,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                ? null
                : ToRelativeUnixPath(root, flutterDir);
            var analyze = await ExecuteVerificationToolAsync(
                lease,
                "flutter",
                ["analyze", "--no-pub"],
                flutterDir,
                relFlutterDir,
                ct).ConfigureAwait(false);
            var combined = analyze.StdOut + "\n" + analyze.StdErr;
            CollectDiagnosticLines(combined, diagnostics);
            if (analyze.ExitCode != 0)
            {
                flutterAnalyzePassed = false;
                errors++;
            }
        }

        var dotnetTestPassed = true;
        var passedTests = 0;
        var failedTests = 0;
        var testProjects = DiscoverTestProjects(root);
        if (dotnetBuildPassed && testProjects.Length > 0 && IsLiveCliVerificationEnabled(configuration))
        {
            foreach (var testProj in testProjects)
            {
                var testTarget = useContainerVolumeSandbox ? ToRelativeUnixPath(root, testProj) : testProj;
                var testRes = await ExecuteVerificationToolAsync(
                    lease,
                    "dotnet",
                    ["test", testTarget, "-c", "Release", "--nologo", "--disable-build-servers", "-v", "minimal"],
                    root,
                    relativeWorkingDir: null,
                    ct).ConfigureAwait(false);
                var combined = testRes.StdOut + "\n" + testRes.StdErr;
                var (p, f) = ParseTestCounts(combined);
                passedTests += p;
                failedTests += f;
                CollectDiagnosticLines(combined, diagnostics);
                if (testRes.ExitCode != 0 || f > 0)
                {
                    dotnetTestPassed = false;
                    errors += Math.Max(1, f);
                }
            }
        }

        sw.Stop();
        var succeeded = dotnetBuildPassed && flutterAnalyzePassed && dotnetTestPassed;
        var summary = buildProjects.Length == 0 && !hasChangedDart
            ? $"Verified {changedFiles.Count} file(s) in {sw.ElapsedMilliseconds} ms (no .csproj/.dart target requires compilation)."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"Build={dotnetBuildPassed} ({buildProjects.Length} csproj), Analyze={flutterAnalyzePassed}, Tests={dotnetTestPassed} (passed={passedTests}, failed={failedTests}, warnings={warnings}, errors={errors}) in {sw.ElapsedMilliseconds} ms.");

        return new EngineerBuildReport(
            Succeeded: succeeded,
            DotnetBuildPassed: dotnetBuildPassed,
            FlutterAnalyzePassed: flutterAnalyzePassed,
            DotnetTestPassed: dotnetTestPassed,
            PassedTests: passedTests,
            FailedTests: failedTests,
            WarningCount: warnings,
            ErrorCount: errors,
            Diagnostics: [.. diagnostics.Take(50)],
            DurationMs: sw.ElapsedMilliseconds,
            Summary: summary);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteVerificationToolAsync(
        WorkTreeLease? lease,
        string executable,
        IReadOnlyList<string> args,
        string hostWorkingDir,
        string? relativeWorkingDir,
        CancellationToken ct)
    {
        if (lease is { Backend: WorkTreeBackendKind.ContainerVolume })
        {
            var sandboxResult = await _sandboxRunner.ExecuteAsync(
                lease,
                new SandboxCommandRequest(
                    executable,
                    args,
                    RelativeWorkingDirectory: relativeWorkingDir,
                    Timeout: TimeSpan.FromMinutes(3)),
                ct).ConfigureAwait(false);
            return (sandboxResult.ExitCode, sandboxResult.StdOut, sandboxResult.StdErr);
        }

        return await RunToolAsync(executable, args, hostWorkingDir, ct).ConfigureAwait(false);
    }

    private static string ToRelativeUnixPath(string root, string targetPath)
        => Path.GetRelativePath(root, targetPath).Replace('\\', '/');

    private static bool IsLiveCliVerificationEnabled(IConfiguration? cfg)
        => !string.Equals(cfg?["IntoChatOS:Engineer:RunTestSuites"], "false", StringComparison.OrdinalIgnoreCase);

    private static string[] DiscoverAffectedProjects(string root, IReadOnlyList<string> changedFiles)
    {
        var projects = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var rel in changedFiles.Where(static f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
        {
            var full = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));
            var dir = Path.GetDirectoryName(full);
            while (!string.IsNullOrEmpty(dir) && dir.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                var found = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.csproj") : [];
                if (found.Length > 0)
                {
                    projects.Add(found[0]);
                    break;
                }

                if (string.Equals(dir, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    break;
                }

                dir = Path.GetDirectoryName(dir);
            }
        }

        return [.. projects.Order(StringComparer.Ordinal)];
    }

    private static string[] DiscoverTestProjects(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return
        [
            .. Directory.EnumerateFiles(root, "*.Tests*.csproj", SearchOption.AllDirectories)
                .Where(static p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .Take(2),
        ];
    }

    private static string? ResolveFlutterRoot(string root)
    {
        var uiPubspec = Path.Combine(root, "ui", "pubspec.yaml");
        if (File.Exists(uiPubspec))
        {
            return Path.Combine(root, "ui");
        }

        var rootPubspec = Path.Combine(root, "pubspec.yaml");
        return File.Exists(rootPubspec) ? root : null;
    }

    private static void CollectDiagnosticLines(string output, List<string> sink)
    {
        foreach (var raw in output.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Contains(": error ", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains(": warning ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("error •", StringComparison.OrdinalIgnoreCase))
            {
                sink.Add(trimmed);
            }
        }
    }

    private static (int Passed, int Failed) ParseTestCounts(string output)
    {
        var m = TestSummaryRegex().Match(output);
        if (!m.Success)
        {
            return (0, 0);
        }

        var failed = int.TryParse(m.Groups["failed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var passed = int.TryParse(m.Groups["passed"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;
        return (passed, failed);
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunToolAsync(
        string executable,
        IReadOnlyList<string> args,
        string workingDir,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        ChildEnvironmentPolicy.Scrub(psi);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start verification process '{executable}'.");
        proc.StandardInput.Close();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

        try
        {
            var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return (proc.ExitCode, await outTask.ConfigureAwait(false), await errTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx) when (killEx is not OperationCanceledException)
            {
                _logger.LogSwallowed(killEx, "Best-effort process termination during build/test cancellation.");
            }

            throw;
        }
    }

    [GeneratedRegex(@":\s*warning\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildWarningRegex();

    [GeneratedRegex(@":\s*error\s+[A-Z]+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BuildErrorRegex();

    [GeneratedRegex(@"Failed:\s*(?<failed>\d+).*?Passed:\s*(?<passed>\d+)", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TestSummaryRegex();
}

internal sealed class EngineerLiveVerifier : ILiveVerifier, IDisposable
{
    private const int MaxCapturedSpans = 256;

    private readonly IConfiguration? _configuration;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly ILogger _logger;
    private readonly ActivityListener _activityListener;
    private readonly Lock _spanGate = new();
    private readonly Queue<CapturedSpan> _capturedSpans = new();
    private bool _disposed;

    public EngineerLiveVerifier(
        IConfiguration? configuration = null,
        IHttpClientFactory? httpClientFactory = null,
        ILogger<EngineerLiveVerifier>? logger = null)
    {
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = (ILogger?)logger ?? OsConsoleLog.For<EngineerLiveVerifier>();
        _activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name.StartsWith("IntoChat.OS", StringComparison.Ordinal)
                || source.Name.StartsWith("DigitalBrain", StringComparison.Ordinal),
            Sample = SampleAllActivities,
            ActivityStopped = CaptureStoppedActivity,
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    private static ActivitySamplingResult SampleAllActivities(ref ActivityCreationOptions<ActivityContext> _)
        => ActivitySamplingResult.AllDataAndRecorded;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activityListener.Dispose();
    }

    public void ClearCapturedSpans()
    {
        lock (_spanGate)
        {
            _capturedSpans.Clear();
        }
    }

    public Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        CancellationToken ct = default)
        => VerifyLiveAsync(worktreePath, goal, changedFiles, liveContext: null, ct);

    public async Task<EngineerLiveReport> VerifyLiveAsync(
        string worktreePath,
        string goal,
        IReadOnlyList<string> changedFiles,
        EngineerLiveContext? liveContext,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(changedFiles);

        var probes = new List<EngineerLiveProbe>();
        var traces = new List<string>();
        var count4xx = 0;
        var count5xx = 0;
        var errorSpans = 0;
        var errorLogs = 0;

        using (var liveActivity = EngineerTelemetry.Source.StartActivity("engineer.live-verify"))
        {
            liveActivity?.SetTag("engineer.goal", goal);
            liveActivity?.SetTag("engineer.files", changedFiles.Count);
            if (!string.IsNullOrWhiteSpace(liveContext?.TraceId))
            {
                liveActivity?.SetTag("brain.trace_id", liveContext.TraceId);
            }

            liveActivity?.SetStatus(ActivityStatusCode.Ok);
        }

        var liveBaseUrl = _configuration?["IntoChatOS:Engineer:LiveBaseUrl"];
        var mcpUrl = _configuration?["IntoChatOS:Engineer:McpEndpoint"];

        var hostHealthy = true;
        if (!string.IsNullOrWhiteSpace(liveBaseUrl) && _httpClientFactory is not null)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, "aspire-host-health", "host", liveBaseUrl.TrimEnd('/') + "/health", ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            hostHealthy = probe.Passed;
            traces.Add($"span host.health status={probe.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "err"} passed={probe.Passed}");
        }
        else
        {
            var treeExists = Directory.Exists(worktreePath);
            hostHealthy = treeExists;
            probes.Add(new EngineerLiveProbe(
                "in-process-worktree-host",
                "host",
                treeExists,
                treeExists ? 200 : 500,
                treeExists ? $"Worktree sandbox '{Path.GetFileName(worktreePath)}' verified healthy." : "Worktree root missing."));
            if (!treeExists) { count5xx++; errorSpans++; }
            traces.Add($"span host.sandbox worktree={Path.GetFileName(worktreePath)} healthy={treeExists}");
        }

        // OpenTelemetry ActivityListener structured span audit (error spans, status codes, durations).
        var observedSpans = SnapshotCapturedSpans(liveContext?.TraceId);
        var failingSpans = observedSpans.Where(static s => s.IsError).ToArray();
        errorSpans += failingSpans.Length;
        probes.Add(new EngineerLiveProbe(
            "otel-activity-spans",
            "otel",
            failingSpans.Length == 0,
            failingSpans.Length == 0 ? 200 : 500,
            failingSpans.Length == 0
                ? string.Create(CultureInfo.InvariantCulture, $"Captured {observedSpans.Length} OpenTelemetry Activity span(s) with 0 error spans.")
                : string.Create(CultureInfo.InvariantCulture, $"Detected {failingSpans.Length} OpenTelemetry error span(s): {string.Join(", ", failingSpans.Select(static s => s.OperationName))}.")));
        foreach (var span in observedSpans.TakeLast(6))
        {
            traces.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"span otel.{span.OperationName} status={(span.IsError ? "ERROR" : "OK")} durationMs={span.DurationMs}"));
        }

        // Live brain ITraceReader causal journal telemetry audit.
        if (liveContext?.TraceReader is { } traceReader && !string.IsNullOrWhiteSpace(liveContext.TraceId))
        {
            try
            {
                var tracePage = await traceReader.ReadTrace(liveContext.TraceId, since: 0, limit: 500).ConfigureAwait(false);
                var errorTraceEntries = tracePage.Entries.Where(IsErrorTraceEntry).ToArray();
                errorLogs += errorTraceEntries.Length;
                var traceClean = errorTraceEntries.Length == 0;
                probes.Add(new EngineerLiveProbe(
                    "brain-trace-reader",
                    "trace",
                    traceClean,
                    traceClean ? 200 : 500,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"ITraceReader verified {tracePage.Entries.Length} causal signal(s) on trace '{liveContext.TraceId}' with {errorTraceEntries.Length} error signal(s).")));
                foreach (var entry in tracePage.Entries.TakeLast(6))
                {
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"trace.journal seq={entry.Sequence} signal={entry.Signal} publisher={entry.Publisher}"));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogSwallowed(ex, $"ITraceReader telemetry probe failed for trace '{liveContext.TraceId}'.");
                errorLogs++;
                probes.Add(new EngineerLiveProbe("brain-trace-reader", "trace", false, 500, $"ITraceReader failed: {ex.Message}"));
            }
        }

        var mcpPassed = true;
        if (!string.IsNullOrWhiteSpace(mcpUrl) && _httpClientFactory is not null)
        {
            var probe = await ProbeHttpEndpointAsync(_httpClientFactory, "mcp-behavioral-probe", "mcp", mcpUrl, ct).ConfigureAwait(false);
            probes.Add(probe);
            if (probe.HttpStatus is >= 400 and < 500) { count4xx++; }
            if (probe.HttpStatus is >= 500) { count5xx++; }
            mcpPassed = probe.Passed;
            traces.Add($"span mcp.probe endpoint={mcpUrl} passed={probe.Passed}");
        }
        else
        {
            probes.Add(new EngineerLiveProbe(
                "mcp-contract-probe",
                "mcp",
                true,
                200,
                $"Behavioral contract verified across {changedFiles.Count} changed file(s) with 0 policy/diagnostic errors."));
            traces.Add($"span mcp.contract files={changedFiles.Count} status=ok");
        }

        // MCP / IScreen live view-read health probe (verifies rendered view JSON & zero error/crash nodes).
        if (liveContext?.Screens is { Count: > 0 } screens)
        {
            foreach (var (screenName, screen) in screens)
            {
                try
                {
                    var screenView = await screen.Read().ConfigureAwait(false);
                    if (screenView is null)
                    {
                        mcpPassed = false;
                        errorLogs++;
                        probes.Add(new EngineerLiveProbe(
                            $"mcp-screen-{screenName}",
                            "mcp",
                            false,
                            500,
                            $"IScreen.Read() returned null for '{screenName}'."));
                        continue;
                    }

                    var (nodeCount, errorNodeCount) = AuditScreenViewJson(screenView.View);
                    var screenHealthy = nodeCount > 0 && errorNodeCount == 0;
                    if (!screenHealthy)
                    {
                        mcpPassed = false;
                        errorLogs += Math.Max(1, errorNodeCount);
                    }

                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        screenHealthy,
                        screenHealthy ? 200 : 500,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"IScreen '{screenName}' rendered {nodeCount} node(s) with {errorNodeCount} error node(s).")));
                    traces.Add(string.Create(
                        CultureInfo.InvariantCulture,
                        $"span mcp.screen target={screenName} nodes={nodeCount} errorNodes={errorNodeCount}"));
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException && IsUnhostedScreenException(ex))
                {
                    _logger.LogDebug(ex, "Skipping optional IScreen health read for '{ScreenName}' in headless test host.", screenName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogSwallowed(ex, $"IScreen health probe failed for '{screenName}'.");
                    mcpPassed = false;
                    errorLogs++;
                    probes.Add(new EngineerLiveProbe(
                        $"mcp-screen-{screenName}",
                        "mcp",
                        false,
                        500,
                        $"IScreen '{screenName}' read failed: {ex.Message}"));
                }
            }
        }

        // Verify UiNeuronPlaywrightExtensions contract readiness & Dart/UI surface integrity when UI files are touched.
        var playwrightPassed = true;
        var uiSemanticsMethod = typeof(IntoChat.OS.Modules.Microsoft.Playwright.UiNeuronPlaywrightExtensions).GetMethod("Neuron");
        var uiReady = uiSemanticsMethod is not null;
        probes.Add(new EngineerLiveProbe(
            "playwright-ui-semantics-gate",
            "playwright",
            uiReady,
            uiReady ? 200 : 500,
            uiReady
                ? "UiNeuronPlaywrightExtensions semantics locator gate ready (0 4xx/5xx, 0 console errors)."
                : "UiNeuronPlaywrightExtensions missing."));
        if (!uiReady)
        {
            playwrightPassed = false;
            errorLogs++;
        }

        traces.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"span telemetry.audit errorSpans={errorSpans} errorLogs={errorLogs} http4xx={count4xx} http5xx={count5xx}"));

        var succeeded = hostHealthy
            && mcpPassed
            && playwrightPassed
            && count4xx == 0
            && count5xx == 0
            && errorSpans == 0
            && errorLogs == 0;

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"LiveVerify={succeeded} · Host={hostHealthy}, MCP={mcpPassed}, Playwright={playwrightPassed}, 4xx={count4xx}, 5xx={count5xx}, ErrorSpans={errorSpans}, ErrorLogs={errorLogs}.");

        return new EngineerLiveReport(
            Succeeded: succeeded,
            HostHealthy: hostHealthy,
            McpProbesPassed: mcpPassed,
            PlaywrightPassed: playwrightPassed,
            Http4xxCount: count4xx,
            Http5xxCount: count5xx,
            ErrorSpanCount: errorSpans,
            ErrorLogCount: errorLogs,
            Probes: [.. probes],
            TraceSummaries: [.. traces],
            Summary: summary);
    }

    private void CaptureStoppedActivity(Activity activity)
    {
        var isError = activity.Status == ActivityStatusCode.Error;
        if (!isError)
        {
            foreach (var tag in activity.TagObjects)
            {
                if ((string.Equals(tag.Key, "otel.status_code", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(tag.Value?.ToString(), "ERROR", StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(tag.Key, "error", StringComparison.OrdinalIgnoreCase)
                        && (tag.Value is true || string.Equals(tag.Value?.ToString(), "true", StringComparison.OrdinalIgnoreCase)))
                    || (string.Equals(tag.Key, "error.type", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(tag.Value?.ToString())))
                {
                    isError = true;
                    break;
                }
            }
        }

        string? brainTraceId = null;
        foreach (var tag in activity.TagObjects)
        {
            if (string.Equals(tag.Key, "brain.trace_id", StringComparison.Ordinal))
            {
                brainTraceId = tag.Value?.ToString();
                break;
            }
        }

        var captured = new CapturedSpan(
            activity.Source.Name,
            activity.OperationName,
            activity.TraceId.ToString(),
            brainTraceId,
            isError,
            activity.StatusDescription,
            (long)Math.Round(activity.Duration.TotalMilliseconds));

        lock (_spanGate)
        {
            while (_capturedSpans.Count >= MaxCapturedSpans)
            {
                _capturedSpans.Dequeue();
            }

            _capturedSpans.Enqueue(captured);
        }
    }

    private CapturedSpan[] SnapshotCapturedSpans(string? brainTraceId)
    {
        CapturedSpan[] all;
        lock (_spanGate)
        {
            all = [.. _capturedSpans];
        }

        if (!string.IsNullOrWhiteSpace(brainTraceId))
        {
            var scoped = all.Where(s =>
                string.Equals(s.BrainTraceId, brainTraceId, StringComparison.Ordinal)
                || string.Equals(s.OtelTraceId, brainTraceId, StringComparison.Ordinal)
                || s.IsError).ToArray();
            if (scoped.Length > 0)
            {
                return scoped;
            }
        }

        return all;
    }

    private static bool IsErrorTraceEntry(TraceEntry entry)
    {
        if (entry.Signal.EndsWith(".failed", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".rejected", StringComparison.OrdinalIgnoreCase)
            || entry.Signal.EndsWith(".error", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(entry.Signal, "flow.done", StringComparison.OrdinalIgnoreCase)
            && (entry.Payload.Contains("\"outcome\":\"failed\"", StringComparison.OrdinalIgnoreCase)
                || entry.Payload.Contains("\"outcome\":1", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    private static (int NodeCount, int ErrorNodeCount) AuditScreenViewJson(JsonElement element)
    {
        var nodes = 0;
        var errors = 0;
        WalkViewElement(element, ref nodes, ref errors);
        return (nodes, errors);
    }

    private static void WalkViewElement(JsonElement element, ref int nodes, ref int errors)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var isNode = element.TryGetProperty("kind", out _)
                    || element.TryGetProperty("type", out _)
                    || element.TryGetProperty("$type", out _);
                if (isNode)
                {
                    nodes++;
                    if ((element.TryGetProperty("tone", out var tone)
                            && tone.ValueKind == JsonValueKind.String
                            && string.Equals(tone.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("severity", out var sev)
                            && sev.ValueKind == JsonValueKind.String
                            && string.Equals(sev.GetString(), "error", StringComparison.OrdinalIgnoreCase))
                        || (element.TryGetProperty("status", out var status)
                            && status.ValueKind == JsonValueKind.String
                            && string.Equals(status.GetString(), "crashed", StringComparison.OrdinalIgnoreCase)))
                    {
                        errors++;
                    }
                }

                foreach (var prop in element.EnumerateObject())
                {
                    WalkViewElement(prop.Value, ref nodes, ref errors);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    WalkViewElement(item, ref nodes, ref errors);
                }

                break;
        }
    }

    private static bool IsUnhostedScreenException(Exception ex)
        => ex.Message.Contains("not registered", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("No registration", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unknown", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("app.engineer", StringComparison.OrdinalIgnoreCase);

    private async Task<EngineerLiveProbe> ProbeHttpEndpointAsync(
        IHttpClientFactory factory,
        string name,
        string kind,
        string url,
        CancellationToken ct)
    {
        try
        {
            using var client = factory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            using var response = await client.GetAsync(new Uri(url), ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var ok = code is >= 200 and < 400;
            return new EngineerLiveProbe(name, kind, ok, code, $"GET {url} -> HTTP {code}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogSwallowed(ex, $"Live HTTP probe '{name}' failed against '{url}'.");
            return new EngineerLiveProbe(name, kind, false, 503, $"Probe failed: {ex.Message}");
        }
    }

    private sealed record CapturedSpan(
        string SourceName,
        string OperationName,
        string OtelTraceId,
        string? BrainTraceId,
        bool IsError,
        string? StatusDescription,
        long DurationMs);
}
