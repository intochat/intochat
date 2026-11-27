using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Invocation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Devices.Link;
using IntoChat.OS.Modules.Identity;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Runtime;
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

    /// <summary>Optional local brain ID for single-brain hosts. When null, resolved per call from the ambient caller or target envelope.</summary>
    public string? LocalBrainId { get; set; }

    /// <summary>Optional bearer access token for desktop/home-server brains authenticating via <c>IntoChatMachine</c> (<c>Bearer</c> + <c>X-Machine-Proof</c>).</summary>
    public string? BearerToken { get; set; }

    /// <summary>Optional async delegate producing an <c>X-Machine-Proof</c> header value for machine-authenticated relay requests.</summary>
    public Func<HttpRequestMessage, byte[], CancellationToken, ValueTask<string?>>? MachineProofFactory { get; set; }

    /// <summary>Maximum allowed clock skew on inbound <see cref="BrainEnvelope.SentAtUtc"/> timestamps (default ±5 minutes).</summary>
    public TimeSpan MaxClockSkew { get; set; } = BrainEnvelopeSealer.DefaultMaxClockSkew;

    /// <summary>Maximum number of <c>(SenderBrainId, MessageId)</c> entries retained in the replay defense cache.</summary>
    public int ReplayCacheCapacity { get; set; } = BrainEnvelopeReplayCache.DefaultCapacity;

    /// <summary>When <see langword="true"/> and <see cref="RelayBaseUri"/> is configured, starts the background relay poll loop.</summary>
    public bool EnablePollLoop { get; set; }
}

