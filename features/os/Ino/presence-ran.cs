using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

// A behavior ran: the home scene's activity ring shows the last 12 runs, then Ino acts. The ring here is a
// cache restored from the scene after the process is reaped; the scene is the state. A redelivered run is already
// in the ring and changes nothing.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var own = Ino.Key(brain);
var recent = new Queue<(string Id, string Label)>();
var stored = (await Ino.Home(brain).Read()).Definition.Nodes.FirstOrDefault(node => node.Id == "ring")?.Parameters.GetValueOrDefault("items");
if (!string.IsNullOrWhiteSpace(stored))
{
    foreach (var item in JsonSerializer.Deserialize<JsonElement[]>(stored) ?? [])
    { recent.Enqueue((item.GetProperty("id").GetString()!, item.GetProperty("label").GetString()!)); }
}
await foreach (var ran in brain.On<BehaviorRan>(brain.Get<IBrainRuns>(BrainRunsKeys.For(Ino.Scope(brain))), brain.Stopping))
{
    if (ran.Behavior.StartsWith(own, StringComparison.Ordinal)) { continue; }
    var id = $"{ran.Behavior}|{ran.Source}|{ran.SignalType}|{ran.Watermark}";
    if (recent.Any(item => item.Id == id)) { continue; }
    recent.Enqueue((id, ran.Behavior[(ran.Behavior.LastIndexOf('/') + 1)..]));
    while (recent.Count > 12) { recent.Dequeue(); }
    var items = recent.Select(item => new { id = item.Id, label = item.Label, kind = "behavior", progress = 1 }).ToArray();
    try { await Ino.ComposeHome(brain, JsonSerializer.Serialize(items)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { Console.Error.WriteLine($"The activity ring was not rebuilt: {error.Message}"); }
    await Ino.Fire(brain, "act");
}
