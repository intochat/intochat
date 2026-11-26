using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Flows;
using DigitalBrain.Units;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

internal sealed partial class InoStudioNeuron
{
    public Task<InoToolResult> ProposeChange(string repository, string goal, CancellationToken ct = default)
        => StartEngineerRunAsync(repository, goal, baseBranch: null, ct);

    public Task<InoToolResult> ProposeChangeOnBranch(string repository, string goal, string baseBranch, CancellationToken ct = default)
        => StartEngineerRunAsync(repository, goal, baseBranch, ct);

    public async Task<InoToolResult> ChangeStatus(string flowId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        var brainId = CurrentBrainId;
        var trimmed = flowId.Trim();
        if (trimmed.Contains($"/{IEngineerRuns.EngineerFlow}/", StringComparison.Ordinal))
        {
            var engSummary = await Get<IEngineerRuns>(IEngineerRuns.Key(brainId)).InspectEngineerRun(trimmed, ct).ConfigureAwait(true);
            return new InoToolResult(engSummary.SummaryText, engSummary.Card, Ok: engSummary.Ok);
        }

        if (!trimmed.Contains('/', StringComparison.Ordinal))
        {
            var candidateEngId = new NeuronId(Id.Brain, IEngineerRuns.EngineerFlow, trimmed).ToString();
            var existingEng = await Get<IFlows>(IFlows.Key(brainId)).Read(candidateEngId, ct).ConfigureAwait(true);
            if (existingEng is not null)
            {
                var engSummary = await Get<IEngineerRuns>(IEngineerRuns.Key(brainId)).InspectEngineerRun(candidateEngId, ct).ConfigureAwait(true);
                return new InoToolResult(engSummary.SummaryText, engSummary.Card, Ok: engSummary.Ok);
            }
        }

        var fullId = NormalizeFlowId(brainId, trimmed);
        return await FormatChangeStatusAsync(brainId, fullId, ct).ConfigureAwait(true);
    }

    public Task<InoToolResult> Repositories(CancellationToken ct = default)
    {
        _ = CurrentBrainId;
        var wt = services.GetRequiredService<IWorkTree>();
        var repos = wt.ListRepositories();
        if (repos.Count == 0)
        {
            return Task.FromResult(new InoToolResult(
                "No repositories are registered in ~/.intochat/repos.json yet. Add an entry such as {\"os\": \"E:\\\\path\\\\to\\\\repo\"} to enable Feature-to-PR."));
        }

        var lines = repos.Select(r =>
            $"- {r.Id}: {r.LocalPath}"
            + (string.IsNullOrWhiteSpace(r.GitHubRepo) ? "" : $" (GitHub: {r.GitHubRepo})")
            + (string.IsNullOrWhiteSpace(r.DefaultBaseBranch) ? "" : $" [base: {r.DefaultBaseBranch}]"));
        return Task.FromResult(new InoToolResult("Allowlisted repositories:\n" + string.Join("\n", lines)));
    }

    // A change Ino proposes is an engineering run (flow.engineer) with every verification gate on: plan, one worktree, build and
    // test, live verification, then the person's approval of the exact commit. The call goes through the engineering door with
    // this call's stamped caller, so the run is recorded for the conversation and the person that asked. Ino starts no
    // flow.change: that flow type only finishes runs already in flight (ChangeStatus still reads them).
    private async Task<InoToolResult> StartEngineerRunAsync(
        string repository,
        string goal,
        string? baseBranch,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);

        var brainId = CurrentBrainId;
        var cleanRepo = repository.Trim();
        var cleanGoal = goal.Trim();
        var runKey = Guid.NewGuid().ToString("N")[..12];

        // Fail fast with clear guidance if repository is not in the allowlist
        var wt = services.GetRequiredService<IWorkTree>();
        RepositoryId parsedRepo;
        BranchName? parsedBase;
        try
        {
            parsedRepo = RepositoryId.Parse(cleanRepo);
            parsedBase = string.IsNullOrWhiteSpace(baseBranch) ? null : BranchName.FromRequestedBase(baseBranch);
            _ = wt.ResolveRepository(parsedRepo);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OsConsoleLog.For<InoStudioNeuron>().LogSwallowed(ex, $"resolving repository '{cleanRepo}' for a proposed change");
            return new InoToolResult($"Could not start the change on '{cleanRepo}': {ex.Message}", Ok: false);
        }

