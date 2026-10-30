using IntoChat.OS.Client;
using IntoChat.OS.Modules.Mcp;

// A PhotoCraft tool changed the document outside an editor turn: the preview is rendered again from the
// active document onto the declarative view surface.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var tools = PhotoCraft.Tools(brain);

await foreach (var ran in brain.On<McpToolRan>(tools, brain.Stopping))
{
    if (ran.IsError || PhotoCraft.Reads.Contains(ran.Tool)) { continue; }
    try
    {
        if ((await PhotoCraft.Editor(brain).Read()).ActiveRun is not null || await PhotoCraft.Unsettled(brain) is not null) { continue; }
        if (await PhotoCraft.Render(tools, brain.Stopping) is not { } png) { continue; }
        var current = await PhotoCraft.Surface(brain).Read();
        if (current?.Root.Children?.ElementAtOrDefault(3)?.Text == png) { continue; }
        var session = await PhotoCraft.Connect(brain, brain.Stopping);
        await PhotoCraft.RenderPreview(brain, session, PhotoCraft.ChangedWithTools, brain.Stopping);
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine($"PhotoCraft preview after {ran.Tool} was not rendered: {error.Message}"); }
}
