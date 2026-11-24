using System.Globalization;
using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using DigitalBrain.Flows;
using IntoChat.OS.Modules.Microsoft.GitHub;

namespace IntoChat.OS.Modules.Apps.Flows;

/// <summary>
/// Durable PR review pipeline (<c>flow.pr-review</c>): reads review evidence through <see cref="IRepository"/>,
/// runs deterministic machine checks (<see cref="LocalSelfReview"/>) first, drops any uncited model findings,
/// gates submission behind human approval, and posts a <see cref="ReviewVerdict.Comment"/> review on the exact head SHA.
/// </summary>
[DigitalBrain.Neurons.Neuron(Alias)]
internal sealed partial class PrReviewFlow : Flow<PrReviewState>, IFlowStarter
{
    // The syntax review is stateless, so the flow reaches a per-brain Roslyn neuron that never opens a workspace.
    [DigitalBrain.Slot(Key = SyntaxReviewKey)]
    private partial IntoChat.OS.Modules.Microsoft.Roslyn.IRoslyn Roslyn { get; }

    internal const string SyntaxReviewKey = "syntax-review";

    public const string Alias = IEngineerRuns.PrReviewFlow;
    public const string ConfirmationKind = "review.pr.submit";
    private const string PlanPrefix = "app:pr-review";

    public static readonly TimeSpan ApprovalWindow = EngineerPipelineStages.DefaultApprovalWindow;

    public static readonly string[] Names =
    [
        "read-and-check",
        "approval-gate",
        "submit",
    ];

    internal static readonly JsonSerializerOptions Json = EngineerPipelineStages.Json;

    protected override IReadOnlyList<string> Steps => Names;

    public Task StartFromJson(string title, string owner, string? parentFlowId, string innerJson)
    {
        var initial = JsonSerializer.Deserialize<PrReviewState>(innerJson, Json)
            ?? throw new ArgumentException("PrReviewState JSON could not be deserialized.", nameof(innerJson));
        return Start(title, owner, parentFlowId, initial);
    }

    public Task<FlowHead> Head() => Task.FromResult(EngineerPipelineStages.BuildHead(
        Names,
        State.Owner,
        State.TraceId,
        State.ParentFlowId,
        State.Started,
        State.Inner,
        State.Title,
        State.Status,
        State.Done,
        State.Ended));

