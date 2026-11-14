using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Flows;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("ino.studio")]
public sealed class InoStudioNeuron(IServiceProvider services) : DigitalBrain.Neurons.Neuron, IInoStudio
{
    private static readonly TimeSpan ProposePollBudget = TimeSpan.FromSeconds(18);
    private static readonly TimeSpan ProposePollInterval = TimeSpan.FromMilliseconds(250);

    private string CurrentBrainId
    {
        get
        {
            var brain = Id.Brain.Value;
            if (string.IsNullOrWhiteSpace(brain) || !BrainScope.IsValidId(brain))
            {
                throw new InvalidOperationException($"Invalid brain id '{brain}'.");
            }
            return brain;
        }
    }

    // Starts the five-step unit flow (author, draft, verify, deploy, install); the flow's author step names the alias. The card points at
    // the flow on the Tasks board, where the verify transcript and the consent to the unit's needs appear.
    public async Task<InoToolResult> Make(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var text = goal.Trim();
        var title = text.Length <= 52 ? text : text[..52];
        var brainId = CurrentBrainId;

        string flowId;
        try
        {
            var inner = JsonSerializer.Serialize(new BuildInner { Goal = text }, BuildFlow.Json);
            flowId = await Get<IFlows>(IFlows.Key(brainId)).Start(BuildFlow.Alias, title, null, inner);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start '{title}': {error.Message}");
        }

        var tasks = $"neuron://{brainId}/apps/tasks#{flowId}";
        var card = new InoCard(
            flowId,
            "flow",
            $"Building: {title}",
            "The unit is authored, verified against its scenarios and deployed; it then waits for your consent to its needs.",
            tasks,
            "running",
            tasks);
        return new InoToolResult(
            $"Started building {title} ({flowId}). The verify transcript and the consent to the unit's needs will appear on the Tasks board ({tasks}).", card);
    }

