using System.Text.Json;
using IntoChat.OS.Modules.Apps.Flows;
using IntoChat.OS.Modules.ArtCraft;

namespace IntoChat.OS.Modules.Apps;

[DigitalBrain.Neuron("ino.studio")]
public sealed class InoStudioNeuron : DigitalBrain.Neuron, IInoStudio
{
    private static readonly (string Id, string Description)[] Studios =
    [
        ("markdraw", "Draw and edit diagrams"),
        ("photocraft", "Edit photos"),
        ("filmcraft", "Edit video"),
        ("data", "Query and chart data"),
        .. ArtCraftCatalog.All
            .Where(static a => a.Id is not ("photocraft" or "filmcraft"))
            .Select(static a => (a.Id, a.Subtitle)),
    ];

    // Starts a build flow of kind unit; the card is its approval, keyed by the flow id.
    public async Task<InoToolResult> Make(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var text = goal.Trim();
        var title = text.Length <= 52 ? text : text[..52];
        string flowId;
        try
        {
            var inner = JsonSerializer.Serialize(new BuildInner { Kind = BuildInner.Unit, Goal = text }, BuildFlow.Json);
            flowId = await Get<IFlows>(IFlows.Key(Id.Brain.Value)).Start(BuildFlow.Alias, title, null, inner);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start '{title}': {error.Message}");
        }

        var card = new InoCard(
            flowId,
            "approval",
            $"Approve: {title}",
            "The build flow asks for this approval once the unit is deployed.",
            "neuron://local/os.confirmations/inbox",
            "pending",
            $"neuron://local/apps/ino#{flowId}");
        return new InoToolResult($"Started build {title} ({flowId}); it waits for your approval once deployed.", card);
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

    public Task<InoToolResult> List()
        => Task.FromResult(new InoToolResult("Studios:\n" + string.Join("\n", Studios.Select(s => $"- {s.Id}: {s.Description}"))));
}
