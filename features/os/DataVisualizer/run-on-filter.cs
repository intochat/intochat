using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Flutter.TextField;
using DigitalBrain.Flutter.TextField.Signals;

// Every filter field of every view is watched; a change refreshes each view sharing that field's group.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var fields = Views.Registry<ITextField>(brain, appKey, view => view.Filters.Select(column => $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
await foreach (var changed in brain.On<TextFieldChanged>(fields, brain.Stopping))
{
    // {app}/views/{group}/filter/{column}, the group being the view id or the shared FilterGroup;
    // Publisher.Name is the full grain id, so match by suffix.
    var views = (await Views.All(brain, appKey))
        .Where(view => view.Filters.Any(column => changed.Publisher.Name.EndsWith($"/{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}", StringComparison.Ordinal)))
        .Select(view => view.Id).Distinct(StringComparer.Ordinal);
    foreach (var id in views) { await app.Request("refresh", id); }
}
