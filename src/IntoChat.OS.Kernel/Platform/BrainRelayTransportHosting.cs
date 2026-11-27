using System.Net.Http.Headers;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Runtime;

namespace IntoChat.OS.Kernel.Platform;

/// <summary>
/// Configuration for cross-brain sealed relay transport (<c>/circle/v1/brains/*</c> and <c>/circle/v1/friends/codes/*</c>).
/// When <see cref="RelayBaseUri"/> is <see langword="null"/>, the kernel degrades cleanly to local-only operation.
/// </summary>
public sealed class BrainRelayOptions
{
    public const string SectionName = "IntoChatOS:BrainRelay";

    /// <summary>Optional base URI of the Circle relay service (e.g. <c>https://cloud.intochat.ai</c>).</summary>
    public Uri? RelayBaseUri { get; set; }

    /// <summary>Optional local brain ID for single-brain hosts. When null, resolved per call from the ambient caller.</summary>
    public string? LocalBrainId { get; set; }

    /// <summary>Maximum allowed clock skew on inbound <see cref="BrainEnvelope.SentAtUtc"/> timestamps (default ±5 minutes).</summary>
    public TimeSpan MaxClockSkew { get; set; } = BrainEnvelopeSealer.DefaultMaxClockSkew;

    /// <summary>Maximum number of <c>(SenderBrainId, MessageId)</c> entries retained in the replay defense cache.</summary>
    public int ReplayCacheCapacity { get; set; } = BrainEnvelopeReplayCache.DefaultCapacity;

    /// <summary>When <see langword="true"/> and <see cref="RelayBaseUri"/> is configured, starts the background relay poll loop.</summary>
    public bool EnablePollLoop { get; set; }
}

/// <summary>
/// Kernel-owned pinned public key directory backed by an immutable in-memory pin store with optional read-through
/// to the calling brain's durable <see cref="IFriends"/> roster.
/// </summary>
public sealed class KernelBrainKeyDirectory(
    IServiceProvider services,
    ICallerAmbient? callers = null) : IBrainKeyDirectory
{
    private readonly InMemoryPinnedBrainKeyDirectory _pins = new();

    /// <inheritdoc />
    public async ValueTask<BrainPublicKeys?> GetPinnedKeysAsync(BrainId brainId, CancellationToken ct = default)
    {
        if (await _pins.GetPinnedKeysAsync(brainId, ct).ConfigureAwait(false) is { } pinned)
        {
            return pinned;
        }

        // Fall back to the active caller brain's durable friend roster if available, and pin on first verified read.
        var callerBrain = callers?.Current?.Brain;
        if (callerBrain is not { IsSystem: false } local || string.IsNullOrWhiteSpace(local.Value))
        {
            return null;
        }

        var proxies = services.GetService<INeuronProxies>();
        if (proxies is null)
        {
            return null;
        }

        var friends = await proxies.Get<IFriends>(FriendsDefaults.Id(local.Value)).List().ConfigureAwait(false);
        var match = friends.FirstOrDefault(friend => friend.Brain == brainId);
        if (match?.PublicKeys is not { } friendKeys)
        {
            return null;
        }

        await _pins.PinAsync(brainId, friendKeys, ct).ConfigureAwait(false);
        return friendKeys;
    }

    /// <inheritdoc />
    public ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default)
        => _pins.PinAsync(brainId, publicKeys, ct);

    /// <inheritdoc />
    public ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default)
        => _pins.RotateAsync(proof, ct);
}

/// <summary>
/// Resolves <see cref="HttpBrainRelayClient"/> when <see cref="BrainRelayOptions.RelayBaseUri"/> is configured,
/// and degrades cleanly to unconfigured (<see cref="IsConfigured"/> = <see langword="false"/>) on local-only boots.
/// </summary>
public sealed class KernelBrainRelayClientProvider
{
    internal const string RelayPurpose = "circle-brain-relay";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<BrainRelayOptions> _options;
    private readonly IServiceAssertionSigner? _assertionSigner;
    private readonly TimeProvider _timeProvider;
    private readonly ICallerAmbient? _callers;

