using System.Text.Json;
using DigitalBrain.Client;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

// A behavior ran: the last 200 runs are kept in {app}/runs/recent (the runs neuron has no read method), and
// the panel's list is refreshed when the run belongs to the selected neuron. The ring is a cache seeded from
// that log when the process starts; a redelivered run is already in it and changes nothing.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var recent = new List<BrainView.Run>(await BrainView.ReadRecent(brain));
await foreach (var ran in brain.On<BehaviorRan>(brain.Get<IBrainRuns>(BrainRunsKeys.For(BrainView.Scope(brain))), brain.Stopping))
{
    if (ran.Outcome == RunOutcome.Skipped) { continue; }
    var mark = $"{ran.Behavior}|{ran.Source}|{ran.SignalType}|{ran.Watermark}|{ran.Outcome}";
    if (recent.Any(run => run.Mark == mark)) { continue; }
    recent.Add(new(ran.Behavior, ran.Behavior[(ran.Behavior.LastIndexOf('/') + 1)..], ran.Outcome.ToString(),
        ran.DurationMicroseconds, ran.Trace?.TraceId ?? "-", mark));
    if (recent.Count > 200) { recent.RemoveRange(0, recent.Count - 200); }
    try
    {
        await BrainView.Recent(brain).Set(JsonSerializer.Serialize(recent));
        var selected = (await BrainView.Selected(brain).Read()).Title;
        if (selected.Length > 0 && ran.Behavior.StartsWith(selected, StringComparison.Ordinal)) { await BrainView.ShowRuns(brain, selected, recent); }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine($"The run of {ran.Behavior} was not logged: {error.Message}"); }
}
