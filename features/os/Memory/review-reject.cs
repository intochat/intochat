using IntoChat.OS.Apps;
using IntoChat.OS.Client;

// "review-reject" (input: the review row id) marks the row rejected. A row already decided is left alone, a
// request id handled once is not handled again, an unknown row id is reported on {app}/status, and the context's
// "review-count" entry is rebuilt after each decision.
await using var brain = await IntoChatOSClient.ConnectAsync(args);
var app = brain.Get<IApp>(brain.Setting("App")!);
var handled = new HashSet<Guid>();

await foreach (var request in brain.On(app, "review-reject", brain.Stopping))
{
    if (!handled.Add(request.RequestId)) { continue; }
    await Reviews.Apply(brain, Reviews.Operation.Reject, request.Input.Trim());
}
