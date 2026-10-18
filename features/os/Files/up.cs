using IntoChat.OS.Apps;
using IntoChat.OS.Client;
using IntoChat.OS.Flutter.Collection;

await using var brain = await IntoChatOSClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var app = brain.Get<IApp>(appKey);
// The ledger also retains completed no-ops before this subscription can be recreated.
var ledger = await RequestLedger.LoadAsync(brain, "up");
await foreach (var request in brain.On(app, "up", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    var breadcrumbs = (await brain.Get<ICollectionView>(appKey + "/listing").Read()).Definition.Breadcrumbs;
    if (breadcrumbs is { Length: > 1 }) { await app.Request("browse", breadcrumbs[^2].Id); }
    await ledger.Record(request.RequestId);
}
