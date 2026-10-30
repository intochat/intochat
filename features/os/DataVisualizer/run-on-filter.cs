using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Flutter.Views.Signals;

// Every filter surface of every view is watched; a change refreshes each view sharing that filter's group.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var fields = Views.Registry<IView>(brain, appKey, view => view.Filters.Select(column => $"{appKey}/views/{view.FilterGroup ?? view.Id}/filter/{column}"));
await foreach (var changed in brain.On<ViewChanged>(fields, brain.Stopping))
{
    _ = changed;
    foreach (var view in await Views.All(brain, appKey))
    {
        await app.Request("refresh", view.Id);
    }
}
