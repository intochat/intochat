using DigitalBrain.Apps;

// "open" composes the surface, opens the window unless docked, and asks the list to render.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Engineer.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Engineer.ComposeAsync(brain);
    if (request.Input != "docked") { await Engineer.OpenWindowAsync(brain); }
    await Engineer.Bump(brain);
}
