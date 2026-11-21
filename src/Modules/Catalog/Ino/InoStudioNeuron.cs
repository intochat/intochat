using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Log;
using DigitalBrain.Flows;
using DigitalBrain.Units;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.ArtCraft;
using IntoChat.OS.Modules.Microsoft.GitHub.WorkTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using IntoChat.OS.Host.Logging;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neurons.Neuron("ino.studio")]
internal sealed partial class InoStudioNeuron(IServiceProvider services) : DigitalBrain.Neurons.Neuron<InoStudioState>, IStudio
{
    // Bounded well below Orleans' default 30s grain RPC ResponseTimeout so contention still returns a tracking card cleanly.
    private static readonly TimeSpan MakePollBudget = TimeSpan.FromSeconds(15);
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
            OsConsoleLog.For<InoStudioNeuron>().LogSwallowed(error, $"starting unit build flow '{title}'");
            return new InoToolResult($"Could not start '{title}': {error.Message}");
        }

        var tasks = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "tasks").Key}#{flowId}";
        if (NeuronId.TryParse(flowId, out var flowNeuronId))
        {
            var deadline = Context.Time.GetUtcNow() + MakePollBudget;
            DateTimeOffset? nullDetailGraceDeadline = null;
            FlowView? settled = null;
            while (Context.Time.GetUtcNow() < deadline)
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
                        nullDetailGraceDeadline ??= Context.Time.GetUtcNow() + TimeSpan.FromSeconds(2);
                        settled = current;
                        if (Context.Time.GetUtcNow() >= nullDetailGraceDeadline.Value)
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
                        var settingsUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "settings").Key}";
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
            var settingsUri = $"neuron://{IntoChat.OS.Host.Primitives.AppRef.Of(Id.Brain, "settings").Key}";
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
            await Get<IApp>(new DigitalBrain.NeuronId(Id.Brain, IntoChat.OS.Host.Primitives.AppGrainType.Name, $"apps/{app}")).Request("open", "");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoStudioNeuron>().LogSwallowed(error, $"opening app '{app}'");
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
        catch (Exception error) when (error is not OperationCanceledException)
        {
            OsConsoleLog.For<InoStudioNeuron>().LogSwallowed(error, "listing synapse units");
            return new InoToolResult(text + $"\nUnits are not available: {error.Message}");
        }
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
}
