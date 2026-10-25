using IntoChat.OS.Client;
using IntoChat.OS.Modules.Flutter.TextField;
using IntoChat.OS.Modules.Flutter.TextField.Signals;

// The sidebar search: each change of the search field becomes the durable query; view.cs republishes.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var field = brain.Get<ITextField>(Ino.DataKey(brain) + "/chat-search");
var viewState = InoViewState.Text(brain);
await foreach (var change in brain.On<TextFieldChanged>(field, brain.Stopping))
{
    try
    {
        var query = InoLayout.CleanQuery((await field.Read()).Value);
        if ((await InoViewState.Read(viewState, DateTimeOffset.UtcNow)).Query == query) { continue; }
        await InoViewState.Update(viewState, state => state with { Query = query }, DateTimeOffset.UtcNow);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}