        // StartEngineerRun answers with the run's next settled stage, or with why it did not start; it does not throw for either.
        var summary = await Get<IEngineerRuns>(IEngineerRuns.Key(brainId))
            .StartEngineerRun(
                new EngineerDelegationRequest(
                    RepoSlug: parsedRepo.Value,
                    Goal: cleanGoal,
                    ParallelInstances: 1,
                    RequestedBaseBranch: parsedBase?.Value,
                    RunId: runKey,
                    Verification: EngineerVerificationPolicy.Full),
                ct)
            .ConfigureAwait(true);
        return new InoToolResult(summary.SummaryText, summary.Card, Ok: summary.Ok);
    }

    private async Task<InoToolResult> FormatChangeStatusAsync(string brainId, string flowId, CancellationToken ct)
    {
        if (!NeuronId.TryParse(flowId, out var neuronId) || neuronId.Brain.Value != brainId)
        {
            return new InoToolResult($"'{flowId}' is not a valid flow in brain '{brainId}'.", Ok: false);
        }

        FlowHead head;
        FlowView? view;
        try
        {
            head = await Get<IFlowStarter>(neuronId).Head().ConfigureAwait(true);
            view = await Get<IFlows>(IFlows.Key(brainId)).Read(flowId, ct).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OsConsoleLog.For<InoStudioNeuron>().LogSwallowed(ex, $"reading Feature-to-PR flow '{flowId}'");
            return new InoToolResult($"Could not read Feature-to-PR flow '{flowId}': {ex.Message}", Ok: false);
        }

        // Only a flow.change run started before that flow type was retired is read here.
        var change = JsonSerializer.Deserialize<Change>(head.InnerJson, FeatureToPrFlow.Json) ?? new Change();
        var status = view?.Status ?? head.Status;
        var tasksUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(brainId), "tasks").Key}#{flowId}";

        if (status == FlowStatus.Waiting && change.Approval is { PlanId.Length: > 0 } approval && change.Commit is { } commit)
        {
            var shortSha = commit.Commit.Short;
            var branch = change.Tree?.BranchName ?? "ino/change";
            // The model gets the facts of the change, not the diff: the whole change is on the approval the person answers.
            var summary = new StringBuilder();
            summary.Append($"Feature-to-PR waits for the person's approval ({flowId}).\n");
            summary.Append(ChangeCard.Header(change)).Append('\n');
            summary.Append($"Approval plan: `{approval.PlanId}`. Only the person can approve it, below the conversation, where the whole change is shown.");
            if (!string.IsNullOrWhiteSpace(change.Edit?.Summary))
            {
                summary.Append($"\nSummary: {change.Edit.Summary.Trim()}");
            }

            // A status, not a place to approve: the one Approve for this plan is the inbox approval Ino shows below the conversation.
            var card = new InoCard(
                Id: approval.PlanId,
                Kind: "approval",
                Title: $"Waiting for your approval: {head.Title} ({shortSha})",
                Summary: $"{commit.ChangedFiles.Length} file(s) on {branch} (+{commit.Additions} / -{commit.Deletions}). The whole change and Approve are below the conversation.",
                SourceNeuronUri: $"neuron://{brainId}/runtime.confirmations/confirmations",
                Status: "pending",
                SurfaceScopeUri: tasksUri);
            return new InoToolResult(summary.ToString(), card, Ok: true);
        }

        if (status == FlowStatus.Completed)
        {
            var prUrl = change.Publication?.PullRequestUrl;
            var pushedRef = change.Publication?.PushedRef;
            var prLine = !string.IsNullOrWhiteSpace(prUrl)
                ? $"Draft PR: {prUrl}"
                : $"Pushed ref: {pushedRef}";
            var card = new InoCard(
                Id: flowId,
                Kind: "flow",
                Title: $"Published: {head.Title}",
                Summary: prLine,
                SourceNeuronUri: tasksUri,
                Status: "completed",
                SurfaceScopeUri: prUrl ?? tasksUri);
            return new InoToolResult(
                $"Feature-to-PR completed ({flowId}). Commit `{change.Commit?.Commit.Value}` pushed to `{pushedRef}`. {prLine}",
                card,
                Ok: true);
        }

        if (status is FlowStatus.Failed or FlowStatus.Cancelled)
        {
            var detail = view?.Detail ?? status.ToString();
            return new InoToolResult($"Feature-to-PR {status.ToString().ToLowerInvariant()} ({flowId}): {detail}", Ok: false);
        }

        var stepName = view?.Current ?? (head.Done.Length < head.Steps.Length ? head.Steps[head.Done.Length] : "running");
        var runningCard = new InoCard(
            Id: flowId,
            Kind: "flow",
            Title: $"Coding: {head.Title}",
            Summary: $"Step: {stepName}",
            SourceNeuronUri: tasksUri,
            Status: "running",
            SurfaceScopeUri: tasksUri);
        return new InoToolResult(
            $"Feature-to-PR '{head.Title}' is running ({flowId}, current step: {stepName}). Call ChangeStatus(\"{flowId}\") to check progress.",
            runningCard,
            Ok: true);
    }

    private static string NormalizeFlowId(string brainId, string flowId)
    {
        if (flowId.Contains('/', StringComparison.Ordinal))
        {
            return flowId;
        }
        var brain = brainId is ['@', .. var sys] ? DigitalBrain.BrainId.System(sys) : new DigitalBrain.BrainId(brainId);
        return new DigitalBrain.NeuronId(brain, FeatureToPrFlow.Alias, flowId).ToString();
    }

    private static string Kept(Dictionary<string, string> sidecars, string file) => sidecars.ContainsKey(file) ? "kept" : "missing";

    private static bool IsMissingModelFailure(string detail) =>
        detail.Contains("no drafter", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("no model", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("no creator model", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("provider", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("ollama", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("api key", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("settings", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
        || detail.Contains("configured", StringComparison.OrdinalIgnoreCase);
}
