using DigitalBrain.AI.Agents;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Mcp;

// Edit: hand the request to PhotoCraft's editor, an agent turn that may act only through PhotoCraft's own
// tools (never files or the PC). This reaction ends once the turn is started; preview.cs and
// edit-failed.cs settle it. The request id is the run id, so a redelivered request starts nothing new.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(PhotoCraft.Key(brain));

await foreach (var request in brain.On(app, "edit", brain.Stopping))
{
    var status = PhotoCraft.Status(brain);
    var text = request.Input.Trim();
    if (text.Length is < 1 or > PhotoCraft.MaxRequestChars)
    {
        await status.Set($"Say what to make or change, in 1-{PhotoCraft.MaxRequestChars} characters.");
        continue;
    }
    var runId = request.RequestId.ToString("N");
    var editor = PhotoCraft.Editor(brain);
    try
    {
        var state = await editor.Read();
        if (state.ActiveRun?.RunId == runId || state.Runs.Any(run => run.RunId == runId)) { continue; }
        if (state.ActiveRun is not null)
        {
            await status.Set("PhotoCraft is still working on the last edit; ask again when it has finished.");
            continue;
        }
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        await PhotoCraft.Show(brain, session, brain.Stopping);
        await status.Set("Editing: " + PhotoCraft.Clip(text, 300));
        var instructions = (await app.Read()).Files.GetValueOrDefault("prompts/editor.md", "")
            + $"\n\nPhotoCraft's tools are on key `{session.McpKey}`.";
        await editor.Start(AgentTurn.Text(text, runId: runId, instructions: instructions, contracts: [typeof(IMcpServer).FullName!]));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await status.Set(PhotoCraft.Clip("The edit could not start: " + error.Message, PhotoCraft.MaxStatusChars)); }
}
