using IntoChat.OS.Client;
using IntoChat.OS.Flutter.TextField;
using IntoChat.OS.Flutter.TextField.Signals;

// The sidebar search: each change of the search field filters the chat list; the query is durable view state.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var field = brain.Get<ITextField>(Ino.DataKey(brain) + "/chat-search");
await foreach (var change in brain.On<TextFieldChanged>(field, brain.Stopping))
{
    try
    {
        var query = (await field.Read()).Value;
        var state = await InoViewState.Load(brain);
        if (state.Query == query) { continue; }
        await InoViewState.Save(brain, state with { Query = query });
        await Ino.PublishView(brain);
    }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested) { }
}
