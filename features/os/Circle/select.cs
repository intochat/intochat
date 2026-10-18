using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Collection;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
await foreach (var selected in brain.On<CollectionActivated>(brain.Get<ICollectionView>(CircleView.Key(brain) + "/feed-items"), brain.Stopping))
{
    try { await CircleView.Navigate(brain, PublicationUi.Decode<PublicationUi.Navigation>(selected.ItemId)); }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested) { await CircleView.Fail(brain, error); }
}
