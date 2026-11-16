using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Flows;
using DigitalBrain.Units;
using IntoChat.OS.Framework.Logging;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("ino.studio")]
public sealed class InoStudioNeuron(IServiceProvider services) : DigitalBrain.Neurons.Neuron, IInoStudio
{
    private static readonly TimeSpan MakePollBudget = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MakePollInterval = TimeSpan.FromMilliseconds(100);
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
        if (NeuronId.TryParse(flowId, out var flowNeuronId))
        {
            var deadline = DateTimeOffset.UtcNow + MakePollBudget;
            DateTimeOffset? nullDetailGraceDeadline = null;
            FlowView? settled = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                var current = await Get<IFlows>(IFlows.Key(brainId)).Read(flowId).ConfigureAwait(true);
                if (current is not null)
                {
                    if (current.Status is FlowStatus.Waiting or FlowStatus.Completed)
                    {
                        settled = current;
                        break;
                    }

                    if (current.Status is FlowStatus.Failed or FlowStatus.Cancelled)
                    {
                        if (!string.IsNullOrWhiteSpace(current.Detail))
                        {
                            settled = current;
                            break;
                        }

                        // FlowsNeuron.WithHead can observe terminal status from FlowHead before OnFlowDone folds Detail.
                        nullDetailGraceDeadline ??= DateTimeOffset.UtcNow + TimeSpan.FromSeconds(2);
                        settled = current;
                        if (DateTimeOffset.UtcNow >= nullDetailGraceDeadline.Value)
                        {
                            break;
                        }
                    }
                }

                await Task.Delay(MakePollInterval).ConfigureAwait(true);
            }

