#pragma warning disable MEAI001
using System.Runtime.CompilerServices;
using DigitalBrain.Access;
using DigitalBrain;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Modules.AI.Tests.Integration;

public sealed class VoiceTranscriptionFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APostedRecordingComesBackAsText()
    {
        var stub = new StubTranscriptionService("deploy the release", "whisper-stub");
        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAudioTranscriptionService>(stub));
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
        Assert.Equal(1, stub.Calls);
        Assert.Equal("voice.wav", stub.LastFileName);

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
        Assert.Equal(2, stub.Calls);
        Assert.Equal("clip.wav", stub.LastFileName);
    }

    [Fact]
    public async Task WithNoModelTheAnswerIs503WithTheReason()
    {
        await using var host = await VoiceHostHarness.StartAsync(Ct);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([0x52, 0x49, 0x46, 0x46]),
        };
        request.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync(Ct);
        using var json = JsonDocument.Parse(body);
        var detail = json.RootElement.GetProperty("detail").GetString();
        Assert.False(string.IsNullOrWhiteSpace(detail));
        Assert.Contains("not configured", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedCallerIsRefusedWithoutCallingTranscription()
    {
        var stub = new StubTranscriptionService("should not run", "whisper-stub");
        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAudioTranscriptionService>(stub));
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, stub.Calls);
    }

    [Fact]
    public async Task FoundryLocalStartAsyncDoesNotLoadModelBeforeFirstUse()
    {
        var options = Options.Create(new AIOptions().WithRole(Role.Hear, KnownModels.WhisperLargeV3Turbo, Where.Device));
        var runtime = new FakeFoundryRuntime("hello world");
        await using var service = new FoundryLocalTranscriptionService(
            options,
            NullLogger<FoundryLocalTranscriptionService>.Instance,
            [runtime],
            audioConverter: null);

        await service.StartAsync(Ct);

        Assert.Equal(0, runtime.LoadCount);
        Assert.False(service.IsReady);
        Assert.False(service.InitializationFailed);
        Assert.True(await service.IsReadyAsync(Ct));
        Assert.Equal("whisper-large-v3-turbo", service.ModelId);

        using var firstStream = new MemoryStream([1, 2, 3, 4]);
        using var secondStream = new MemoryStream([5, 6, 7, 8]);
        var results = await Task.WhenAll(
            service.TranscribeAsync(firstStream, "first.wav", Ct),
            service.TranscribeAsync(secondStream, "second.wav", Ct));

        Assert.Equal(["hello world", "hello world"], results);
        Assert.Equal(1, runtime.LoadCount);
        Assert.Equal(2, runtime.TranscribeCount);
        Assert.True(service.IsReady);

        await service.StopAsync(Ct);
        Assert.Equal(1, runtime.UnloadCount);
        Assert.False(service.IsReady);
    }

    [Fact]
    public async Task FoundryLocalLazyLoadFailureReturns503WithReason()
    {
        var options = Options.Create(new AIOptions().WithRole(Role.Hear, KnownModels.WhisperLargeV3Turbo, Where.Device));
        var failingRuntime = new FailingFoundryRuntime("Foundry Local catalog unreachable");
        await using var service = new FoundryLocalTranscriptionService(
            options,
            NullLogger<FoundryLocalTranscriptionService>.Instance,
            [failingRuntime],
            audioConverter: null);

        await service.StartAsync(Ct);
        Assert.True(await service.IsReadyAsync(Ct));

        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAudioTranscriptionService>(service));
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/ai/transcriptions")
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        };
        request.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        using var response = await host.Client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Foundry Local catalog unreachable", json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.True(service.InitializationFailed);
        Assert.False(await service.IsReadyAsync(Ct));
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
        // An app principal ("app:...") as a person, a person in a model conversation and a brain id like "a/b" have no Caller
        // to stand for any more: the edge mints none of them (task 7 ruling on IsDirectPersonCaller).
    }

    [Fact]
    public async Task EmptyBodyOrTraversalFileNameReturns400()
    {
        var stub = new StubTranscriptionService("unused", "whisper-stub");
        await using var host = await VoiceHostHarness.StartAsync(Ct, builder =>
        {
            builder.Services.Replace(ServiceDescriptor.Singleton<IAudioTranscriptionService>(stub));
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
        Assert.Equal(0, stub.Calls);
    }

    private sealed class FakeFoundryRuntime(string transcript) : ILocalRuntime
    {
        private int _loadCount;
        private int _transcribeCount;
        private int _unloadCount;

        public RuntimeId Id => RuntimeId.FoundryLocal;
        public int LoadCount => Volatile.Read(ref _loadCount);
        public int TranscribeCount => Volatile.Read(ref _transcribeCount);
        public int UnloadCount => Volatile.Read(ref _unloadCount);

        public async Task<RuntimeStatus> Probe(CancellationToken ct)
        {
            await Task.Delay(25, ct);
            return new RuntimeStatus(Id, Reachable: true, Version: "2.5.0", Endpoint: null);
        }

        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId id) => throw new NotSupportedException();

        public ISpeechToTextClient? Hear(ModelId id)
        {
            Interlocked.Increment(ref _loadCount);
            return new FakeSpeechClient(this, transcript);
        }

        private sealed class FakeSpeechClient(FakeFoundryRuntime owner, string transcript) : ISpeechToTextClient, IAsyncDisposable
        {
            public Task<SpeechToTextResponse> GetTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref owner._transcribeCount);
                return Task.FromResult(new SpeechToTextResponse(transcript) { ModelId = "whisper-large-v3-turbo" });
            }

            public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(Stream audioSpeechStream, SpeechToTextOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.CompletedTask;
                yield return new SpeechToTextResponseUpdate(transcript);
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() { }
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner._unloadCount);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FailingFoundryRuntime(string reason) : ILocalRuntime
    {
        public RuntimeId Id => RuntimeId.FoundryLocal;
        public Task<RuntimeStatus> Probe(CancellationToken ct) => Task.FromException<RuntimeStatus>(new InvalidOperationException(reason));
        public async IAsyncEnumerable<ModelCard> Catalog([EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ModelCard> Installed([EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<InstallProgress> Install(ModelId id, [EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
        public Task Remove(ModelId id, CancellationToken ct) => Task.CompletedTask;
        public IChatClient Chat(ModelId id) => throw new NotSupportedException();
    }

    private sealed class StubTranscriptionService(string text, string modelId) : IAudioTranscriptionService
    {
        public int Calls { get; private set; }
        public string? LastFileName { get; private set; }

        public Task<bool> IsReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public bool InitializationFailed => false;
        public string? ErrorMessage => null;
        public string ModelId => modelId;

        public Task<string> TranscribeAsync(string audioFilePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastFileName = Path.GetFileName(audioFilePath);
            return Task.FromResult(text);
        }

        public async Task<string> TranscribeAsync(Stream audioStream, string fileName, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastFileName = fileName;
            using var ms = new MemoryStream();
            await audioStream.CopyToAsync(ms, cancellationToken);
            Assert.True(ms.Length > 0);
            return text;
        }
    }

    private sealed class VoiceHostHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly IDisposable _ports;
        private readonly string _homeDir;

        private VoiceHostHarness(WebApplication app, HttpClient client, IDisposable ports, string homeDir, string presenceToken)
        {
            _app = app;
            Client = client;
            _ports = ports;
            _homeDir = homeDir;
            PresenceToken = presenceToken;
        }

        public HttpClient Client { get; }
        public string PresenceToken { get; }

        public static async Task<VoiceHostHarness> StartAsync(CancellationToken ct, Action<WebApplicationBuilder>? configure = null)
        {
            var homeDir = Path.Combine(Path.GetTempPath(), "intochat-voice-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(homeDir, "run"));
            await File.WriteAllBytesAsync(Path.Combine(homeDir, "run", "presence"), RandomNumberGenerator.GetBytes(32), ct);

            var ports = PortReservation.ReservePair();
            WebApplication? app = null;
            HttpClient? client = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    EnvironmentName = "Development",
                    ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
                    ContentRootPath = Path.GetTempPath(),
                });
                builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                    [IntoChatOSNames.HomeKey] = homeDir,
                    [IntoChatOSNames.ShellEnabledKey] = "false",
                    [IntoChatOSNames.FrontEnabledKey] = "false",
                    [AuthKeys.Posture] = "Open",
                    [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
                    [TopologyContext.GatewayPortKey] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture),
                });

                LocalRunDefaults.Apply(builder.Configuration);
                builder.AddIntoChatOSServer(server => server.AddOperatingSystem(OperatingSystemModules.InProcessModules));
                configure?.Invoke(builder);

                app = builder.Build();
                app.MapIntoChatOSServer();
                await app.StartAsync(ct);

                var baseUrl = app.Urls.First();
                client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
                var proof = app.Services.GetRequiredService<IPresenceProof>();

                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
                var ready = false;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    try
                    {
                        using var res = await client.GetAsync("/ready", ct);
                        if (res.IsSuccessStatusCode)
                        {
                            ready = true;
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }

                    await Task.Delay(100, ct);
                }

                if (!ready)
                {
                    throw new TimeoutException($"VoiceHostHarness at '{baseUrl}' did not become ready within 30s.");
                }

                return new VoiceHostHarness(app, client, ports, homeDir, proof.Token);
            }
            catch
            {
                client?.Dispose();
                if (app is not null)
                {
                    await app.DisposeAsync();
                }

                ports.Dispose();
                TryDeleteDirectory(homeDir);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _ports.Dispose();
            TryDeleteDirectory(_homeDir);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) { return; }
            try { Directory.Delete(path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
