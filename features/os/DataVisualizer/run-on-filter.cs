using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.TextField.Signals;

// Every filter field of every view is watched; a change refreshes each view sharing that field's group.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var fields = Views.Registry<ITextField>(brain, appKey, view => view.Filters.Select(column => $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
await foreach (var changed in brain.On<TextFieldChanged>(fields, brain.Stopping))
{
    // The signal names its field, {app}/views/{group}/filter/{column}, the group being the view id or the
    // shared FilterGroup.
    var views = (await Views.All(brain, appKey))
        .Where(view => view.Filters.Any(column => changed.Name == $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"))
        .Select(view => view.Id).Distinct(StringComparer.Ordinal);
    foreach (var id in views) { await app.Request("refresh", id); }
}
