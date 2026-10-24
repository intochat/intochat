using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Text.Signals;

// The single writer of Ino's view. Every other script only changes state and writes the view state, which is
// the dirty signal (index.cs does that for chat index changes). A shown toast is republished away at expiry.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
DateTimeOffset? toastUntil = null;
var refresher = new InoRefresher(async () => toastUntil = await Ino.PublishView(brain));
_ = Expire();
// A settings change (the app setting Model among them) redeploys the program, so a fresh start republishes.
await Refresh();
await foreach (var changed in brain.On<TextChanged>(InoViewState.Text(brain), Refresh, brain.Stopping))
{
    await Refresh();
}

async Task Refresh()
{
    try { await refresher.Refresh(); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}

async Task Expire()
{
    try
    {
        while (!brain.Stopping.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(500), brain.Stopping);
            if (toastUntil is { } until && DateTimeOffset.UtcNow > until) { toastUntil = null; await Refresh(); }
        }
    }
    catch (OperationCanceledException) { }
}