    public async Task<InoToolResult> Launch(string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var app = appId.Trim();
        _ = CurrentBrainId;

        try
        {
            await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Connections.AppGrainType.Name, $"apps/{app}")).Request("open", "");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not open {app}: {error.Message}");
        }

        return new InoToolResult($"Opened {app}.");
    }

    public async Task<InoToolResult> List()
    {
        var brainId = CurrentBrainId;
        var studios = (await ShippedManifests.Of(services)).Studios();
        var text = "Studios:\n" + string.Join("\n", studios.Select(s => $"- {s.App}: {s.Description}"));
        var studio = Get<ISynapseStudio>(ISynapseStudio.Key(brainId));
        DigitalBrain.Units.DeployedSource[] units;
        try { units = await studio.List(); }
        catch (Exception error) when (error is not OperationCanceledException) { return new InoToolResult(text + $"\nUnits are not available: {error.Message}"); }
        if (units.Length == 0) { return new InoToolResult(text + "\nUnits: none deployed."); }
        var lines = new List<string>();
        foreach (var unit in units)
        {
            var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
            Dictionary<string, string> sidecars;
            try { sidecars = await studio.Sidecars(alias); }
            catch (Exception error) when (error is not OperationCanceledException) { lines.Add($"- {alias} ({unit.Status}): sidecars unavailable"); continue; }
            lines.Add($"- {alias} ({unit.Status}): spec.md {Kept(sidecars, "spec.md")}, scenarios.json {Kept(sidecars, "scenarios.json")}");
        }
        return new InoToolResult(text + "\nUnits:\n" + string.Join("\n", lines));
    }

    public Task<InoToolResult> ProposeChange(string repository, string goal, CancellationToken ct = default)
        => StartFeatureToPrAsync(repository, goal, baseBranch: null, ct);

    public Task<InoToolResult> ProposeChangeOnBranch(string repository, string goal, string baseBranch, CancellationToken ct = default)
        => StartFeatureToPrAsync(repository, goal, baseBranch, ct);

    public async Task<InoToolResult> ChangeStatus(string flowId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(flowId);
        var brainId = CurrentBrainId;
        var fullId = NormalizeFlowId(brainId, flowId.Trim());
        return await FormatChangeStatusAsync(brainId, fullId, ct).ConfigureAwait(true);
    }

    public Task<InoToolResult> Repositories(CancellationToken ct = default)
    {
        _ = CurrentBrainId;
        var wt = services.GetService<IWorkTree>() ?? new WorkTreeService();
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

    private async Task<InoToolResult> StartFeatureToPrAsync(
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
        var title = cleanGoal.Length <= 56 ? cleanGoal : cleanGoal[..56];
        var runKey = Guid.NewGuid().ToString("N")[..12];

        // Fail fast with clear guidance if repository is not in the allowlist
        var wt = services.GetService<IWorkTree>() ?? new WorkTreeService();
        try
        {
            _ = (wt as WorkTreeService)?.ResolveRepository(cleanRepo)
                ?? wt.ListRepositories().FirstOrDefault(r => string.Equals(r.Id, cleanRepo, StringComparison.OrdinalIgnoreCase))
                ?? throw new UnauthorizedAccessException($"Repository '{cleanRepo}' is not registered in the allowlist.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start Feature-to-PR on '{cleanRepo}': {ex.Message}", Ok: false);
        }

        string flowId;
        try
        {
            var innerJson = JsonSerializer.Serialize(new FeatureToPrInner
            {
                RunId = runKey,
                Repository = cleanRepo,
                Goal = cleanGoal,
                RequestedBaseBranch = string.IsNullOrWhiteSpace(baseBranch) ? null : baseBranch.Trim(),
            }, FeatureToPrFlow.Json);

            flowId = await Get<IFlows>(IFlows.Key(brainId))
                .Start(FeatureToPrFlow.Alias, title, null, innerJson, ct, flowId: runKey)
                .ConfigureAwait(true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start Feature-to-PR '{title}': {error.Message}", Ok: false);
        }

        // Poll briefly (within 18s grain-safe budget) so fast local runs immediately return the SHA-bound confirmation card & diff!
        var deadline = DateTimeOffset.UtcNow + ProposePollBudget;
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            var view = await Get<IFlows>(IFlows.Key(brainId)).Read(flowId, ct).ConfigureAwait(true);
            if (view is not null && view.Status is FlowStatus.Waiting or FlowStatus.Completed or FlowStatus.Failed or FlowStatus.Cancelled)
            {
                return await FormatChangeStatusAsync(brainId, flowId, ct).ConfigureAwait(true);
            }

            await Task.Delay(ProposePollInterval, ct).ConfigureAwait(true);
        }

        return await FormatChangeStatusAsync(brainId, flowId, ct).ConfigureAwait(true);
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
            return new InoToolResult($"Could not read Feature-to-PR flow '{flowId}': {ex.Message}", Ok: false);
        }

        var inner = JsonSerializer.Deserialize<FeatureToPrInner>(head.InnerJson, FeatureToPrFlow.Json) ?? new FeatureToPrInner();
        var status = view?.Status ?? head.Status;
        var tasksUri = $"neuron://{brainId}/apps/tasks#{flowId}";

        if (status == FlowStatus.Waiting && !string.IsNullOrWhiteSpace(inner.ConfirmationPlanId) && !string.IsNullOrWhiteSpace(inner.CommitSha))
        {
            var shortSha = inner.CommitSha[..Math.Min(8, inner.CommitSha.Length)];
            var branch = inner.Lease?.BranchName ?? "ino/change";
            var summary = new StringBuilder();
            summary.AppendLine($"Feature-to-PR ready for human approval ({flowId}):");
            summary.AppendLine($"- Repository: {inner.Repository} (branch `{branch}` from `{inner.Lease?.BaseBranch ?? "main"}`)");
            summary.AppendLine($"- Commit SHA: `{inner.CommitSha}`");
            summary.AppendLine($"- Changed files ({inner.ChangedFiles.Length}): {string.Join(", ", inner.ChangedFiles)} (+{inner.Additions} / -{inner.Deletions})");
            summary.AppendLine($"- Approval Plan ID: `{inner.ConfirmationPlanId}` (RequiresPerson = true; only the human Person can approve)");
            if (!string.IsNullOrWhiteSpace(inner.CoderSummary))
            {
                summary.AppendLine($"- Summary: {inner.CoderSummary}");
            }

            var card = new InoCard(
                Id: inner.ConfirmationPlanId,
                Kind: "approval",
                Title: $"Approve PR: {head.Title} ({shortSha})",
                Summary: $"{inner.ChangedFiles.Length} file(s) on {branch} (+{inner.Additions} / -{inner.Deletions})",
                SourceNeuronUri: $"neuron://{brainId}/runtime.confirmations/confirmations",
                Status: "pending",
                SurfaceScopeUri: tasksUri);
            return new InoToolResult(summary.ToString().Trim(), card, Ok: true);
        }

        if (status == FlowStatus.Completed)
        {
            var prLine = !string.IsNullOrWhiteSpace(inner.PullRequestUrl)
                ? $"Draft PR: {inner.PullRequestUrl}"
                : $"Pushed ref: {inner.PushedRef}";
            var card = new InoCard(
                Id: flowId,
                Kind: "flow",
                Title: $"Published: {head.Title}",
                Summary: prLine,
                SourceNeuronUri: tasksUri,
                Status: "completed",
                SurfaceScopeUri: inner.PullRequestUrl ?? tasksUri);
            return new InoToolResult(
                $"Feature-to-PR completed ({flowId}). Commit `{inner.CommitSha}` pushed to `{inner.PushedRef}`. {prLine}",
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
        return $"{brainId}/{FeatureToPrFlow.Alias}/{flowId}";
    }

    private static string Kept(Dictionary<string, string> sidecars, string file) => sidecars.ContainsKey(file) ? "kept" : "missing";
}
