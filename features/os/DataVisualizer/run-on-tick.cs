using DigitalBrain.Apps;
using DigitalBrain.Client;
using DigitalBrain.Time.Reminders;
using DigitalBrain.Time.Reminders.Signals;

// Every view with a refresh interval owns a reminder at {app}/views/{id}/refresh; its tick refreshes that view.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
var reminders = Views.Registry<IReminder>(brain, appKey, view => view.RefreshSeconds > 0 ? [$"{appKey}/views/{view.Id}/refresh"] : []);
await foreach (var tick in brain.On<ReminderTick>(reminders, brain.Stopping))
{
    // The tick names its reminder; a view removed since is skipped.
    var view = (await Views.All(brain, appKey)).FirstOrDefault(view => tick.ReminderId == $"{appKey}/views/{view.Id}/refresh");
    if (view is not null) { await app.Request("refresh", view.Id); }
}
