using System.Globalization;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Select;

// A slot's choice changed: show the detail again, which re-arms Install with the chosen connections.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var slots = Sources.Of([.. Enumerable.Range(0, AppsView.SlotsShown).Select(i => Apps.Slot(brain, i))]);
await foreach (var changed in brain.On<SelectChanged>(slots, brain.Stopping))
{
    try { await Apps.Show(brain); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { Console.Error.WriteLine("Apps could not re-arm Install: " + error.GetType().Name); }
}
