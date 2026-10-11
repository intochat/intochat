using DigitalBrain.Flutter;
using DigitalBrain.Flutter.Chat;

namespace DigitalBrain.Modules.Flutter.Tests.Chat;

public sealed class ComposerFacts
{
    [Fact]
    public async Task UncertainSubmissionRetainsItsIdentityAndRetryWakesTheBehavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        await composer.Edit("a", "retry safely");
        var id = (await composer.Submit(Guid.NewGuid(), "a", "retry safely"))!.Value;
        await composer.Defer(id, "Delivery could not be confirmed. Send to retry.");
        await brain.DeactivateAsync(composer, ct);
        await using var retried = await brain.Observe<ComposerSubmitted>(composer, ct);
        Assert.Equal(id, await composer.Submit(Guid.NewGuid(), "a", "retry safely"));
        Assert.Equal(id, (await retried.NextAsync(ct: ct)).RequestId);
        Assert.Equal("pending", Assert.Single((await composer.Read()).Submissions).Status);
        Assert.Equal("retry safely", (await composer.Read()).Draft);
        await composer.Settle(id);
        Assert.Empty((await composer.Read()).Draft);
    }

    [Fact]
    public async Task RetryingCapturedAudioRestoresTheWakeupWithoutReplacingAudio()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        var id = Guid.NewGuid();
        await composer.BeginVoice(id, "a");
        await composer.CaptureVoice(id, [1, 2], "audio/wav");
        await using var captured = await brain.Observe<ComposerVoiceCaptured>(composer, ct);
        await composer.CaptureVoice(id, [3, 4], "audio/wav");
        Assert.Equal(id, (await captured.NextAsync(ct: ct)).CaptureId);
        Assert.Equal([1, 2], (await composer.ReadAudio(id))!.Bytes);
    }

    [Fact]
    public async Task SubmissionCapturesOneDraftAndAcknowledgementPreservesNewerEdits()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("conversation-a", 1);
        await composer.Edit("conversation-a", "first draft");
        var id = Guid.NewGuid();
        Assert.Equal(id, await composer.Submit(id, "conversation-a", "first draft"));
        Assert.Equal(id, await composer.Submit(Guid.NewGuid(), "conversation-a", "first draft"));
        await composer.Edit("conversation-a", "newer draft");
        await brain.DeactivateAsync(composer, ct);
        var request = Assert.Single((await composer.Read()).Submissions);
        Assert.Equal((id, "conversation-a", "first draft", "pending"), (request.Id, request.Context, request.Text, request.Status));
        await composer.Settle(id);
        await composer.Settle(id);
        Assert.Equal("newer draft", (await composer.Read()).Draft);
        Assert.Equal("accepted", Assert.Single((await composer.Read()).Submissions).Status);
    }

    [Fact]
    public async Task RefusalPreservesInputAndSuccessfulAcknowledgementClearsOnlyItsDraft()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        await composer.Edit("a", " \n ");
        Assert.Null(await composer.Submit(Guid.NewGuid(), "a", " \n "));
        Assert.Empty((await composer.Read()).Submissions);
        await composer.Edit("a", "hello");
        var id = (await composer.Submit(Guid.NewGuid(), "a", "hello"))!.Value;
        await composer.Settle(id, "A response is already running. Try again when it finishes.");
        Assert.Equal("hello", (await composer.Read()).Draft);
        Assert.Contains("already running", (await composer.Read()).Error);
        var retry = (await composer.Submit(Guid.NewGuid(), "a", "hello"))!.Value;
        Assert.NotEqual(id, retry);
        await composer.Settle(retry);
        Assert.Equal("", (await composer.Read()).Draft);
        Assert.Null((await composer.Read()).Error);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("conversation")]
    [InlineData("cancel")]
    [InlineData("failure")]
    public async Task LateOrFailedTranscriptionCannotOverwriteProtectedInput(string change)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        await composer.Edit("a", "existing draft");
        var capture = Guid.NewGuid();
        await composer.BeginVoice(capture, "a");
        await composer.CaptureVoice(capture, [1, 2, 3], "audio/wav");
        await brain.DeactivateAsync(composer, ct);
        Assert.Equal([1, 2, 3], (await composer.ReadAudio(capture))!.Bytes);
        if (change == "edit") { await composer.Edit("a", "newer edit"); }
        if (change == "conversation") { await composer.Configure("b", 2); await composer.Edit("b", "new conversation draft"); }
        if (change == "cancel") { await composer.CancelVoice(capture); }
        var expected = (await composer.Read()).Draft;
        await composer.CompleteVoice(capture, "transcribed words", change == "failure" ? "Transcription unavailable. Try again." : null);
        Assert.Equal(expected, (await composer.Read()).Draft);
        Assert.Empty((await composer.Read()).Submissions);
        Assert.Null(await composer.ReadAudio(capture));
    }

    [Fact]
    public async Task VoiceProducesAnEditableDraftWithoutSubmittingAndRecomposePreservesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await TestBrain.Create().WithModule<FlutterModule>().StartAsync(ct);
        var composer = brain.Get<IComposer>("owner/a/apps/chat/composer");
        await composer.Configure("a", 1);
        var capture = Guid.NewGuid();
        await composer.BeginVoice(capture, "a");
        await composer.CaptureVoice(capture, [1], "audio/wav");
        await composer.CompleteVoice(capture, "voice draft");
        await composer.Configure("a", 1);
        await composer.CompleteVoice(capture, "late duplicate");
        Assert.Equal("voice draft", (await composer.Read()).Draft);
        Assert.Empty((await composer.Read()).Submissions);
        await composer.Edit("a", "edited voice draft");
        await composer.Configure("old", 0);
        Assert.Equal("a", (await composer.Read()).Context);
        await Assert.ThrowsAsync<InvalidOperationException>(() => composer.Edit("old", "stale edit"));
        Assert.Equal("edited voice draft", (await composer.Read()).Draft);
        var submission = (await composer.Submit(Guid.NewGuid(), "a", "edited voice draft"))!.Value;
        await composer.Settle(submission);
        Assert.Null((await composer.Read()).Voice);
    }
}
