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
        var targetFlowId = trimmed.Contains('/', StringComparison.Ordinal)
            ? trimmed
            : new NeuronId(Id.Brain, IEngineerRuns.EngineerFlow, trimmed).ToString();

        var engSummary = await Get<IEngineerRuns>(IEngineerRuns.Key(brainId)).InspectEngineerRun(targetFlowId, ct).ConfigureAwait(true);
        return new InoToolResult(engSummary.SummaryText, engSummary.Card, Ok: engSummary.Ok);
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
