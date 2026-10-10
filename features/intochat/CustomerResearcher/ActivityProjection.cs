using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using DigitalBrain.Client;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Tabs;
using DigitalBrain.Flutter.Text;

// The activity projection reads the durable queue neuron: every pending company as Queued or
// Running, and the most recent outcomes. It is rebuilt whole from that state on every change.
public static class Activity
{
    static readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);

    public static async Task Project(DigitalBrainConnection brain, string key)
    {
        // A stale revision means another synapse's rebuild just wrote newer derived state; a rebuild
        // reads authoritative state, so rerunning it from scratch is correct.
        for (var attempt = 1; ; attempt++)
        {
            try { await Rebuild(brain, key); return; }
            catch (InvalidOperationException) when (attempt < 3) { }
        }
    }

    static async Task Rebuild(DigitalBrainConnection brain, string key)
    {
        var queue = brain.Get<IText>(key + "/queue");
        var active = brain.Get<IText>(key + "/active-research");
        var layout = brain.Get<ILayout>(key + "/research-activity");
        var text = (await queue.Read()).Markdown;
        var records = text.Length == 0 ? [] : JsonSerializer.Deserialize<QueuedResearch[]>(text, json) ?? [];
        var activeId = (await active.Read()).Markdown;
        var pending = records.Where(item => item.Status is "pending" or "running").ToArray();
        var recent = records.Where(item => item.Status is not ("pending" or "running")).Reverse().Take(20).ToArray();
        var children = new List<UiChildRef>();
        await Add("summary", "Research activity", records.Length == 0
            ? "No research yet. Enter a company above and choose Research."
            : $"{pending.Length} pending · {recent.Length} recent outcomes. Saved company records remain available.");
        foreach (var item in pending.Concat(recent))
        {
            var state = item.Status == "running" ? "Running"
                : item.Status == "pending" ? (item.Id == activeId ? "Running" : "Queued")
                : item.Status == "failed" ? "Failed"
                : item.Status == "stopped" || item.Outcome?.StartsWith("Stopped", StringComparison.Ordinal) == true ? "Stopped"
                : "Completed";
            var detail = item.Status is "pending" or "running"
                ? (state == "Running" ? "Research is in progress in the browser below." : "Waiting for earlier research.")
                : item.Outcome ?? "Finished.";
            if (item.Status == "failed" || state == "Stopped")
            { detail += "\nEnter this company above and choose Research to retry."; }
            var title = string.IsNullOrWhiteSpace(item.Query) ? "Missing company name" : item.Query.Trim();
            await Add(item.Id, title, state + "\n" + detail);
        }
        if (children.Count <= 128)
        {
            await layout.Set(new("list", children.ToArray()), (await layout.Read()).Revision);
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

        async Task Add(string id, string title, string body)
        {
            var name = key + "/research-activity/" + id;
            await brain.Get<ICard>(name).Set(title, body);
            children.Add(new(UIVocabulary.CardType, name));
        }
    }
}

