#pragma warning disable MEAI001 // Integration tests for POST /ai/speech on ITextToSpeechClient.
using System.Net;
using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Voice;

[Collection("VoiceHost")]
public sealed class SpeechEndpointFacts(VoiceHostFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadAloudReturnsAWav()
    {
        var speak = new StubTextToSpeechClient(new byte[480], "audio/pcm;rate=24000");
        var stub = new StubVoiceClients(
            speak: (_, _) => Task.FromResult(new Voice<ITextToSpeechClient>(speak, KnownModels.Kokoro82M.Id, null)));

        using var _ = fixture.UseClients(stub);
        var host = fixture.Host;

        using var response = await host.PostJsonAsync("/ai/speech", """{"text":"Hello.","voice":"af_heart"}""", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/wav", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync(Ct);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal(24_000, BitConverter.ToInt32(bytes, 24));
        Assert.Equal(44 + 480, bytes.Length);
        Assert.Equal(1, speak.Calls);
        Assert.Equal("Hello.", speak.LastText);
        Assert.Equal("af_heart", speak.LastVoiceId);
    }

    [Fact]
    public async Task ReadAloudWithoutSpeakIs503WithTheReason()
    {
        var stub = new StubVoiceClients(
            speak: (_, _) => Task.FromResult(new Voice<ITextToSpeechClient>(
                null,
                KnownModels.Kokoro82M.Id,
                "Kokoro 82M is not installed for the Speak role.")));

        using var _ = fixture.UseClients(stub);
        var host = fixture.Host;

        using var response = await host.PostJsonAsync("/ai/speech", """{"text":"x"}""", Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var detail = await VoiceHostHarness.ReadProblemDetailAsync(response, Ct);
        Assert.Contains("Kokoro 82M", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAloudRefusesEmptyAndOverlongText()
    {
        var speak = new StubTextToSpeechClient(new byte[4]);
        var stub = new StubVoiceClients(
            speak: (_, _) => Task.FromResult(new Voice<ITextToSpeechClient>(speak, KnownModels.Kokoro82M.Id, null)));

        using var _ = fixture.UseClients(stub);
        var host = fixture.Host;

        using var empty = await host.PostJsonAsync("/ai/speech", """{"text":""}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var overlong = new string('a', 4_097);
        using var tooLong = await host.PostJsonAsync("/ai/speech", $$"""{"text":"{{overlong}}"}""", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        Assert.Equal(0, speak.Calls);
        Assert.Equal(0, stub.SpeakLookups);
    }

    [Fact]
    public async Task AnAppCannotReadAloud()
    {
        var speak = new StubTextToSpeechClient(new byte[4]);
        var stub = new StubVoiceClients(
            speak: (_, _) => Task.FromResult(new Voice<ITextToSpeechClient>(speak, KnownModels.Kokoro82M.Id, null)));

        using var _ = fixture.UseClients(stub);
        var host = fixture.Host;

        using var unauthenticated = await host.PostJsonAsync("/ai/speech", """{"text":"hi"}""", Ct, authenticated: false);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);

        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        var owner = new Actor.Person("owner", "owner");
        var brain = new BrainId("owner");
        using var callerScope = fixture.OverrideCaller(edge.App("some-app", owner, brain));

        using var asApp = await host.PostJsonAsync("/ai/speech", """{"text":"hi"}""", Ct, authenticated: true);
        Assert.Equal(HttpStatusCode.Forbidden, asApp.StatusCode);
        Assert.Equal(0, speak.Calls);
        Assert.Equal(0, stub.SpeakLookups);
    }
}
