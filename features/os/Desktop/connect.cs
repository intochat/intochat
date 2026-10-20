using System.Text.Json;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Button;
using IntoChat.OS.Flutter.Views;
using IntoChat.OS.Integrations;

// `connect:<connectorId>` asks the platform for the provider's sign-in page (real OAuth). The card then
// shows a sign-in button whose activation {url} the shell opens in the browser; a connector this host
// has not configured shows why instead. The click never throws out.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var clicked in brain.On<ViewAction>(brain.Get<IView>(Desktop.ViewKey(brain)), brain.Stopping))
{
    if (!clicked.Action.StartsWith("connect:", StringComparison.Ordinal)) { continue; }
    var connector = clicked.Action["connect:".Length..];
    try
    {
        var login = await brain.Get<IConnectionRequests>(Desktop.Scope(brain)).LoginUrl(connector);
        if (login.Url is { } url)
        { await brain.Get<IButton>(Desktop.SignInKey(brain)).SetActivation("Sign in", JsonSerializer.Serialize(new { url })); }
        await Desktop.Connecting(brain).Set(login.Url is null ? "" : connector);
        await Desktop.Notice(brain).Set(login.Message ?? "");
        await Desktop.Show(brain);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        Console.Error.WriteLine("Desktop could not connect " + connector + ": " + error.Message);
        try { await Desktop.Notice(brain).Set("That connection could not be started."); await Desktop.Show(brain); }
        catch (Exception again) when (again is not OperationCanceledException) { Console.Error.WriteLine(again.Message); }
    }
}
