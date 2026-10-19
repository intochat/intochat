using IntoChat.OS.Apps;
using IntoChat.OS.Client;

// "review-accept" (input: the review row id) asserts the item as a fact (idempotent on the row id) and marks the
// row accepted. A row already decided is left alone, a request id handled once is not handled again, an unknown
// row id is reported on {app}/status, and the context's "review-count" entry is rebuilt after each decision.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(brain.Setting("App")!);
var handled = new HashSet<Guid>();

await foreach (var request in brain.On(app, "review-accept", brain.Stopping))
{
    if (!handled.Add(request.RequestId)) { continue; }
    await Reviews.Apply(brain, Reviews.Operation.Accept, request.Input.Trim());
}
