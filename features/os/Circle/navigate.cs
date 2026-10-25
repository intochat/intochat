using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Client;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var request in brain.On(brain.Get<IApp>(CircleView.Key(brain)), "navigate", brain.Stopping))
{
    try { await CircleView.Navigate(brain, PublicationUi.Decode<PublicationUi.Navigation>(request.Input)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await CircleView.Fail(brain, error); }
}
