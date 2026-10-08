using DigitalBrain.AI.Media;
using DigitalBrain.Client;
using DigitalBrain.Flutter.Chat;

await using var brain = await DigitalBrainClient.ConnectAsync(args);
var appKey = brain.Setting("App")!;
var composer = brain.Get<IComposer>(appKey + "/composer");
Guid activeId = Guid.Empty;
CancellationTokenSource? active = null;
await Task.WhenAll(Captures(), Changes());

async Task Captures()
{
    await foreach (var _ in brain.On<ComposerVoiceCaptured>(composer, Transcribe, brain.Stopping)) { await Transcribe(); }
}

async Task Changes()
{
    await foreach (var _ in brain.On<ComposerChanged>(composer, CheckCancellation, brain.Stopping)) { await CheckCancellation(); }
}

async Task CheckCancellation()
{
    var voice = (await composer.Read()).Voice;
    if (active is { } running && (voice?.Id != activeId || voice.Status != "transcribing")) { await running.CancelAsync(); }
}

async Task Transcribe()
{
    if ((await composer.Read()).Voice is not { Status: "transcribing" } voice) { return; }
    var audio = await composer.ReadAudio(voice.Id);
    if (audio is null) { return; }
    using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(brain.Stopping);
    activeId = voice.Id;
    active = cancellation;
    try
    {
        await CheckCancellation();
        var result = await brain.Get<ISpeechRecognizer>(appKey + "/recognizer")
            .Recognize(new SpeechRecognitionRequest(new MediaPayload(audio.Bytes, audio.MimeType), "voice.wav"), cancellation.Token);
        await composer.CompleteVoice(voice.Id, result.Text);
    }
    catch (OperationCanceledException) when (!brain.Stopping.IsCancellationRequested) { await composer.CancelVoice(voice.Id); }
    catch (Exception) when (!brain.Stopping.IsCancellationRequested)
    { await composer.CompleteVoice(voice.Id, null, "Transcription failed. Your draft is unchanged. Try recording again or type your message."); }
    finally { active = null; activeId = Guid.Empty; }
}
