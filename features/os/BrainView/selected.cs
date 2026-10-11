using DigitalBrain.Client;
using DigitalBrain.Flame.Scene.Signals;

// A tap on a projected neuron selects it: the card names it, the sphere highlights it and the panel lists
// its last runs from the recent-runs log. A redelivered tap sets the same state again.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
await foreach (var input in brain.On<SceneInput>(BrainView.Scene(brain), brain.Stopping))
{
    if (input.Kind != "tap" || input.Payload is not { Length: > 0 } neuronKey) { continue; }
    try
    {
        await BrainView.Selected(brain).Set(neuronKey, "Selected neuron");
        await BrainView.ComposeScene(brain, neuronKey);
        await BrainView.ShowRuns(brain, neuronKey, await BrainView.ReadRecent(brain));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine($"The selection of {neuronKey} was not shown: {error.Message}"); }
}
