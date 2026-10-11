using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

// A behavior ran: Ino acts, and the home scene's activity ring shows the last 12 runs. The ring here is a
// cache that starts empty when the process is reaped; the scene is the state. A redelivered run is already
// in the ring and changes nothing.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var own = Ino.Key(brain);
var recent = new Queue<(string Id, string Label)>();
await foreach (var ran in brain.On<BehaviorRan>(brain.Get<IBrainRuns>(BrainRunsKeys.For(Ino.Scope(brain))), brain.Stopping))
{
    if (ran.Behavior.StartsWith(own, StringComparison.Ordinal)) { continue; }
    var id = $"{ran.Behavior}|{ran.Source}|{ran.SignalType}|{ran.Watermark}";
    if (recent.Any(item => item.Id == id)) { continue; }
    await Ino.Fire(brain, "act");
    recent.Enqueue((id, ran.Behavior[(ran.Behavior.LastIndexOf('/') + 1)..]));
    while (recent.Count > 12) { recent.Dequeue(); }
    var items = recent.Select(item => new { id = item.Id, label = item.Label, kind = "behavior", progress = 1 }).ToArray();
    try { await Ino.Compose(brain, JsonSerializer.Serialize(items)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { Console.Error.WriteLine($"The activity ring was not rebuilt: {error.Message}"); }
}
