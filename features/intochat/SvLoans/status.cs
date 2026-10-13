using DigitalBrain.Flutter.Layout;
using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Flutter.Text;

// One reaction: the last result of every operation, one line each, into {app}/status.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var main = brain.Get<ILayout>(key + "/main");
await main.Set(new("column", [new("text", key + "/status")]), (await main.Read()).Revision);
await brain.Get<IApp>(key).Present(long.Parse(brain.Setting("ProgramRevision")!, System.Globalization.CultureInfo.InvariantCulture),
    new(new Sphere("icon.svg", []), Main: new("layout", key + "/main")));

await foreach (var request in brain.On(app, "status", brain.Stopping))
{
    var lines = new List<string>();
    foreach (var operation in Spike.Operations)
    {
        var latest = (await brain.Get<IText>($"{key}/results/{operation}/latest").Read()).Markdown;
        if (latest.Length == 0) { lines.Add($"- {operation}: no result yet"); continue; }
        using var document = JsonDocument.Parse(latest);
        var root = document.RootElement;
        lines.Add($"- {operation}: {Spike.Text(root, "outcome")} at {Spike.Text(root, "at")}: {Spike.Text(root, "detail")}");
    }
    await brain.Get<IText>($"{key}/status").Set(string.Join("\n", lines));
}
