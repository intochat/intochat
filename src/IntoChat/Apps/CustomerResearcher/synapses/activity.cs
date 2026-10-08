using DigitalBrain.Apps;
using DigitalBrain.Apps.Signals;
using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Card;
using DigitalBrain.Flutter.Layout;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Tabs;
using DigitalBrain.Flutter.Text.Signals;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = brain.Setting("App")!;
var app = brain.Get<IApp>(key);
var active = brain.Get<IText>(key + "/active-research");
var layout = brain.Get<ILayout>(key + "/research-activity");
using var writer = new SemaphoreSlim(1, 1);
using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
var loops = new[] { Invoked(), Completed(), ActiveChanged() };
await Task.WhenAny(loops);
lifetime.Cancel();
await Task.WhenAll(loops);

async Task Invoked()
{
    await foreach (var _ in brain.On<AppInvoked>(app, Project, lifetime.Token)) { await Project(); }
}
async Task Completed()
{
    await foreach (var _ in brain.On<AppInvocationCompleted>(app, Project, lifetime.Token)) { await Project(); }
}
async Task ActiveChanged()
{
    await foreach (var _ in brain.On<TextChanged>(active, Project, lifetime.Token)) { await Project(); }
}
async Task Project()
{
    await writer.WaitAsync(lifetime.Token);
    try
    {
        var records = (await app.ReadInvocations()).Where(item => item.Operation == "research").ToArray();
        var activeId = (await active.Read()).Markdown;
        var pending = records.Where(item => item.Status == InvocationStatus.Pending).ToArray();
        var recent = records.Where(item => item.Status != InvocationStatus.Pending)
            .OrderByDescending(item => item.CompletedAt).Take(20).ToArray();
        var children = new List<UiChildRef>();
        await Add("summary", "Research activity", records.Length == 0
            ? "No research yet. Enter a company above and choose Research."
            : $"{pending.Length} pending · {recent.Length} recent outcomes. Saved company records remain available.");
        foreach (var item in pending.Concat(recent))
        {
            var state = item.Status == InvocationStatus.Pending
                ? (item.Id.ToString() == activeId ? "Running" : "Queued")
                : item.Status == InvocationStatus.Failed ? "Failed"
                : item.Output?.StartsWith("Stopped", StringComparison.Ordinal) == true ? "Stopped" : "Completed";
            var detail = item.Status == InvocationStatus.Pending
                ? (state == "Running" ? "Research is in progress in the browser below." : "Waiting for earlier research.")
                : item.Error ?? item.Output ?? "Finished.";
            if (item.Status == InvocationStatus.Failed || state == "Stopped")
            { detail += "\nEnter this company above and choose Research to retry."; }
            var title = string.IsNullOrWhiteSpace(item.Input) ? "Missing company name" : item.Input.Trim();
            await Add(item.Id.ToString("N"), title, state + "\n" + detail);
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
    finally { writer.Release(); }
}
