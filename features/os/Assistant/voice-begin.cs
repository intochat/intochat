using DigitalBrain.Apps;
using DigitalBrain.Flutter.Chat;

// The dock's mic: "voice" composes the surface and begins a voice message on the composer under the
// request id. The composer captures the recording and voice.cs transcribes it into the draft. A
// recording already being transcribed is never replaced; a capture that only began is begun again.
// A replayed request is skipped, so a finished capture is never begun again.
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var key = Assistant.Key(brain);
var app = brain.Get<IApp>(key);
var composer = brain.Get<IComposer>(key + "/composer");
var ledger = await RequestLedger.LoadAsync(brain, "voice");
await foreach (var request in brain.On(app, "voice", brain.Stopping))
{
    if (ledger.Handled(request.RequestId)) { continue; }
    try
    {
        await Assistant.ComposeAsync(brain);
        var state = await composer.Read();
        if (state.Voice is not { Status: "transcribing" })
        {
            await composer.BeginVoice(request.RequestId, state.Context);
            await app.Progress("listening", "Listening");
        }
    }
    catch (Exception error) when (!brain.Stopping.IsCancellationRequested)
    { await Assistant.Status(brain, "Could not start voice input: " + Assistant.Clip(error.Message)); }
    await ledger.Record(request.RequestId);
}