    public KernelBrainRelayClientProvider(
        IHttpClientFactory httpFactory,
        IOptions<BrainRelayOptions> options,
        TimeProvider? timeProvider = null,
        IServiceAssertionSigner? assertionSigner = null,
        ICallerAmbient? callers = null)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpFactory = httpFactory;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _assertionSigner = assertionSigner;
        _callers = callers;
    }

    /// <summary>True when a remote relay endpoint is configured.</summary>
    public bool IsConfigured => ResolveBaseUri() is not null;

    /// <summary>Returns a configured <see cref="HttpBrainRelayClient"/>, or <see langword="null"/> when running local-only.</summary>
    public HttpBrainRelayClient? TryGetClient()
    {
        var baseUri = ResolveBaseUri();
        if (baseUri is null)
        {
            return null;
        }

        var http = _httpFactory.CreateClient(BrainRelayTransportHosting.HttpClientName);
        return new HttpBrainRelayClient(http, baseUri, AuthorizeAsync, _timeProvider);
    }

    private Uri? ResolveBaseUri()
        => _options.Value.RelayBaseUri ?? (_assertionSigner is { IsConfigured: true, ServiceUri: { } uri } ? uri : null);

    private async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (_assertionSigner is not { IsConfigured: true })
        {
            return;
        }

        var brainId = _callers?.Current?.Brain.Value ?? _options.Value.LocalBrainId;
        if (string.IsNullOrWhiteSpace(brainId) || request.RequestUri is null)
        {
            return;
        }

        var bodyBytes = request.Content is not null
            ? await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)
            : [];

        var token = _assertionSigner.Sign(
            brainId,
            RelayPurpose,
            request.Method.Method,
            request.RequestUri.PathAndQuery,
            bodyBytes);
        request.Headers.Authorization = new AuthenticationHeaderValue(ServiceAssertion.Scheme, token);
    }
}

/// <summary>
/// DI registration helpers for <see cref="SealedRelayBrainTransport"/>, <see cref="BrainEnvelopeReceiver"/>,
/// <see cref="IBrainKeyDirectory"/>, and <see cref="HttpBrainRelayClient"/> in <c>IntoChat.OS.Kernel</c>.
/// </summary>
public static class BrainRelayTransportHosting
{
    public const string HttpClientName = "IntoChatOS.BrainRelay";

    public static IServiceCollection AddKernelBrainRelayTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BrainRelayOptions>()
            .Configure<IServiceProvider>(static (options, sp) =>
                sp.GetService<IConfiguration>()?.GetSection(BrainRelayOptions.SectionName).Bind(options));
        services.AddHttpClient(HttpClientName, static client =>
        {
            client.Timeout = TimeSpan.FromSeconds(60);
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<BrainEnvelopeReplayCache>(static sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BrainRelayOptions>>().Value;
            var skew = opts.MaxClockSkew > TimeSpan.Zero ? opts.MaxClockSkew : BrainEnvelopeSealer.DefaultMaxClockSkew;
            var capacity = opts.ReplayCacheCapacity > 0 ? opts.ReplayCacheCapacity : BrainEnvelopeReplayCache.DefaultCapacity;
            return new BrainEnvelopeReplayCache(capacity, skew + skew);
        });

        services.TryAddSingleton<IBrainKeyDirectory>(static sp =>
            new KernelBrainKeyDirectory(sp, sp.GetService<ICallerAmbient>()));

        services.TryAddSingleton<KernelBrainRelayClientProvider>(static sp =>
            new KernelBrainRelayClientProvider(
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IOptions<BrainRelayOptions>>(),
                sp.GetService<TimeProvider>(),
                sp.GetService<IServiceAssertionSigner>(),
                sp.GetService<ICallerAmbient>()));

        services.TryAddSingleton<SealedRelayBrainTransport>(static sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BrainRelayOptions>>().Value;
            var signer = sp.GetRequiredService<IBrainKeySigner>();
            var directory = sp.GetRequiredService<IBrainKeyDirectory>();
            var callers = sp.GetRequiredService<ICallerAmbient>();
            var relayProvider = sp.GetRequiredService<KernelBrainRelayClientProvider>();
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var replayCache = sp.GetRequiredService<BrainEnvelopeReplayCache>();
            var skew = opts.MaxClockSkew > TimeSpan.Zero ? opts.MaxClockSkew : BrainEnvelopeSealer.DefaultMaxClockSkew;
            BrainId? localBrain = !string.IsNullOrWhiteSpace(opts.LocalBrainId) ? new BrainId(opts.LocalBrainId) : null;

            return new SealedRelayBrainTransport(
                signer: signer,
                keyLookup: directory.GetPinnedKeysAsync,
                callers: callers,
                relay: (envelope, ct) =>
                {
                    var client = relayProvider.TryGetClient()
                        ?? throw new InvalidOperationException("Cannot send remote brain envelope: IntoChatOS:BrainRelay:RelayBaseUri is not configured.");
                    return client.SendAndWaitReplyAsync(envelope, ct);
                },
                postRelay: (envelope, ct) =>
                {
                    var client = relayProvider.TryGetClient()
                        ?? throw new InvalidOperationException("Cannot post one-way remote brain envelope: IntoChatOS:BrainRelay:RelayBaseUri is not configured.");
                    return client.SendOneWayAsync(envelope, ct);
                },
                isRemote: brain =>
                {
                    if (!relayProvider.IsConfigured || brain.IsSystem)
                    {
                        return false;
                    }

                    if (localBrain is { } configuredLocal)
                    {
                        return brain != configuredLocal;
                    }

                    var currentBrain = callers.Current?.Brain;
                    return currentBrain is { IsSystem: false } active && brain != active;
                },
                localBrain: localBrain,
                timeProvider: time,
                maxClockSkew: skew,
                replayCache: replayCache);
        });

