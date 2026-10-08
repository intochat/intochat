using System.Text.Json;
using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Text;
using DigitalBrain.Flutter.Text.Signals;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.TextField.Signals;
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var registry = brain.Get<IText>(appKey + "/views");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

while (!brain.Stopping.IsCancellationRequested)
{
    var snapshot = await registry.Read();
    var views = snapshot.Markdown.Length == 0 ? [] : JsonSerializer.Deserialize<View[]>(snapshot.Markdown, json) ?? [];
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
        var fields = views.SelectMany(view => view.Filters.Select(column => new
        {
            Name = $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}",
            View = view.Id,
        })).GroupBy(field => field.Name);
        await Task.WhenAll(fields.Select(group => Pump<TextFieldChanged>(brain.Get<ITextField>(group.Key),
            group.Select(field => field.View).Distinct().ToArray(), scope.Token)).Append(Registry()));
    }
    catch (OperationCanceledException) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task Pump<T>(INeuron source, string[] views, CancellationToken ct) where T : Signal
{
    async Task Refresh() { foreach (var view in views) { await app.Invoke(new(Guid.NewGuid(), "refresh", view)); } }
    await foreach (var _ in brain.On<T>(source, Refresh, ct)) { await Refresh(); }
}

sealed record View(string Id, string Title, string Kind, string Source, string Sql, string[] Filters, int RefreshSeconds, string? FilterGroup = null);
