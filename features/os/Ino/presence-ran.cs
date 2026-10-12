using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Microsoft.CSharp.Runtime;
using DigitalBrain.Microsoft.CSharp.Runtime.Signals;

// Recent app updates, not running tasks. The scene holds the history; subscription processes hold no state.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var own = Ino.Key(brain);
var prefix = Ino.Scope(brain) + "/apps/";
await foreach (var ran in brain.On<BehaviorRan>(brain.Get<IBrainRuns>(BrainRunsKeys.For(Ino.Scope(brain))), brain.Stopping))
{
    if (ran.Outcome == RunOutcome.Skipped || !ran.Behavior.StartsWith(prefix, StringComparison.Ordinal)) { continue; }
    var end = ran.Behavior.IndexOf('/', prefix.Length);
    if (end < 0) { continue; }
    var appKey = ran.Behavior[..end];
    if (appKey == own) { continue; }
    try
    {
        var recent = Ino.RecentApps((await Ino.Home(brain).Read()).Definition);
        var run = $"{ran.Behavior}|{ran.Source}|{ran.SignalType}|{ran.Watermark}";
        if (recent.Any(item => item.GetProperty("run").GetString() == run)) { continue; }
        var title = appKey[prefix.Length..].Replace('-', ' ').Replace('_', ' ');
        try
        {
            var app = await brain.Get<IApp>(appKey).Read();
            if (!string.IsNullOrWhiteSpace(app.Manifest.Title)) { title = app.Manifest.Title; }
        }
        catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
        var outcome = ran.Outcome == RunOutcome.Succeeded ? "Updated" : "Needs attention";
        var item = JsonSerializer.SerializeToElement(new { id = appKey, run, label = $"{title} · {outcome}", kind = "app", progress = 1 });
        var items = recent.Where(entry => entry.GetProperty("id").GetString() != appKey).Append(item).TakeLast(6).ToArray();
        await Ino.ComposeHome(brain, JsonSerializer.Serialize(items));
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { Console.Error.WriteLine($"The activity ring was not rebuilt: {error.Message}"); }
}
