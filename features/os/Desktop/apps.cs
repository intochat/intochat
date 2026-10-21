using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `gallery` shows or hides the apps gallery; hiding it clears the last notice.
// (Named apps.cs: gallery.cs would collide with Gallery.cs on case-insensitive disks.)
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (clicked.Action != "gallery") { continue; }
    try
    {
        var showing = (await Desktop.ShowGallery(brain).Read()).Markdown == "true";
        await Desktop.ShowGallery(brain).Set(showing ? "false" : "true");
        if (showing) { await Desktop.Notice(brain).Set(""); }
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    { Console.Error.WriteLine("Desktop could not toggle the gallery: " + error.Message); }
}
