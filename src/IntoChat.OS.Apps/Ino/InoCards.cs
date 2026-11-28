using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DigitalBrain.Flows;
using DigitalBrain.UI;
using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Apps.Flows;
using static IntoChat.OS.UI.Kit;

namespace IntoChat.OS.Apps.Ino;

// Extracts InoCards from tool call JSON results and inline live Slot(neuronUri) attachments from assistant prose.
internal static partial class InoCards
{
    public static InoCard[] FromToolCalls(IEnumerable<AgentToolCall> calls)
        => [.. calls.Select(c => c.Result is { Length: > 0 } r ? FromResult(r) : null).OfType<InoCard>()];

    public static (string CleanText, IReadOnlyList<NeuronRef> Slots) ExtractInlineSlots(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText) || !rawText.Contains("slot:", StringComparison.OrdinalIgnoreCase))
        {
            return (rawText ?? string.Empty, []);
        }

        var slots = new List<NeuronRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Capture(Match m)
        {
            if (ToLiveSlot(m.Groups["target"].Value) is { } s && seen.Add($"{s.Kind}:{s.NeuronUri}")) { slots.Add(s); }
            return string.Empty;
        }

        var cleaned = UriSlotRegex().Replace(BracketSlotRegex().Replace(rawText, Capture), Capture);
        return slots.Count == 0 ? (rawText, []) : (cleaned.Trim(), slots);
    }

    public static NeuronRef? ToLiveSlot(string? rawTarget, string? explicitKind = null)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) { return null; }
        var trimmed = rawTarget.Trim().TrimEnd('.', ',', ';', ')');
        if (trimmed.StartsWith("slot://", StringComparison.OrdinalIgnoreCase)) { trimmed = trimmed[7..]; }

        var refKind = explicitKind;
        foreach (var (prefix, kind) in (ReadOnlySpan<(string, string)>)[("app:", "app"), ("unit:", "unit"), ("view:", "view")])
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                refKind ??= kind;
                trimmed = trimmed[prefix.Length..];
                break;
            }
        }

        var uri = InoView.StripNeuronUri(trimmed).Trim('/');
        if (uri.IndexOf('#') is > 0 and var hash) { uri = uri[..hash]; }
        if (string.IsNullOrWhiteSpace(uri)) { return null; }

        var resolved = !string.IsNullOrWhiteSpace(refKind) ? refKind
            : uri.StartsWith("unit.", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("units/", StringComparison.OrdinalIgnoreCase) ? "unit"
            : !uri.Contains('/', StringComparison.Ordinal) && !uri.Contains('.', StringComparison.Ordinal) ? "app"
            : "view";
        if (resolved == "view" && uri.StartsWith("apps/", StringComparison.OrdinalIgnoreCase) && !uri.EndsWith("/view", StringComparison.OrdinalIgnoreCase))
        {
            uri = $"{uri}/view";
        }

        return Embed(uri, resolved);
    }

    internal static InoCard? FromResult(string result)
    {
        try
        {
            using var payload = JsonDocument.Parse(result);
            return payload.RootElement is { ValueKind: JsonValueKind.Object } root
                && root.TryGetProperty("card", out var card) && card.ValueKind == JsonValueKind.Object
                ? card.Deserialize(InoJson.Default.InoCard)
                : null;
        }
        catch (JsonException) { return null; }
    }

    internal static InoDrawerArtifact? FromCodeOrchestrationResult(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) { return null; }
        try
        {
            var view = JsonSerializer.Deserialize<CodeOrchestrationView>(resultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return view is { FlowId.Length: > 0 } ? FromCodeOrchestrationView(view) : null;
        }
        catch (JsonException) { return null; }
    }

    internal static InoDrawerArtifact FromCodeOrchestrationView(CodeOrchestrationView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var status = view.Status switch
        {
            FlowStatus.Completed => "done",
            FlowStatus.Failed or FlowStatus.Cancelled => "failed",
            _ => "running",
        };

        var diffSb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(view.Result?.Summary))
        {
            diffSb.AppendLine(view.Result.Summary);
        }

        if (view.Result?.Artifacts is { Length: > 0 } artifacts)
        {
            foreach (var item in artifacts)
            {
                diffSb.AppendLine();
                diffSb.Append(item.Name).Append(" (").Append(item.Kind).AppendLine(")");
                if (!string.IsNullOrWhiteSpace(item.Content))
                {
                    diffSb.AppendLine(item.Content);
                }
                else if (!string.IsNullOrWhiteSpace(item.Uri))
                {
                    diffSb.AppendLine(item.Uri);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(view.CompiledScript))
        {
            if (diffSb.Length > 0) { diffSb.AppendLine(); }
            diffSb.AppendLine(view.CompiledScript);
        }

        var logsSb = new StringBuilder();
        logsSb.Append("Status: ").AppendLine(view.Status.ToString());
        if (!string.IsNullOrWhiteSpace(view.ExecutionTarget))
        {
            logsSb.Append("Execution target: ").AppendLine(view.ExecutionTarget);
        }

        if (!string.IsNullOrWhiteSpace(view.ExecutedByMachineId))
        {
            logsSb.Append("Machine: ").AppendLine(view.ExecutedByMachineId);
        }

        if (view.ScopedContracts is { Length: > 0 })
        {
            logsSb.Append("Scoped contracts: ").AppendLine(string.Join(", ", view.ScopedContracts));
        }

        if (view.Result?.Metrics is { Length: > 0 } metrics)
        {
            foreach (var metric in metrics)
            {
                var unit = string.IsNullOrWhiteSpace(metric.Unit) ? "" : $" {metric.Unit}";
                logsSb.Append("Metric · ").Append(metric.Name).Append(": ").Append(metric.Value).AppendLine(unit);
            }
        }

        if (!string.IsNullOrWhiteSpace(view.Error))
        {
            logsSb.Append("Error: ").AppendLine(view.Error);
        }

        return new InoDrawerArtifact(
            Id: $"code-orch:{view.FlowId}",
            Title: string.IsNullOrWhiteSpace(view.Title) ? "Code Orchestration" : view.Title,
            Kind: "code-orchestration",
            Status: status,
            FlowId: view.FlowId,
            LiveViewPath: view.LiveViewPath,
            CompiledScript: view.CompiledScript,
            Summary: view.Result?.Summary ?? view.Error,
            DiffText: diffSb.Length > 0 ? diffSb.ToString().Trim() : null,
            LogsText: logsSb.Length > 0 ? logsSb.ToString().Trim() : null,
            ScopedContracts: view.ScopedContracts ?? [],
            PinnedAppAlias: view.PinnedAppAlias,
            ExecutionTarget: view.ExecutionTarget);
    }

    internal const int MaxDisplayedDiffChars = 64_000;

    internal static bool IsEngineerFlowId(string? flowId)
    {
        if (string.IsNullOrWhiteSpace(flowId))
        {
            return false;
        }

        var trimmed = flowId.Trim();
        if (DigitalBrain.NeuronId.TryParse(trimmed, out var parsed))
        {
            return string.Equals(parsed.Type, IEngineerRuns.EngineerFlow, StringComparison.Ordinal);
        }

        return trimmed.Contains($"/{IEngineerRuns.EngineerFlow}/", StringComparison.Ordinal);
    }

    internal static InoDrawerArtifact FromEngineerRunSummary(EngineerRunSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var status = summary.Status switch
        {
            FlowStatus.Completed => "done",
            FlowStatus.Failed or FlowStatus.Cancelled => "failed",
            FlowStatus.Waiting => "waiting",
            _ => "running",
        };

        var orderedCandidates = summary.Candidates is { Length: > 0 }
            ? summary.Candidates
                .OrderBy(static c => c.Rank)
                .ThenByDescending(static c => c.Score.TotalScore)
                .ToArray()
            : [];
        var orderedInstances = summary.Instances is { Length: > 0 }
            ? summary.Instances
                .OrderBy(static i => i.Rank)
                .ThenByDescending(static i => i.TotalScore)
                .ToArray()
            : [];

        var winnerCandidate = orderedCandidates.FirstOrDefault(static c => c.IsSelected)
            ?? orderedCandidates.FirstOrDefault();
        var winnerInstance = orderedInstances.FirstOrDefault(static i => i.IsSelected)
            ?? orderedInstances.FirstOrDefault();

        var candidateSha = FirstNonEmpty(
            summary.Receipt?.CommitSha,
            summary.WinningCommitSha,
            winnerCandidate?.Score.CommitSha,
            winnerInstance?.CommitSha);

        var (selfReviewPassed, selfReviewSummary) = ResolveSelfReview(summary, winnerCandidate, winnerInstance);

        var tournament = BuildTournamentCandidates(orderedCandidates, orderedInstances, selfReviewPassed, selfReviewSummary);

        var rawDiff = FirstNonEmpty(
            winnerCandidate?.Score.UnifiedDiff,
            winnerInstance?.UnifiedDiff);
        string? diffText = null;
        if (!string.IsNullOrWhiteSpace(rawDiff))
        {
            diffText = rawDiff.Length > MaxDisplayedDiffChars
                ? string.Concat(rawDiff[..MaxDisplayedDiffChars].TrimEnd(), "\n… (diff truncated for display)")
                : rawDiff.TrimEnd();
        }

        var logsText = BuildEngineerLogsText(summary, winnerCandidate, winnerInstance, candidateSha, selfReviewPassed, selfReviewSummary);

        var pushedRef = FirstNonEmpty(
            summary.Receipt?.PushedRef,
            summary.PushedRef);

        var headBranch = FirstNonEmpty(
            winnerCandidate?.BranchName,
            winnerInstance?.BranchName,
            StripRefsHeads(pushedRef));

        var pullRequestUrl = NormalizeHttpsPullRequestUrlOrNull(FirstNonEmpty(
            summary.Receipt?.PullRequestUrl,
            summary.PullRequestUrl));

        var title = !string.IsNullOrWhiteSpace(summary.Goal)
            ? (summary.Goal.Trim().Length <= 72 ? summary.Goal.Trim() : summary.Goal.Trim()[..71] + "…")
            : $"Engineer Run · {summary.RunId}";

        var flowKey = !string.IsNullOrWhiteSpace(summary.FlowId) ? summary.FlowId.Trim() : summary.RunId;
        var hasReceipt = summary.Receipt is not null;
        var buildVerified = summary.Receipt?.BuildVerified == true;
        var liveVerified = summary.Receipt?.LiveVerified == true;
        var confirmationPlanId = string.IsNullOrWhiteSpace(summary.ConfirmationPlanId) ? null : summary.ConfirmationPlanId.Trim();

        return new InoDrawerArtifact(
            Id: $"engineer-pr:{flowKey}",
            Title: title,
            Kind: InoDrawerArtifact.EngineerPrKind,
            Status: status,
            FlowId: flowKey,
            Summary: string.IsNullOrWhiteSpace(summary.SummaryText) ? summary.Goal : summary.SummaryText,
            DiffText: diffText,
            LogsText: logsText,
            ExecutionTarget: summary.Backend,
            CandidateSha: candidateSha,
            RepoSlug: string.IsNullOrWhiteSpace(summary.RepoSlug) ? "os" : summary.RepoSlug.Trim(),
            HeadBranch: headBranch,
            BaseBranch: null,
            PullRequestUrl: pullRequestUrl,
            SelfReviewPassed: selfReviewPassed,
            SelfReviewSummary: selfReviewSummary,
            VerificationSummary: summary.Receipt?.VerificationSummary,
            TournamentCandidates: tournament,
            BuildVerified: buildVerified,
            LiveVerified: liveVerified,
            HasVerificationReceipt: hasReceipt,
            ConfirmationPlanId: confirmationPlanId,
            PushedRef: pushedRef);
    }

    private static (bool Passed, string? Summary) ResolveSelfReview(
        EngineerRunSummary summary,
        EngineerCandidateResult? winnerCandidate,
        EngineerInstanceSummary? winnerInstance)
    {
        var report = summary.SelfReview ?? winnerCandidate?.SelfReview ?? winnerInstance?.SelfReview;
        if (summary.Receipt is { } receipt)
        {
            var passed = receipt.SelfReviewPassed && (report is null || !report.HasBlocking);
            var text = FirstNonEmpty(receipt.SelfReviewSummary, report?.Summary);
            return (passed, text ?? (passed ? "passed" : "Self-review gate failed."));
        }

        // Fail closed: without an authoritative EngineerVerificationReceipt, self-review is never considered passed.
        return (false, FirstNonEmpty(report?.Summary, "Self-review receipt unavailable."));
    }

    private static InoEngineerCandidate[] BuildTournamentCandidates(
        EngineerCandidateResult[] candidates,
        EngineerInstanceSummary[] instances,
        bool runSelfReviewPassed,
        string? runSelfReviewSummary)
    {
        if (candidates.Length > 0)
        {
            var rows = new InoEngineerCandidate[candidates.Length];
            for (var i = 0; i < candidates.Length; i++)
            {
                var c = candidates[i];
                var srPassed = c.SelfReview is not null
                    ? !c.SelfReview.HasBlocking
                    : (c.IsSelected && runSelfReviewPassed);
                var srSummary = c.SelfReview?.Summary ?? (c.IsSelected ? runSelfReviewSummary : null);
                rows[i] = new InoEngineerCandidate(
                    InstanceIndex: c.InstanceIndex,
                    Rank: c.Rank,
                    StrategyLabel: string.IsNullOrWhiteSpace(c.StrategyLabel) ? EngineerCandidateStrategies.LabelFor(c.Strategy, c.InstanceIndex) : c.StrategyLabel,
                    Status: c.Status,
                    TotalScore: c.Score.TotalScore,
                    ChangedLines: c.Score.ChangedLines,
                    IsSelected: c.IsSelected,
                    BuildPassed: c.Score.BuildPassed,
                    TestsPassed: c.Score.TestsPassed,
                    LiveVerifyPassed: c.Score.LiveVerifyPassed,
                    ZeroTelemetryErrors: c.Score.ZeroTelemetryErrors,
                    SelfReviewPassed: srPassed,
                    SelfReviewSummary: srSummary,
                    CommitSha: c.Score.CommitSha,
                    BranchName: c.BranchName,
                    FailureDetail: c.FailureDetail);
            }

            return rows;
        }

        if (instances.Length > 0)
        {
            var rows = new InoEngineerCandidate[instances.Length];
            for (var i = 0; i < instances.Length; i++)
            {
                var inst = instances[i];
                var srPassed = inst.SelfReview is not null
                    ? !inst.SelfReview.HasBlocking
                    : (inst.IsSelected && runSelfReviewPassed);
                var srSummary = inst.SelfReview?.Summary ?? (inst.IsSelected ? runSelfReviewSummary : null);
                rows[i] = new InoEngineerCandidate(
                    InstanceIndex: inst.InstanceIndex,
                    Rank: inst.Rank,
                    StrategyLabel: EngineerCandidateStrategies.LabelFor(inst.Strategy, inst.InstanceIndex),
                    Status: inst.Status,
                    TotalScore: inst.TotalScore,
                    ChangedLines: inst.ChangedLines,
                    IsSelected: inst.IsSelected,
                    BuildPassed: inst.BuildSucceeded,
                    TestsPassed: inst.BuildSucceeded,
                    LiveVerifyPassed: inst.LiveSucceeded,
                    ZeroTelemetryErrors: inst.LiveSucceeded,
                    SelfReviewPassed: srPassed,
                    SelfReviewSummary: srSummary,
                    CommitSha: inst.CommitSha,
                    BranchName: inst.BranchName,
                    FailureDetail: inst.FailureDetail);
            }

            return rows;
        }

        return [];
    }

    private static string BuildEngineerLogsText(
        EngineerRunSummary summary,
        EngineerCandidateResult? winnerCandidate,
        EngineerInstanceSummary? winnerInstance,
        string? candidateSha,
        bool selfReviewPassed,
        string? selfReviewSummary)
    {
        var sb = new StringBuilder();
        sb.Append("Flow: ").AppendLine(summary.FlowId);
        sb.Append("Repository: ").AppendLine(summary.RepoSlug);
        sb.Append("Stage: ").Append(summary.CurrentStage).Append(" (").Append(summary.Status).AppendLine(")");
        if (!string.IsNullOrWhiteSpace(candidateSha))
        {
            sb.Append("Commit SHA: ").AppendLine(candidateSha);
        }

        sb.Append("Self-Review: ").Append(selfReviewPassed ? "PASS" : "FAIL");
        if (!string.IsNullOrWhiteSpace(selfReviewSummary))
        {
            sb.Append(" — ").Append(selfReviewSummary);
        }

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summary.Receipt?.VerificationSummary))
        {
            sb.Append("Verification: ").AppendLine(summary.Receipt.VerificationSummary);
        }

        if (winnerCandidate?.Build is { } build)
        {
            sb.AppendLine();
            sb.AppendLine("── Roslyn Build & TRX Tests ──");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Dotnet Build: {(build.DotnetBuildPassed ? "PASS" : "FAIL")} | Flutter Analyze: {(build.FlutterAnalyzePassed ? "PASS" : "FAIL")} | Tests: {(build.DotnetTestPassed ? "PASS" : "FAIL")} ({build.PassedTests} passed, {build.FailedTests} failed)");
            sb.AppendLine();
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Warnings: {build.WarningCount} | Errors: {build.ErrorCount} | Duration: {build.DurationMs} ms");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(build.Summary))
            {
                sb.AppendLine(build.Summary);
            }

            if (build.Diagnostics is { Length: > 0 })
            {
                foreach (var diag in build.Diagnostics.Take(25))
                {
                    sb.Append("  • ").AppendLine(diag);
                }
            }
        }

        if (winnerCandidate?.Live is { } live)
        {
            sb.AppendLine();
            sb.AppendLine("── Live Verify (MCP / Playwright / OTel) ──");
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"Host Healthy: {(live.HostHealthy ? "PASS" : "FAIL")} | MCP Probes: {(live.McpProbesPassed ? "PASS" : "FAIL")} | Playwright: {(live.PlaywrightPassed ? "PASS" : "FAIL")}");
            sb.AppendLine();
            sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"OTel Errors: HTTP 4xx={live.Http4xxCount}, HTTP 5xx={live.Http5xxCount}, Error Spans={live.ErrorSpanCount}, Error Logs={live.ErrorLogCount}");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(live.Summary))
            {
                sb.AppendLine(live.Summary);
            }

            if (live.Probes is { Length: > 0 })
            {
                foreach (var probe in live.Probes)
                {
                    var httpPart = probe.HttpStatus is { } code ? $" [HTTP {code}]" : string.Empty;
                    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"  • [{(probe.Passed ? "PASS" : "FAIL")}] {probe.Kind}/{probe.Name}{httpPart}: {probe.Detail}");
                    sb.AppendLine();
                }
            }

            if (live.TraceSummaries is { Length: > 0 })
            {
                sb.AppendLine("OTel Spans:");
                foreach (var trace in live.TraceSummaries.Take(25))
                {
                    sb.Append("  • ").AppendLine(trace);
                }
            }
        }

        var selfReviewReport = summary.SelfReview ?? winnerCandidate?.SelfReview ?? winnerInstance?.SelfReview;
        if (selfReviewReport is { Findings.Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("── Local Self-Review Findings ──");
            sb.AppendLine(selfReviewReport.FormatForCard());
        }

        if (summary.DiagnosticsAndTraces is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("── Diagnostics & OTel Audit Log ──");
            foreach (var entry in summary.DiagnosticsAndTraces.Take(40))
            {
                sb.Append("  • ").AppendLine(entry);
            }
        }

        return sb.ToString().Trim();
    }

    internal static bool TryNormalizeHttpsPullRequestUrl(string? rawUrl, out string normalizedUrl)
    {
        normalizedUrl = string.Empty;
        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            return false;
        }

        // Reject any control characters (CR, LF, TAB, NUL, etc.) anywhere in the raw input.
        foreach (var ch in rawUrl)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        var trimmed = rawUrl.Trim();
        foreach (var ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(uri.Host)
            || uri.HostNameType == UriHostNameType.Unknown
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        normalizedUrl = uri.AbsoluteUri;
        return true;
    }

    internal static string? NormalizeHttpsPullRequestUrlOrNull(string? rawUrl)
        => TryNormalizeHttpsPullRequestUrl(rawUrl, out var normalized) ? normalized : null;

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var value in candidates)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? StripRefsHeads(string? gitRef)
    {
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return null;
        }

        var trimmed = gitRef.Trim();
        const string prefix = "refs/heads/";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    [GeneratedRegex(@"\[slot:(?<target>[^\]\s]+)\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketSlotRegex();

    [GeneratedRegex(@"(?<!\w)slot://(?<target>[^\s\]\)"">]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UriSlotRegex();
}
