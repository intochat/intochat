using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Reminders.Signals;

// Every view with a refresh interval owns a reminder; its tick refreshes that view.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");

while (!brain.Stopping.IsCancellationRequested)
{
    var snapshot = await registry.Read();
    var views = snapshot.Markdown.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(snapshot.Markdown, Views.Json) ?? [];
    using var scope = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    async Task Changed()
    {
        if ((await registry.Read()).Version != snapshot.Version) { await scope.CancelAsync(); }
    }
    async Task Registry()
    {
        await foreach (var _ in brain.On<TextChanged>(registry, Changed, scope.Token)) { await Changed(); }
    }
    try
    {
        await Task.WhenAll(views.Where(view => view.RefreshSeconds > 0).Select(view => Pump<ReminderTick>(brain.Get<IReminder>($"{appKey}/views/{view.Id}/refresh"), view.Id, scope.Token)).Append(Registry()));
    }
    catch (OperationCanceledException) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task Pump<T>(INeuron source, string view, CancellationToken ct) where T : Signal
{
    async Task Refresh() { await app.Request("refresh", view); }
    await foreach (var _ in brain.On<T>(source, Refresh, ct)) { await Refresh(); }
}
