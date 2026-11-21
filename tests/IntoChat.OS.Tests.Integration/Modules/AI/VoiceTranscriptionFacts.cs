#pragma warning disable MEAI001 // Integration tests for POST /ai/transcriptions on ISpeechToTextClient.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Tests.Integration.Voice;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class VoiceTranscriptionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APostedRecordingComesBackAsText()
    {
        var hear = new StubSpeechToTextClient("deploy the release", "whisper-stub");
        var stub = new StubVoiceClients(
            hear: (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(hear, new ModelId("whisper-stub"), null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        using var rawRequest = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions?fileName=voice.wav")
        {
            Content = new ByteArrayContent([0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00]),
        };
        rawRequest.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        rawRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var rawResponse = await host.Client.SendAsync(rawRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, rawResponse.StatusCode);

        using var rawJson = JsonDocument.Parse(await rawResponse.Content.ReadAsStringAsync(Ct));
        Assert.Equal("deploy the release", rawJson.RootElement.GetProperty("text").GetString());
        Assert.Equal("whisper-stub", rawJson.RootElement.GetProperty("model").GetString());
        Assert.Equal(1, hear.Calls);
        Assert.Equal("whisper-stub", hear.LastModelId);

        using var multipart = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent([1, 2, 3, 4]);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        multipart.Add(fileContent, "file", "clip.wav");
        using var multipartRequest = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = multipart,
        };
        multipartRequest.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);

        using var multipartResponse = await host.Client.SendAsync(multipartRequest, Ct);
        Assert.Equal(HttpStatusCode.OK, multipartResponse.StatusCode);
        Assert.Equal(2, hear.Calls);
    }

    [Fact]
    public async Task WithNoModelTheAnswerIs503WithTheReason()
    {
        var stub = new StubVoiceClients(
            hear: (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(
                null,
                KnownModels.WhisperLargeV3Turbo.Id,
                "Whisper Large v3 Turbo is not configured for the Hear role.")));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([0x52, 0x49, 0x46, 0x46]),
        };
        request.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var detail = await VoiceHostHarness.ReadProblemDetailAsync(response, Ct);
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Contains("not configured", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedCallerIsRefusedWithoutCallingTranscription()
    {
        var hear = new StubSpeechToTextClient("should not run", "whisper-stub");
        var stub = new StubVoiceClients(
            hear: (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(hear, new ModelId("whisper-stub"), null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, hear.Calls);
        Assert.Equal(0, stub.HearLookups);
    }

    [Fact]
    public async Task AppCallerIsRejectedWith403()
    {
        var hear = new StubSpeechToTextClient("should not run", "whisper-stub");
        var stub = new StubVoiceClients(
            hear: (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(hear, new ModelId("whisper-stub"), null)));
        OverrideCallerAmbient? callerOverride = null;

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
            var existing = builder.Services.LastOrDefault(d => d.ServiceType == typeof(ICallerAmbient));
            if (existing is not null)
            {
                builder.Services.Remove(existing);
                builder.Services.AddSingleton<ICallerAmbient>(sp =>
                {
                    var inner = existing.ImplementationInstance as ICallerAmbient
                        ?? (existing.ImplementationFactory?.Invoke(sp) as ICallerAmbient)
                        ?? (ICallerAmbient)ActivatorUtilities.CreateInstance(sp, existing.ImplementationType!);
                    callerOverride = new OverrideCallerAmbient(inner);
                    return callerOverride;
                });
            }
        });

        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        var owner = new Actor.Person("owner", "owner");
        var brain = new BrainId("owner");
        Assert.NotNull(callerOverride);
        callerOverride!.Override = edge.App("some-app", owner, brain);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        request.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, hear.Calls);
    }

    [Fact]
    public void NonPersonCallersAreRejectedByCallerGuard()
    {
        var edge = IntoChat.OS.Tests.TestEdge.Authority;
        var owner = new Actor.Person("owner", "owner");
        var brain = new BrainId("owner");
        Assert.True(AIModule.IsDirectPersonCaller(edge.Person("owner", "owner", brain)));
        Assert.False(AIModule.IsDirectPersonCaller(null));
        Assert.False(AIModule.IsDirectPersonCaller(edge.Agent(owner, brain)));
        Assert.False(AIModule.IsDirectPersonCaller(edge.App("some-app", owner, brain)));
        Assert.False(AIModule.IsDirectPersonCaller(edge.Kernel(brain)));
        Assert.False(AIModule.IsDirectPersonCaller(edge.Scheduler(brain)));
        Assert.False(AIModule.IsDirectPersonCaller(edge.Person("try-guest1", "try-guest1", new BrainId("try-guest1"))));
        Assert.False(AIModule.IsDirectPersonCaller(edge.Guest("owner", "owner", brain)));
        Assert.False(AIModule.IsDirectPersonCaller(edge.RelayedPerson("owner", "owner", brain)));
    }

    [Fact]
    public async Task EmptyBodyOrTraversalFileNameReturns400()
    {
        var hear = new StubSpeechToTextClient("unused", "whisper-stub");
        var stub = new StubVoiceClients(
            hear: (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(hear, new ModelId("whisper-stub"), null)));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IVoiceClients>(stub));
        });

        using var emptyReq = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([]),
        };
        emptyReq.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        using var emptyRes = await host.Client.SendAsync(emptyReq, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, emptyRes.StatusCode);

        using var badNameReq = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions?fileName=../evil.wav")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        badNameReq.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        using var badNameRes = await host.Client.SendAsync(badNameReq, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, badNameRes.StatusCode);
        Assert.Equal(0, hear.Calls);
    }
}