    protected override async Task<StepResult<PrReviewState>> Run(string step, PrReviewState inner, CancellationToken ct)
    {
        try
        {
            return step switch
            {
                "read-and-check" => await ReadAndCheckStepAsync(inner, ct).ConfigureAwait(true),
                "approval-gate" => ApprovalGateStep(step, inner),
                "submit" => await SubmitStepAsync(inner, ct).ConfigureAwait(true),
                _ => throw new FlowFailed(step, $"Unknown PrReviewFlow step '{step}'."),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !Context.Interrupts(ex))
        {
            if (ex is FlowFailed)
            {
                throw;
            }

            throw new FlowFailed(step, ex.Message);
        }
    }

    protected override ConfirmationPlan PlanFor(string step, PrReviewState inner)
    {
        var planId = !string.IsNullOrWhiteSpace(inner.ConfirmationPlanId)
            ? inner.ConfirmationPlanId!
            : BuildPlanId(inner.Repository, inner.Number, inner.HeadSha);
        var shortSha = inner.HeadSha.Length > 8 ? inner.HeadSha[..8] : inner.HeadSha;

        return new ConfirmationPlan(
            Id: planId,
            Action: ConfirmationAction.Other,
            Destructive: false,
            Title: $"Submit PR #{inner.Number} review on {inner.Repository} ({shortSha})?",
            Body: FormatReviewBody(inner.Findings),
            ApproveLabel: "Submit Review",
            DenyLabel: "Dismiss Review",
            TargetLabel: $"{inner.Repository}#{inner.Number}@{shortSha}")
        {
            RequiresPerson = true,
            Expiry = ApprovalWindow,
            Kind = ConfirmationKind,
            Subject = inner.HeadSha,
        };
    }

    public static string BuildPlanId(string repository, int number, string headSha)
    {
        var runKey = string.Create(CultureInfo.InvariantCulture, $"{repository}#{number}");
        return EngineerPipelineStages.BuildPlanId(PlanPrefix, runKey, headSha);
    }

    public static ReviewFinding[] FilterModelFindings(IEnumerable<ReviewFinding>? modelFindings)
    {
        if (modelFindings is null)
        {
            return [];
        }

        return
        [
            .. modelFindings.Where(static f => !string.IsNullOrWhiteSpace(f.RuleId)),
        ];
    }

    public static string FormatReviewBody(IReadOnlyList<ReviewFinding> findings)
    {
        if (findings is null || findings.Count == 0)
        {
            return "Automated PR review completed with no findings.";
        }

        var sb = new StringBuilder();
        sb.AppendLine("## Automated PR Review");
        foreach (var finding in findings)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"- [{finding.Severity}] `{finding.RuleId}` (`{finding.Path}:{finding.Line}`): {finding.Message}"));
        }

        return sb.ToString().Trim();
    }

    private async Task<StepResult<PrReviewState>> ReadAndCheckStepAsync(PrReviewState inner, CancellationToken ct)
    {
        if (inner.Number < 1)
        {
            throw new FlowFailed("read-and-check", "Pull request number must be >= 1.");
        }

        var repo = ResolveRepository(inner);
        var prRead = await repo.ReadPullRequest(new ReadPullRequest(inner.Number), ct).ConfigureAwait(true);
        var evidenceRead = await repo.ReadReviewEvidence(new ReadReviewEvidence(prRead.Snapshot), ct).ConfigureAwait(true);
        if (evidenceRead.Evidence is null)
        {
            throw new FlowFailed("read-and-check", evidenceRead.Detail ?? "Review evidence could not be retrieved.");
        }

        var headSha = string.IsNullOrWhiteSpace(inner.HeadSha)
            ? evidenceRead.Evidence.HeadSha
            : inner.HeadSha;

        var machineReport = await LocalSelfReview.ReviewUnifiedDiff(Roslyn, evidenceRead.Evidence.Text, ct).ConfigureAwait(true);
        var citedModelFindings = FilterModelFindings(inner.ModelFindings);
        ReviewFinding[] combined = [.. machineReport.Findings, .. citedModelFindings];

        var updated = inner with
        {
            HeadSha = headSha,
            Findings = combined,
            Verdict = PrReviewVerdict.Comment,
            AwaitingApproval = false,
        };

        return updated.Advance(new FlowSubject(Id.ToString(), $"{inner.Repository}#{inner.Number}"));
    }

    private StepResult<PrReviewState> ApprovalGateStep(string step, PrReviewState inner)
    {
        if (string.IsNullOrWhiteSpace(inner.HeadSha))
        {
            throw new FlowFailed(step, "Cannot enter approval-gate without a PR HeadSha.");
        }

        var planId = BuildPlanId(inner.Repository, inner.Number, inner.HeadSha);
        var expires = Context.Time.GetUtcNow().Add(ApprovalWindow);
        var next = inner with
        {
            Verdict = PrReviewVerdict.Comment,
            AwaitingApproval = true,
            ConfirmationPlanId = planId,
        };

        return next.WaitApproval(step, expires);
    }

    private async Task<StepResult<PrReviewState>> SubmitStepAsync(PrReviewState inner, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(inner.SubmittedReviewId))
        {
            return (inner with { AwaitingApproval = false }).Advance();
        }

        var repo = ResolveRepository(inner);
        var findings = inner.Findings ?? [];
        var body = FormatReviewBody(findings);
        var inlineComments = findings
            .Where(static f => !string.IsNullOrWhiteSpace(f.Path) && f.Line > 0)
            .Select(static f => new InlineComment(f.Path, f.Line, $"[{f.RuleId}] {f.Message}"))
            .ToArray();

        var verdict = ToGitHubVerdict(inner.Verdict);
        var submitted = await repo.SubmitReview(
            new SubmitReviewRequest(inner.Number, inner.HeadSha, verdict, body, inlineComments),
            ct).ConfigureAwait(true);

        var updated = inner with
        {
            SubmittedReviewId = submitted.ReviewId,
            AwaitingApproval = false,
        };

        return updated.Advance();
    }

    private IRepository ResolveRepository(PrReviewState inner)
    {
        var key = string.IsNullOrWhiteSpace(inner.RepositoryNeuronKey)
            ? "connection/github/main"
            : inner.RepositoryNeuronKey;
        return Context.Get<IRepository>(new NeuronId(Id.Brain, "github", key));
    }

    private static ReviewVerdict ToGitHubVerdict(PrReviewVerdict verdict) => verdict switch
    {
        PrReviewVerdict.RequestChanges => ReviewVerdict.RequestChanges,
        PrReviewVerdict.Approve => ReviewVerdict.Approve,
        _ => ReviewVerdict.Comment,
    };
}
