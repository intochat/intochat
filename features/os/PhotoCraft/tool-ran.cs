using IntoChat.OS.Client;
using IntoChat.OS.Modules.Mcp;

// A PhotoCraft tool changed the document outside an editor turn (the assistant ran PhotoCraft's tools
// itself): the preview is rendered again from the active document. An editor turn is left to preview.cs,
// which renders it when it ends; tools that only read are ignored, and an unchanged image is not written
// again, so a late or repeated signal changes nothing.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var tools = PhotoCraft.Tools(brain);

await foreach (var ran in brain.On<McpToolRan>(tools, brain.Stopping))
{
    if (ran.IsError || PhotoCraft.Reads.Contains(ran.Tool)) { continue; }
    try
    {
        if ((await PhotoCraft.Editor(brain).Read()).ActiveRun is not null || await PhotoCraft.Unsettled(brain) is not null) { continue; }
        if (await PhotoCraft.Render(tools, brain.Stopping) is not { } png) { continue; }
        var preview = PhotoCraft.Preview(brain);
        if ((await preview.Read()).Url == png) { continue; }
        await preview.Set(png, "image/png", PhotoCraft.ChangedWithTools);
    }
    // The status line belongs to edits; a render that fails here leaves the last preview and is logged.
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine($"PhotoCraft preview after {ran.Tool} was not rendered: {error.Message}"); }
}