            if (settled is not null)
            {
                BuildInner? built = null;
                try
                {
                    var head = await Get<IFlowStarter>(flowNeuronId).Head().ConfigureAwait(true);
                    built = JsonSerializer.Deserialize<BuildInner>(head.InnerJson, BuildFlow.Json);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    services.GetService<ILogger<InoStudioNeuron>>()?.LogDebug(ex, "Could not deserialize BuildInner head for flow {FlowId}.", flowId);
                    built = null;
                }

                var alias = !string.IsNullOrWhiteSpace(built?.Alias) ? built.Alias! : settled.Subject;
                if (settled.Status == FlowStatus.Completed && !string.IsNullOrWhiteSpace(alias))
                {
                    var liveViewPath = $"/brains/{brainId}/apps/{alias}/view";
                    var doneCard = new InoCard(
                        flowId,
                        "flow",
                        $"Live: {alias}",
                        $"Verified & live at {liveViewPath}",
                        tasks,
                        "completed",
                        tasks);
                    return new InoToolResult(
                        $"Built, verified, deployed, and installed interactive unit '{alias}' ({flowId}). Live view: `{liveViewPath}` (UI event target: `{brainId}/{alias}`). Inspect the transcript on the Tasks board ({tasks}).",
                        doneCard);
                }

                if (settled.Status == FlowStatus.Waiting && settled.WaitingStep == "install" && !string.IsNullOrWhiteSpace(alias))
                {
                    var liveViewPath = $"/brains/{brainId}/apps/{alias}/view";
                    var planNote = !string.IsNullOrWhiteSpace(built?.PlanId) ? $" (consent plan `{built.PlanId}`)" : string.Empty;
                    var waitingCard = new InoCard(
                        flowId,
                        "flow",
                        $"Ready to install: {alias}",
                        $"Verified & deployed; awaiting your consent{planNote}.",
                        tasks,
                        "waiting",
                        tasks);
                    return new InoToolResult(
                        $"Built, verified, and deployed unit '{alias}' ({flowId}); awaiting your install consent{planNote} on the Tasks board ({tasks}). Once you approve, its interactive view is immediately live at `{liveViewPath}` (UI event target: `{brainId}/{alias}`).",
                        waitingCard);
                }

                if (settled.Status is FlowStatus.Failed or FlowStatus.Cancelled)
                {
                    var detail = !string.IsNullOrWhiteSpace(settled.Detail)
                        ? settled.Detail
                        : services.GetService<IDrafter>() is null
                            ? "No drafter/model provider is configured on this OS brain."
                            : settled.Status.ToString();
                    if (IsMissingModelFailure(detail))
                    {
                        var settingsUri = $"neuron://{brainId}/apps/settings";
                        var setupCard = new InoCard(
                            "setup-model",
                            "settings",
                            "Model setup required",
                            detail,
                            settingsUri,
                            "setup-required",
                            settingsUri);
                        return new InoToolResult(
                            $"Cannot author '{title}' because no model provider is configured: {detail} Configure a model in Settings ({settingsUri}).",
                            setupCard,
                            Ok: false);
                    }

                    var failedCard = new InoCard(
                        flowId,
                        "flow",
                        $"Build {settled.Status.ToString().ToLowerInvariant()}: {title}",
                        detail,
                        tasks,
                        "failed",
                        tasks);
                    return new InoToolResult(
                        $"Build flow {flowId} {settled.Status.ToString().ToLowerInvariant()}: {detail}. Inspect the verify transcript on the Tasks board ({tasks}).",
                        failedCard,
                        Ok: false);
                }
            }
        }

        if (services.GetService<IDrafter>() is null)
        {
            var settingsUri = $"neuron://{brainId}/apps/settings";
            var setupCard = new InoCard(
                "setup-model",
                "settings",
                "Model setup required",
                "No drafter/model provider is configured on this OS brain.",
                settingsUri,
                "setup-required",
                settingsUri);
            return new InoToolResult(
                $"Cannot author '{title}' because no model provider is configured. Configure a model in Settings ({settingsUri}).",
                setupCard,
                Ok: false);
        }

        var card = new InoCard(
            flowId,
            "flow",
            $"Building: {title}",
            "Build flow started; track progress on the Tasks board.",
            tasks,
            "running",
            tasks);
        return new InoToolResult(
            $"Started building {title} ({flowId}). Track authoring, verification, and consent progress on the Tasks board ({tasks}).", card);
    }

    public Task<InoToolResult> CreateInteractiveApp(string? alias, string? title, string goal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var cleanGoal = goal.Trim();
        var prompt = !string.IsNullOrWhiteSpace(alias) && !cleanGoal.Contains(alias.Trim(), StringComparison.OrdinalIgnoreCase)
            ? $"Create an interactive app{(string.IsNullOrWhiteSpace(title) ? "" : $" titled '{title.Trim()}'")} with alias '{alias.Trim()}': {cleanGoal}"
            : cleanGoal;
        return Make(prompt);
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
        if (units.Length == 0)
        {
            var workspaceSkills = await SkillDiscovery.DiscoverAsync(studio).ConfigureAwait(true);
            var skillsSuffix = workspaceSkills.Count == 0
                ? string.Empty
                : "\nSkills:\n" + string.Join("\n", workspaceSkills.Select(s => $"- [skill] {s.Alias} ({s.Source}): {s.Description}"));
            return new InoToolResult(text + "\nUnits: none deployed." + skillsSuffix);
        }
        var lines = new List<string>();
        foreach (var unit in units)
        {
            var alias = unit.Aliases.FirstOrDefault() ?? unit.SourceKey;
            Dictionary<string, string> sidecars;
            try { sidecars = await studio.Sidecars(alias); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                (services.GetService<ILoggerFactory>()?.CreateLogger<InoStudioNeuron>() ?? OsConsoleLog.For<InoStudioNeuron>())
                    .LogSwallowed(error, "reading synapse unit sidecars");
                lines.Add($"- {alias} ({unit.Status}): sidecars unavailable");
                continue;
            }
            lines.Add($"- {alias} ({unit.Status}): spec.md {Kept(sidecars, "spec.md")}, scenarios.json {Kept(sidecars, "scenarios.json")}");
        }
        var discovered = await SkillDiscovery.DiscoverAsync(studio).ConfigureAwait(true);
        var discoveredSection = discovered.Count == 0
            ? string.Empty
            : "\nSkills:\n" + string.Join("\n", discovered.Select(s => $"- [skill] {s.Alias} ({s.Source}): {s.Description}"));
        return new InoToolResult(text + "\nUnits:\n" + string.Join("\n", lines) + discoveredSection);
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
            return new InoToolResult($"Could not start Feature-to-PR on '{cleanRepo}': {ex.Message}", Ok: false);
        }

        string flowId;
        try
        {
            var innerJson = JsonSerializer.Serialize(
                Change.Start(runKey, parsedRepo, cleanGoal, parsedBase),
                FeatureToPrFlow.Json);

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

        var change = JsonSerializer.Deserialize<Change>(head.InnerJson, FeatureToPrFlow.Json) ?? new Change();
        var status = view?.Status ?? head.Status;
        var tasksUri = $"neuron://{brainId}/apps/tasks#{flowId}";

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
        return $"{brainId}/{FeatureToPrFlow.Alias}/{flowId}";
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
