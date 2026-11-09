using System.Text.Json;
using IntoChat.OS.Modules.Apps.Flows;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("ino.studio")]
public sealed class InoStudioNeuron : DigitalBrain.Neuron, IInoStudio
{
    // Starts the five-step unit flow (author, draft, verify, deploy, install); the flow's author step names the alias. The card points at
    // the flow on the Tasks board, where the verify transcript and the consent to the unit's needs appear.
    public async Task<InoToolResult> Make(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var text = goal.Trim();
        var title = text.Length <= 52 ? text : text[..52];
        string flowId;
        try
        {
            var inner = JsonSerializer.Serialize(new BuildInner { Goal = text }, BuildFlow.Json);
            flowId = await Get<IFlows>(IFlows.Key(Id.Brain.Value)).Start(BuildFlow.Alias, title, null, inner);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start '{title}': {error.Message}");
        }

        var tasks = $"neuron://local/apps/tasks#{flowId}";
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
        var studios = (await ShippedManifests.Of(Context.Services)).Studios();
        var text = "Studios:\n" + string.Join("\n", studios.Select(s => $"- {s.App}: {s.Description}"));
        var studio = Get<ISynapseStudio>(ISynapseStudio.Key(Id.Brain.Value));
        DigitalBrain.DeployedSource[] units;
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

    private static string Kept(Dictionary<string, string> sidecars, string file) => sidecars.ContainsKey(file) ? "kept" : "missing";
}
