#pragma warning disable MEAI001 // Integration test harness for MEAI voice endpoints.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Access;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Primitives;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.AI.Voice;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IntoChat.OS.Modules.AI.Tests.Integration.Voice;

internal sealed class VoiceHostHarness : IAsyncDisposable
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
    public Uri BaseAddress => Client.BaseAddress!;
    public string PresenceToken { get; }
    public IServiceProvider Services => _app.Services;

    public async Task<HttpResponseMessage> PostJsonAsync(string path, string json, CancellationToken ct, bool authenticated = true)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (authenticated)
        {
            request.Headers.Add(PresenceProof.HeaderName, PresenceToken);
        }

        return await Client.SendAsync(request, ct);
    }

    public static async Task<string> ReadProblemDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("detail", out var detail)
            ? detail.GetString() ?? string.Empty
            : body;
    }

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
        try
        {
            Client.Dispose();
            try
            {
                await _app.StopAsync();
            }
            finally
            {
                await _app.DisposeAsync();
            }
        }
        finally
        {
            _ports.Dispose();
            TryDeleteDirectory(_homeDir);
        }
    }

    private static void TryDeleteDirectory(string path)
        => DigitalBrain.Testing.TempDirectory.DeleteIfPresent(path);
}

internal sealed class StubVoiceClients : IVoiceClients
{
    private readonly Func<string, CancellationToken, Task<Voice<ISpeechToTextClient>>> _hear;
    private readonly Func<string, CancellationToken, Task<Voice<ITextToSpeechClient>>> _speak;
    private readonly Func<string, ConversationId, CancellationToken, Task<TalkVoice>> _talk;

    public StubVoiceClients(
        Func<string, CancellationToken, Task<Voice<ISpeechToTextClient>>>? hear = null,
        Func<string, CancellationToken, Task<Voice<ITextToSpeechClient>>>? speak = null,
        Func<string, ConversationId, CancellationToken, Task<TalkVoice>>? talk = null)
    {
        _hear = hear ?? (static (_, _) => Task.FromResult(new Voice<ISpeechToTextClient>(null, null, "Hear is not configured.")));
        _speak = speak ?? (static (_, _) => Task.FromResult(new Voice<ITextToSpeechClient>(null, null, "Kokoro 82M is not installed for the Speak role.")));
        _talk = talk ?? (static (_, _, _) => Task.FromResult(new TalkVoice(null, null, true, 16_000, "Talk is not configured.")));
    }

    public int HearLookups { get; private set; }
    public int SpeakLookups { get; private set; }

    public Task<Voice<ISpeechToTextClient>> Hear(string brain, CancellationToken ct)
    {
        HearLookups++;
        return _hear(brain, ct);
    }

    public Task<Voice<ITextToSpeechClient>> Speak(string brain, CancellationToken ct)
    {
        SpeakLookups++;
        return _speak(brain, ct);
    }

    public Task<TalkVoice> Talk(string brain, ConversationId conversation, CancellationToken ct)
        => _talk(brain, conversation, ct);
}

internal sealed class StubSpeechToTextClient(string text, string modelId) : ISpeechToTextClient
{
    public int Calls { get; private set; }
    public string? LastModelId { get; private set; }
    public long LastAudioBytes { get; private set; }

    public async Task<SpeechToTextResponse> GetTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastModelId = options?.ModelId;
        using var ms = new MemoryStream();
        await audioSpeechStream.CopyToAsync(ms, cancellationToken);
        LastAudioBytes = ms.Length;
        Assert.True(ms.Length > 0);
        return new SpeechToTextResponse(text) { ModelId = modelId };
    }

    public async IAsyncEnumerable<SpeechToTextResponseUpdate> GetStreamingTextAsync(
        Stream audioSpeechStream,
        SpeechToTextOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetTextAsync(audioSpeechStream, options, cancellationToken);
        yield return new SpeechToTextResponseUpdate(response.Text) { ModelId = response.ModelId };
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}

internal sealed class StubTextToSpeechClient(byte[] payload, string mediaType = "audio/pcm;rate=24000") : ITextToSpeechClient
{
    public int Calls { get; private set; }
    public string? LastText { get; private set; }
    public string? LastVoiceId { get; private set; }

    public Task<TextToSpeechResponse> GetAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        LastText = text;
        LastVoiceId = options?.VoiceId;
        return Task.FromResult(new TextToSpeechResponse([new DataContent(payload, mediaType)]));
    }

    public async IAsyncEnumerable<TextToSpeechResponseUpdate> GetStreamingAudioAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetAudioAsync(text, options, cancellationToken);
        yield return new TextToSpeechResponseUpdate(response.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}

internal sealed class OverrideCallerAmbient(ICallerAmbient inner) : ICallerAmbient
{
    public Caller? Override { get; set; }
    public Caller? Current => Override ?? inner.Current;
}
