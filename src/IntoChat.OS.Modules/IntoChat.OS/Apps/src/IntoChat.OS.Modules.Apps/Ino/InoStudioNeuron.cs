using IntoChat.OS.Modules.AI.Tasks;

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
    ];

    private IUserSettings Settings => Get<IUserSettings>(new DigitalBrain.NeuronId(Id.Brain, "os.settings", "settings"));

    public async Task<InoToolResult> Make(string goal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        var text = goal.Trim();
        if (Context.Services.GetService(typeof(ITaskEngine)) is not ITaskEngine engine)
        {
            return new InoToolResult($"Task engine is unavailable, so I could not start '{text}'.");
        }

        var title = text.Length <= 52 ? text : text[..52];
        AgentTask created;
        try
        {
            var settings = await Settings.Read();
            created = await engine.CreateTaskAsync(new CreateTaskRequest(title, text, settings.DisplayName, [new TaskStepDefinition(title, text, "app.execute")]));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new InoToolResult($"Could not start '{title}': {error.Message}");
        }

        var cardId = $"approval-{created.TaskId}";
        var card = new InoCard(
            cardId,
            "approval",
            $"Approve: {title}",
            "Pre-filled LFP ApprovalCard ready for 1-click confirmation.",
            "neuron://local/os.confirmations/inbox",
            "pending",
            $"neuron://local/apps/ino#{cardId}");
        return new InoToolResult($"Created task {title} ({created.TaskId}); it waits for your approval.", card);
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