        services.TryAddSingleton<IBrainTransport>(static sp => sp.GetRequiredService<SealedRelayBrainTransport>());

        services.TryAddSingleton<BrainEnvelopeReceiver>(static sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BrainRelayOptions>>().Value;
            var signer = sp.GetRequiredService<IBrainKeySigner>();
            var directory = sp.GetRequiredService<IBrainKeyDirectory>();
            var edge = sp.GetRequiredService<EdgeAuthority>();
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var replayCache = sp.GetRequiredService<BrainEnvelopeReplayCache>();
            var skew = opts.MaxClockSkew > TimeSpan.Zero ? opts.MaxClockSkew : BrainEnvelopeSealer.DefaultMaxClockSkew;
            BrainId? localBrain = !string.IsNullOrWhiteSpace(opts.LocalBrainId) ? new BrainId(opts.LocalBrainId) : null;

            return new BrainEnvelopeReceiver(
                isLocalBrain: brain => !brain.IsSystem && (localBrain is null || brain == localBrain.Value),
                signer: signer,
                keyLookup: directory.GetPinnedKeysAsync,
                edge: edge,
                invoke: (target, call, ct) => InvokeLocalOrleansAsync(sp, target, call, ct),
                post: (target, call) => PostLocalOrleansAsync(sp, target, call),
                timeProvider: time,
                maxClockSkew: skew,
                replayCache: replayCache);
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BrainRelayPollService>());
        return services;
    }

    private static async Task<JsonElement?> InvokeLocalOrleansAsync(
        IServiceProvider services,
        NeuronId target,
        RuntimeInvocation call,
        CancellationToken ct)
    {
        var grains = services.GetRequiredService<IGrainFactory>();
        var grainType = await grains.GetGrain<INeuronHostCatalog>("/").HostType(target.Type).WaitAsync(ct).ConfigureAwait(false)
            ?? "neuron.host";
        var host = grains.GetGrain<INeuronHost>(GrainId.Create(grainType, target.ToString()));
        var interleaved = await host.IsInterleaved(call).WaitAsync(ct).ConfigureAwait(false);
        return await (interleaved ? host.InvokeInterleaved(call, ct) : host.Invoke(call, ct)).ConfigureAwait(false);
    }

    private static async Task PostLocalOrleansAsync(IServiceProvider services, NeuronId target, RuntimeInvocation call)
    {
        var grains = services.GetRequiredService<IGrainFactory>();
        var grainType = await grains.GetGrain<INeuronHostCatalog>("/").HostType(target.Type).ConfigureAwait(false)
            ?? "neuron.host";
        var host = grains.GetGrain<INeuronHost>(GrainId.Create(grainType, target.ToString()));
        var interleaved = await host.IsInterleaved(call).ConfigureAwait(false);
        await (interleaved ? host.PostInterleaved(call) : host.Post(call)).ConfigureAwait(false);
    }

    private sealed class BrainRelayPollService(
        IOptions<BrainRelayOptions> options,
        KernelBrainRelayClientProvider clientProvider,
        BrainEnvelopeReceiver receiver,
        ILogger<BrainRelayPollService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!options.Value.EnablePollLoop || !clientProvider.IsConfigured)
            {
                return;
            }

            var client = clientProvider.TryGetClient();
            if (client is null)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await client.RunPollLoopAsync(receiver, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Brain relay poll loop encountered an error; backing off before retry.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
    }
}
