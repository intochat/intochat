using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Workspace;

// The surface composer: it rebuilds the surface and, unless the shell docks us, opens the window.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = CircleView.Key(brain);
await foreach (var request in brain.On(brain.Get<IApp>(appKey), "open", brain.Stopping))
{
    try
    {
        await CircleView.Compose(brain);
        await CircleView.Project(brain);
        if (request.Input != "docked")
        {
            await brain.Get<IWorkspace>(CircleView.Scope(brain)).EnsureOpenAsync(appKey + "/window", "Circle",
                WindowReference.For(new UiChildRef(UIVocabulary.SurfaceType, appKey + "/surface")), brain.Stopping);
        }
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await CircleView.Status(brain, "Circles could not load. Reopen Circle to try again."); }
}