/// <summary>
/// Kernel-owned multi-brain-isolated pinned public key directory backed by per-<c>(localBrain, remoteBrain)</c>
/// immutable pin stores with cold-start read-through to the target brain's durable <see cref="IFriends"/> roster,
/// plus fail-closed sender brain-to-account binding for <see cref="BrainEnvelopeReceiver"/>'s <c>verifyCallerAccount</c> gate.
/// </summary>
public sealed class KernelBrainKeyDirectory(
    IServiceProvider services,
    ICallerAmbient? callers = null,
    IOptions<BrainRelayOptions>? options = null) : IBrainKeyDirectory
{
    private const string DefaultScopeKey = "*";

    private readonly ConcurrentDictionary<string, InMemoryPinnedBrainKeyDirectory> _scopedPins = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string LocalBrain, string RemoteBrain), string> _pinnedAccounts = new();
    private readonly Lock _accountGate = new();

    /// <summary>
    /// Immutably pins the trusted <paramref name="accountId"/> for <paramref name="remoteBrain"/> in the default/active scope.
    /// </summary>
    public void PinAccount(BrainId remoteBrain, string accountId)
        => PinAccountFor(ResolveActiveLocalBrain(), remoteBrain, accountId);

    /// <summary>
    /// Immutably pins the trusted <paramref name="accountId"/> for <paramref name="remoteBrain"/> scoped to <paramref name="localBrain"/>.
    /// Idempotent for the same account; rejects silent account reassignment fail-closed.
    /// </summary>
    public void PinAccountFor(BrainId? localBrain, BrainId remoteBrain, string accountId)
    {
        if (string.IsNullOrWhiteSpace(remoteBrain.Value))
        {
            throw new ArgumentException("Remote BrainId must be non-empty.", nameof(remoteBrain));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var scope = ScopeKey(localBrain);
        var key = (scope, remoteBrain.Value);

        lock (_accountGate)
        {
            if (_pinnedAccounts.TryGetValue(key, out var existing))
            {
                if (!string.Equals(existing, accountId, StringComparison.Ordinal))
                {
                    throw new CryptographicException(
                        $"Account for brain '{remoteBrain.Value}' (scope '{scope}') is already pinned and cannot be silently reassigned.");
                }

                return;
            }

            _pinnedAccounts[key] = accountId;
        }
    }

    /// <summary>
    /// Fail-closed verification that <paramref name="callerAccount"/> is authorized for <paramref name="senderBrain"/>
    /// in the active local brain scope.
    /// </summary>
    public ValueTask<bool> VerifyCallerAccountAsync(BrainId senderBrain, string callerAccount, CancellationToken ct = default)
        => VerifyCallerAccountForAsync(ResolveActiveLocalBrain() ?? default, senderBrain, callerAccount, ct);

    /// <summary>
    /// Fail-closed multi-brain verification that <paramref name="callerAccount"/> is authorized for <paramref name="senderBrain"/>
    /// when delivering to <paramref name="targetBrain"/>. Supports cold-start roster lookup when <c>callers.Current</c> is <see langword="null"/>.
    /// </summary>
    public async ValueTask<bool> VerifyCallerAccountForAsync(
        BrainId targetBrain,
        BrainId senderBrain,
        string callerAccount,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(senderBrain.Value) || string.IsNullOrWhiteSpace(callerAccount))
        {
            return false;
        }

        var localScope = ResolveScopeBrain(targetBrain);
        var scopeKey = ScopeKey(localScope);
        if (_pinnedAccounts.TryGetValue((scopeKey, senderBrain.Value), out var scopedAccount))
        {
            return string.Equals(scopedAccount, callerAccount, StringComparison.Ordinal);
        }

        if (!string.Equals(scopeKey, DefaultScopeKey, StringComparison.Ordinal)
            && _pinnedAccounts.TryGetValue((DefaultScopeKey, senderBrain.Value), out var defaultAccount))
        {
            return string.Equals(defaultAccount, callerAccount, StringComparison.Ordinal);
        }

        if (services.GetService<IBrainOwners>() is { } owners)
        {
            var ownerAccount = await owners.OwnerAccount(senderBrain, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(ownerAccount))
            {
                return string.Equals(ownerAccount, callerAccount, StringComparison.Ordinal);
            }
        }

        if (localScope is { IsSystem: false } target && !string.IsNullOrWhiteSpace(target.Value))
        {
            var match = await TryFindFriendInRosterAsync(target, senderBrain).ConfigureAwait(false);
            if (match is not null && !string.IsNullOrWhiteSpace(match.Account.Value))
            {
                PinAccountFor(target, senderBrain, match.Account.Value);
                if (match.PublicKeys is { } friendKeys)
                {
                    await PinForAsync(target, senderBrain, friendKeys, ct).ConfigureAwait(false);
                }

                return string.Equals(match.Account.Value, callerAccount, StringComparison.Ordinal);
            }
        }

        // Fail closed when no trusted binding exists for the sender brain.
        return false;
    }

    /// <summary>
    /// Resolves pinned public keys for <paramref name="remoteBrain"/> scoped to <paramref name="localBrain"/>,
    /// with cold-start read-through to <c>IFriends(localBrain).List()</c> under module scope even when <c>callers.Current</c> is <see langword="null"/>.
    /// </summary>
    public async ValueTask<BrainPublicKeys?> GetPinnedKeysForAsync(
        BrainId localBrain,
        BrainId remoteBrain,
        CancellationToken ct = default)
    {
        var scopeBrain = ResolveScopeBrain(localBrain);
        var scopeKey = ScopeKey(scopeBrain);

        if (_scopedPins.TryGetValue(scopeKey, out var scopedDir)
            && await scopedDir.GetPinnedKeysAsync(remoteBrain, ct).ConfigureAwait(false) is { } scopedKeys)
        {
            return scopedKeys;
        }

        if (!string.Equals(scopeKey, DefaultScopeKey, StringComparison.Ordinal)
            && _scopedPins.TryGetValue(DefaultScopeKey, out var defaultDir)
            && await defaultDir.GetPinnedKeysAsync(remoteBrain, ct).ConfigureAwait(false) is { } defaultKeys)
        {
            return defaultKeys;
        }

        if (scopeBrain is not { IsSystem: false } target || string.IsNullOrWhiteSpace(target.Value))
        {
            return null;
        }

        var match = await TryFindFriendInRosterAsync(target, remoteBrain).ConfigureAwait(false);
        if (match?.PublicKeys is not { } friendKeys)
        {
            return null;
        }

        await PinForAsync(target, remoteBrain, friendKeys, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(match.Account.Value))
        {
            PinAccountFor(target, remoteBrain, match.Account.Value);
        }

        return friendKeys;
    }

    /// <summary>Pins <paramref name="publicKeys"/> for <paramref name="remoteBrain"/> scoped to <paramref name="localBrain"/>.</summary>
    public ValueTask PinForAsync(BrainId? localBrain, BrainId remoteBrain, BrainPublicKeys publicKeys, CancellationToken ct = default)
    {
        var dir = _scopedPins.GetOrAdd(ScopeKey(localBrain), static _ => new InMemoryPinnedBrainKeyDirectory());
        return dir.PinAsync(remoteBrain, publicKeys, ct);
    }

    /// <summary>Rotates pinned keys for <see cref="BrainKeyRotationProof.BrainId"/> scoped to <paramref name="localBrain"/>.</summary>
    public ValueTask RotateForAsync(BrainId? localBrain, BrainKeyRotationProof proof, CancellationToken ct = default)
    {
        var dir = _scopedPins.GetOrAdd(ScopeKey(localBrain), static _ => new InMemoryPinnedBrainKeyDirectory());
        return dir.RotateAsync(proof, ct);
    }

    /// <inheritdoc />
    public ValueTask<BrainPublicKeys?> GetPinnedKeysAsync(BrainId brainId, CancellationToken ct = default)
        => GetPinnedKeysForAsync(ResolveActiveLocalBrain() ?? default, brainId, ct);

    /// <inheritdoc />
    public ValueTask PinAsync(BrainId brainId, BrainPublicKeys publicKeys, CancellationToken ct = default)
        => PinForAsync(ResolveActiveLocalBrain(), brainId, publicKeys, ct);

    /// <inheritdoc />
    public ValueTask RotateAsync(BrainKeyRotationProof proof, CancellationToken ct = default)
        => RotateForAsync(ResolveActiveLocalBrain(), proof, ct);

    private async Task<Friend?> TryFindFriendInRosterAsync(BrainId localBrain, BrainId remoteBrain)
    {
        var proxies = services.GetService<INeuronProxies>();
        var edge = services.GetService<EdgeAuthority>();
        if (proxies is null)
        {
            return null;
        }

        try
        {
            using var scope = edge?.Enter(edge.Module(IdentityModule.Id, localBrain));
            var friends = await proxies.Get<IFriends>(FriendsDefaults.Id(localBrain.Value)).List().ConfigureAwait(false);
            return friends.FirstOrDefault(friend => friend.Brain == remoteBrain);
        }
        catch (InvalidOperationException)
        {
            // Minimal host without friends.links registered: fail closed (return null).
            return null;
        }
    }

    private BrainId? ResolveActiveLocalBrain()
    {
        if (callers?.Current?.Brain is { IsSystem: false } active && !string.IsNullOrWhiteSpace(active.Value))
        {
            return active;
        }

        if (options?.Value.LocalBrainId is { Length: > 0 } configured)
        {
            return new BrainId(configured);
        }

        return null;
    }

    private BrainId? ResolveScopeBrain(BrainId explicitBrain)
    {
        if (!explicitBrain.IsSystem && !string.IsNullOrWhiteSpace(explicitBrain.Value))
        {
            return explicitBrain;
        }

        return ResolveActiveLocalBrain();
    }

    private static string ScopeKey(BrainId? localBrain)
        => localBrain is { IsSystem: false } b && !string.IsNullOrWhiteSpace(b.Value) ? b.Value : DefaultScopeKey;
}

