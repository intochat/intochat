using System.Text.Json;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Tabs;

// The activity projection derives the queue from its durable facts: every open company as Queued or
// Running, and the most recent outcomes. Each rebuild computes the whole view from those facts; card keys
// are request ids, so a rebuild never duplicates a card. The queue neuron is written last and records what
// was projected, so a rebuild writes only the cards and layout that differ from it.
public static class Activity
{
    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    sealed record Card(string Id, string Title, string Body);

    public static Task Project(DigitalBrainConnection brain, string key) => Rebuild(brain, key);

    static async Task Rebuild(DigitalBrainConnection brain, string key)
    {
        var records = ResearchState.Derive(
            await ResearchState.ReadRequests(ResearchState.Requests(brain, key)),
            await ResearchState.ReadCancellations(ResearchState.Cancellations(brain, key)),
            await ResearchState.ReadOutcomes(ResearchState.Outcomes(brain, key)),
            (await ResearchState.Active(brain, key).Read()).Markdown);
        var queue = ResearchState.Queue(brain, key);
        var projected = (await queue.Read()).Markdown;
        var cards = View(records);
        foreach (var card in cards)
        { await brain.Get<ICard>(Name(key, card.Id)).Set(card.Title, card.Body); }

        var children = cards.Select(card => new UiChildRef(UIVocabulary.CardType, Name(key, card.Id))).ToArray();
        var layout = brain.Get<ILayout>(key + "/research-activity");
        if (children.Length <= 128)
        {
            var current = await layout.Read();
            if (current.Definition is not { Mode: "list" } shown || !shown.Children.SequenceEqual(children))
            { await layout.Set(new("list", children), current.Revision); }
        }
        else
        {
            // Keep every pending entry available within the composition child limit.
            var pages = new List<TabItem>();
            foreach (var batch in children.Skip(1).Chunk(100))
            {
                var id = "page-" + pages.Count;
                var pageKey = key + "/research-activity/" + id;
                var page = brain.Get<ILayout>(pageKey);
                await page.Set(new("list", batch), (await page.Read()).Revision);
                pages.Add(new(id, $"Activity {pages.Count * 100 + 1}–{pages.Count * 100 + batch.Length}", new(UIVocabulary.LayoutType, pageKey)));
            }
            var tabsKey = key + "/research-activity/pages";
            var tabs = brain.Get<ITabs>(tabsKey);
            var selected = (await tabs.Read()).SelectedId;
            await tabs.Set(pages, pages.Any(page => page.Id == selected) ? selected : pages[0].Id);
            await layout.Set(new("column", [children[0], new(UIVocabulary.TabsType, tabsKey)], Extents: [80, 0]),
                (await layout.Read()).Revision);
        }
        var next = JsonSerializer.Serialize(records, json);
        if (next != projected) { await queue.Set(next); }
    }

    static string Name(string key, string id) => key + "/research-activity/" + id;

    static QueuedResearch[] Parse(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) { return []; }
        try { return JsonSerializer.Deserialize<QueuedResearch[]>(markdown, json) ?? []; }
        catch (JsonException) { return []; } // an older shape is simply not a projection to compare with
    }

    static Card[] View(QueuedResearch[] records)
    {
        var pending = records.Where(item => item.Status is "pending" or "running").ToArray();
        var recent = records.Where(item => item.Status is not ("pending" or "running")).Reverse().Take(20).ToArray();
        var summary = new Card("summary", "Your research", records.Length == 0
            ? "Your first company starts here. Enter its name or website above.\nVerified findings and their sources will appear here; nothing is shared with your app publication."
            : $"{pending.Length} pending · {recent.Length} recent outcomes. Saved company records remain available.");
        return [summary, .. pending.Concat(recent).Select(item =>
        {
            var state = item.Status switch
            {
                "running" => "Running",
                "pending" => "Queued",
                "failed" => "Failed",
                "stopped" => "Stopped",
                _ => "Completed",
            };
            var detail = state switch
            {
                "Running" => "Research is in progress in the browser below.",
                "Queued" => "Waiting for earlier research.",
                _ => item.Outcome ?? "Finished.",
            };
            if (state is "Failed" or "Stopped") { detail += "\nEnter this company above and choose Research to retry."; }
            var title = string.IsNullOrWhiteSpace(item.Query) ? "Missing company name" : item.Query.Trim();
            return new Card(item.Id, title, state + "\n" + detail);
        })];
    }
}
