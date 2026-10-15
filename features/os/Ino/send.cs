using DigitalBrain.AI.Routing;
using DigitalBrain.Apps;

// The assistant tool path: "send" by request. The composer path lives in submissions.cs.
// Jev first: the request is asked of the router and this reaction ends; routed.cs answers it.
// A replayed request is skipped, so Jev is asked once per message.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(Ino.Key(brain));
var router = brain.Get<IRouter>(Ino.Scope(brain));
var ledger = await RequestLedger.LoadAsync(brain, "send");
await foreach (var request in brain.On(app, "send", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    // Jev keeps the requests it was asked: one it already holds was sent before this ledger saw it.
    if (await router.Pending(request.RequestId, brain.Stopping) is not null) { await ledger.Record(request.RequestId); continue; }
    if (string.IsNullOrWhiteSpace(request.Input) || request.Input.Length > 32000) { await ledger.Record(request.RequestId); continue; }
    await app.Progress("listening", "Heard you");
    if (request.Input.Length > Ino.MaxRoutedChars)
    {
        await Ino.Status(brain, "A long message goes straight to the model");
        await Ino.Answer(brain, await app.Read(), request.Input, "", request.RequestId);
    }
    else
    {
        try { await router.Ask(new RouteRequest(request.RequestId, request.Input), brain.Stopping); }
        catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
        {
            await Ino.Status(brain, "Jev unavailable; asking the model. " + Ino.Clip(error.Message));
            await app.Progress("attention", "Jev unavailable; asking the model");
            await Ino.Answer(brain, await app.Read(), request.Input, "", request.RequestId);
        }
    }
    await ledger.Record(request.RequestId);
}
