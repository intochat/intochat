using System.Text;
using System.Text.Json;
using IntoChat.OS.Apps;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;

// "review" lists the consolidation's pending review items (sensitive or unsure items that were never published)
// as the text neuron {app}/review, oldest first.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var reviews = brain.Get<ITable>(Consolidation.ReviewTableKey(appKey));
var answer = brain.Get<IText>(Consolidation.ReviewTextKey(appKey));

await foreach (var request in brain.On(app, "review", brain.Stopping))
{
    string text;
    try
    {
        var pending = new List<Dictionary<string, string>>();
        for (var offset = 0; ; offset += 200)
        {
            var page = await reviews.Page(offset, 200);
            pending.AddRange(page.Select(cells => cells.ToDictionary(cell => cell.Column, cell => Text(cell.Json), StringComparer.Ordinal))
                .Where(row => row.GetValueOrDefault("status") == "pending"));
            if (page.Length < 200) { break; }
        }
        var builder = new StringBuilder();
        builder.Append("Pending review items: ").Append(pending.Count).Append('\n');
        foreach (var row in pending.OrderBy(row => row.GetValueOrDefault("at", ""), StringComparer.Ordinal))
        {
            builder.Append("- [").Append(row.GetValueOrDefault("kind", "")).Append("] ").Append(row.GetValueOrDefault("text", ""))
                .Append(" (").Append(row.GetValueOrDefault("reason", "")).Append("; ").Append(row.GetValueOrDefault("id", "")).Append(")\n");
        }
        text = builder.ToString();
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { text = "Memory could not list the review items: " + error.Message; }
    await answer.Set(text);
}

static string Text(string literal)
{
    using var document = JsonDocument.Parse(literal);
    return document.RootElement.ValueKind == JsonValueKind.String ? document.RootElement.GetString() ?? "" : document.RootElement.GetRawText();
}
