using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Views;

// `get:<publisher>/<name>` installs the app for real and opens it in a window; a refusal
// becomes the gallery's notice instead (nothing half-installed, the click never throws out).
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("get:", StringComparison.Ordinal)) { continue; }
    var listing = clicked.Action["get:".Length..];
    var slash = listing.LastIndexOf('/');
    try
    {
        var outcome = slash <= 0 ? new InstallOutcome(false, null, "That app is not in the gallery.")
            : await brain.Get<IAppStore>(IAppStore.Key(Desktop.Scope(brain))).Get(listing[..slash], listing[(slash + 1)..]);
        await Desktop.Notice(brain).Set(outcome.Ok ? "" : outcome.Message ?? "That app could not be installed.");
        if (outcome is { Ok: true, InstalledName: { } name }) { await Desktop.Open(brain, clicked.RequestId, name); }
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        Console.Error.WriteLine("Desktop could not get " + listing + ": " + error.Message);
        try { await Desktop.Notice(brain).Set("That app could not be installed."); await Desktop.Show(brain); }
        catch (Exception again) when (again is not OperationCanceledException) { Console.Error.WriteLine(again.Message); }
    }
}
