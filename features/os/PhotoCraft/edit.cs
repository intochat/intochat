using IntoChat.OS.Modules.AI.Agents;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Mcp;

// Edit: hand the request to PhotoCraft's editor, an agent turn that may act only through PhotoCraft's own
// tools (never files or the PC). This reaction ends once the turn is started; preview.cs and
// edit-failed.cs settle it.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(PhotoCraft.Key(brain));

await foreach (var request in brain.On(app, "edit", brain.Stopping))
{
    var text = request.Input.Trim();
    if (text.Length is < 1 or > PhotoCraft.MaxRequestChars)
    {
        await PhotoCraft.SetStatus(brain, $"Say what to make or change, in 1-{PhotoCraft.MaxRequestChars} characters.");
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
            await PhotoCraft.SetStatus(brain, "PhotoCraft is still working on the last edit; ask again when it has finished.");
            continue;
        }
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        await PhotoCraft.Show(brain, session, brain.Stopping);
        await PhotoCraft.SetStatus(brain, "Editing: " + PhotoCraft.Clip(text, 300), session.WebUrl ?? "http://localhost:18082");
        var instructions = (await app.Read()).Files.GetValueOrDefault("prompts/editor.md", "")
            + $"\n\nPhotoCraft's tools are on key `{session.McpKey}`.";
        await editor.Start(AgentTurn.Text(text, runId: runId, instructions: instructions, contracts: [typeof(IMcpServer).FullName!]));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    {
        await PhotoCraft.SetStatus(brain, PhotoCraft.Clip("The edit could not start: " + error.Message, PhotoCraft.MaxStatusChars));
    }
}
