using System.Text;
using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text;
using IntoChat.OS.Postgres.Tables;

// "review" lists the consolidation's pending review items (sensitive or unsure items that were never published)
// as the text neuron {app}/review, oldest first. "review-accept" and "review-reject" decide one (see Reviews.cs).
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
        var pending = (await Reviews.Rows(reviews)).Select(Reviews.View).Where(row => row.GetValueOrDefault("status") == "pending").ToList();
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