/// <summary>
/// Resolves <see cref="HttpBrainRelayClient"/> when <see cref="BrainRelayOptions.RelayBaseUri"/> is configured,
/// and degrades cleanly to unconfigured (<see cref="IsConfigured"/> = <see langword="false"/>) on local-only boots.
/// Supports both Cloud <c>ServiceAssertion</c> auth and desktop/home-server <c>Bearer</c> + <c>X-Machine-Proof</c> fallback.
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

    private ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
        => CloudRelayRequestSigner.AuthorizeServiceOrBearerAsync(
            request,
            _assertionSigner,
            _callers?.Current?.Brain.Value ?? _options.Value.LocalBrainId,
            RelayPurpose,
            _options.Value.BearerToken,
            _options.Value.MachineProofFactory,
            ct);
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

        services.TryAddSingleton<KernelBrainKeyDirectory>(static sp =>
            new KernelBrainKeyDirectory(
                sp,
                sp.GetService<ICallerAmbient>(),
                sp.GetService<IOptions<BrainRelayOptions>>()));
        services.TryAddSingleton<IBrainKeyDirectory>(static sp =>
            sp.GetRequiredService<KernelBrainKeyDirectory>());

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
            var kernelDirectory = sp.GetService<KernelBrainKeyDirectory>();
            var edge = sp.GetRequiredService<EdgeAuthority>();
            var time = sp.GetService<TimeProvider>() ?? TimeProvider.System;
            var replayCache = sp.GetRequiredService<BrainEnvelopeReplayCache>();
            var skew = opts.MaxClockSkew > TimeSpan.Zero ? opts.MaxClockSkew : BrainEnvelopeSealer.DefaultMaxClockSkew;
            BrainId? localBrain = !string.IsNullOrWhiteSpace(opts.LocalBrainId) ? new BrainId(opts.LocalBrainId) : null;

            return new BrainEnvelopeReceiver(
                isLocalBrain: brain => !brain.IsSystem && (localBrain is null || brain == localBrain.Value),
                signer: signer,
                scopedKeyLookup: kernelDirectory is not null
                    ? kernelDirectory.GetPinnedKeysForAsync
                    : (_, sender, ct) => directory.GetPinnedKeysAsync(sender, ct),
                edge: edge,
                invoke: (target, call, ct) => InvokeLocalOrleansAsync(sp, target, call, ct),
                post: (target, call) => PostLocalOrleansAsync(sp, target, call),
                timeProvider: time,
                maxClockSkew: skew,
                scopedVerifyCallerAccount: kernelDirectory is not null
                    ? kernelDirectory.VerifyCallerAccountForAsync
                    : static (_, _, _, _) => ValueTask.FromResult(false),
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
