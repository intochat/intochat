using DigitalBrain.Apps;

// Opening the assistant: compose the surface and, unless the shell docks us, open our window.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Assistant.Key(brain));
await foreach (var request in brain.On(app, "open", brain.Stopping))
{
    await Assistant.ComposeAsync(brain);
    if (request.Input != "docked") { await Assistant.OpenWindowAsync(brain, brain.Stopping); }
}
