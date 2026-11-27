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

    /// <summary>When <see langword="true"/> and <see cref="RelayBaseUri"/> is configured, starts the background relay receiver loop.</summary>
    public bool EnablePollLoop { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default) and <see cref="RelayBaseUri"/> is not explicitly configured,
    /// automatically binds the desktop <c>link.bin</c> server URI and machine-proof header signing from
    /// <see cref="IDeviceLinkCredentialsProvider"/> without exposing raw tokens or private keys in options.
    /// </summary>
    public bool AutoBindDeviceCredentials { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (default), the background relay receiver connects over WebSocket
    /// <c>GET /circle/v1/brains/stream</c> with automatic HTTP polling fallback when WebSocket upgrade is unavailable.
    /// </summary>
    public bool PreferWebSocket { get; set; } = true;

    /// <summary>
    /// Interval at which <see cref="BrainRelayPollService"/> checks for newly written Desktop <c>link.bin</c>
    /// enrollment when auto-bind is enabled and no relay was configured at boot.
    /// </summary>
    public TimeSpan EnrollmentWatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>True when <see cref="EnablePollLoop"/> was explicitly configured via <see cref="IConfiguration"/>.</summary>
    internal bool EnablePollLoopExplicitlyConfigured { get; set; }
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
/// Resolves <see cref="HttpBrainRelayClient"/> when <see cref="BrainRelayOptions.RelayBaseUri"/> is configured
/// (explicitly, via <see cref="IServiceAssertionSigner"/>, or auto-bound from desktop <c>link.bin</c>),
/// and degrades cleanly to unconfigured (<see cref="IsConfigured"/> = <see langword="false"/>) on local-only boots.
/// Supports Cloud <c>ServiceAssertion</c> auth, explicit <c>Bearer</c> + <c>X-Machine-Proof</c>, and
/// auto-bound desktop <see cref="IDeviceLinkCredentialsProvider"/> header signing.
/// </summary>
public sealed class KernelBrainRelayClientProvider : IBrainRelayClientProvider
{
    internal const string RelayPurpose = "circle-brain-relay";

    private readonly IHttpClientFactory _httpFactory;
    private readonly IOptions<BrainRelayOptions> _options;
    private readonly IServiceAssertionSigner? _assertionSigner;
    private readonly IDeviceLinkCredentialsProvider? _deviceCredentials;
    private readonly TimeProvider _timeProvider;
    private readonly ICallerAmbient? _callers;
    private readonly ILogger<KernelBrainRelayClientProvider>? _logger;

    public KernelBrainRelayClientProvider(
        IHttpClientFactory httpFactory,
        IOptions<BrainRelayOptions> options,
        TimeProvider? timeProvider = null,
        IServiceAssertionSigner? assertionSigner = null,
        ICallerAmbient? callers = null,
        IDeviceLinkCredentialsProvider? deviceCredentials = null,
        ILogger<KernelBrainRelayClientProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(options);
        _httpFactory = httpFactory;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _assertionSigner = assertionSigner;
        _callers = callers;
        _deviceCredentials = deviceCredentials;
        _logger = logger;
    }

    /// <summary>True when a remote relay endpoint is configured.</summary>
    public bool IsConfigured => ResolveBaseUri() is not null;

    /// <summary>The currently resolved relay base URI, or <see langword="null"/> when running local-only.</summary>
    public Uri? RelayBaseUri => ResolveBaseUri();

    /// <summary>True when Desktop <c>link.bin</c> auto-bind can become available dynamically after process start.</summary>
    internal bool CanWatchDynamicEnrollment =>
        _options.Value.AutoBindDeviceCredentials
        && _deviceCredentials is not null
        && _assertionSigner is not { IsConfigured: true };

    IBrainRelayClient? IBrainRelayClientProvider.TryGetClient() => TryGetClient();

    /// <summary>Returns a configured <see cref="HttpBrainRelayClient"/>, or <see langword="null"/> when running local-only.</summary>
    public HttpBrainRelayClient? TryGetClient()
    {
        var baseUri = ResolveBaseUri();
        if (baseUri is null)
        {
            return null;
        }

        var http = _httpFactory.CreateClient(BrainRelayTransportHosting.HttpClientName);
        var clientOptions = new BrainRelayClientOptions
        {
            RecipientsHeaderName = BrainRelayTransportHosting.CircleBlobRecipientsHeader,
            ContentSha256HeaderName = BrainRelayTransportHosting.CircleBlobContentSha256Header,
            CreateAuthRefreshFrameAsync = CreateAuthRefreshFrameAsync,
            RefreshAuthOnHandshakeFailureAsync = RefreshAuthOnHandshakeFailureAsync,
            OnStreamDiagnostic = message => _logger?.LogDebug("Brain relay stream: {Diagnostic}", message),
        };
        return new HttpBrainRelayClient(http, baseUri, AuthorizeAsync, _timeProvider, clientOptions);
    }

    private Uri? ResolveBaseUri()
    {
        var opts = _options.Value;
        if (opts.RelayBaseUri is not null)
        {
            return opts.RelayBaseUri;
        }

        if (_assertionSigner is { IsConfigured: true, ServiceUri: { } uri })
        {
            return uri;
        }

        if (opts.AutoBindDeviceCredentials && _deviceCredentials is not null)
        {
            return _deviceCredentials.TryGetServerUri();
        }

        return null;
    }

    private async ValueTask AuthorizeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var opts = _options.Value;
        var brainId = _callers?.Current?.Brain.Value ?? opts.LocalBrainId;

        if (await CloudRelayRequestSigner.TrySignWithServiceAssertionAsync(
                request,
                _assertionSigner,
                brainId,
                RelayPurpose,
                ct).ConfigureAwait(false))
        {
            return;
        }

        if (await CloudRelayRequestSigner.TrySignWithBearerAsync(
                request,
                opts.BearerToken,
                opts.MachineProofFactory,
                ct).ConfigureAwait(false))
        {
            return;
        }

        if (opts.AutoBindDeviceCredentials
            && _deviceCredentials is not null
            && HasMatchingOrigin(request.RequestUri, _deviceCredentials.TryGetServerUri()))
        {
            await _deviceCredentials.TryAuthorizeRequestAsync(request, ct).ConfigureAwait(false);
        }
    }

    internal async ValueTask RefreshAuthOnHandshakeFailureAsync(Uri httpStreamUri, CancellationToken ct)
    {
        var opts = _options.Value;
        if (string.IsNullOrWhiteSpace(opts.BearerToken)
            && opts.AutoBindDeviceCredentials
            && _deviceCredentials is not null
            && HasMatchingOrigin(httpStreamUri, _deviceCredentials.TryGetServerUri()))
        {
            _ = await _deviceCredentials
                .TryCreateStreamCredentialsAsync(httpStreamUri, forceRefresh: true, ct)
                .ConfigureAwait(false);
        }
    }

    internal async ValueTask<BrainStreamFrame?> CreateAuthRefreshFrameAsync(Uri httpStreamUri, CancellationToken ct)
    {
        var opts = _options.Value;
        var brainId = _callers?.Current?.Brain.Value ?? opts.LocalBrainId;

        if (_assertionSigner is { IsConfigured: true } && !string.IsNullOrWhiteSpace(brainId))
        {
            var token = _assertionSigner.Sign(
                brainId,
                RelayPurpose,
                HttpMethod.Get.Method,
                httpStreamUri.PathAndQuery,
                []);
            return new BrainStreamFrame(BrainStreamFrameTypes.AuthRefresh, ServiceAssertion: token);
        }

        if (string.IsNullOrWhiteSpace(opts.BearerToken)
            && opts.AutoBindDeviceCredentials
            && _deviceCredentials is not null
            && HasMatchingOrigin(httpStreamUri, _deviceCredentials.TryGetServerUri()))
        {
            var creds = await _deviceCredentials
                .TryCreateStreamCredentialsAsync(httpStreamUri, forceRefresh: true, ct)
                .ConfigureAwait(false);
            if (creds is not null)
            {
                return new BrainStreamFrame(
                    BrainStreamFrameTypes.AuthRefresh,
                    AccessToken: creds.AccessToken,
                    MachineProof: creds.MachineProof);
            }
        }

        return null;
    }

    private static bool HasMatchingOrigin(Uri? targetUri, Uri? serverUri)
    {
        if (targetUri is null || serverUri is null || !targetUri.IsAbsoluteUri || !serverUri.IsAbsoluteUri)
        {
            return false;
        }

        return targetUri.Port > 0
            && string.Equals(targetUri.Scheme, serverUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(targetUri.IdnHost, serverUri.IdnHost, StringComparison.OrdinalIgnoreCase)
            && targetUri.Port == serverUri.Port;
    }
}

/// <summary>
/// DI registration helpers for <see cref="SealedRelayBrainTransport"/>, <see cref="BrainEnvelopeReceiver"/>,
/// <see cref="IBrainKeyDirectory"/>, and <see cref="HttpBrainRelayClient"/> in <c>IntoChat.OS.Kernel</c>.
/// </summary>
public static class BrainRelayTransportHosting
{
    public const string HttpClientName = "IntoChatOS.BrainRelay";
    public const string CircleBlobRecipientsHeader = "X-IntoChat-Recipients";
    public const string CircleBlobContentSha256Header = "X-IntoChat-Content-Sha256";

    public static IServiceCollection AddKernelBrainRelayTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<BrainRelayOptions>()
            .Configure<IServiceProvider>(static (options, sp) =>
                sp.GetService<IConfiguration>()?.GetSection(BrainRelayOptions.SectionName).Bind(options))
            .PostConfigure<IServiceProvider>(static (options, sp) =>
            {
                var explicitPollLoop = sp.GetService<IConfiguration>()
                    ?.GetSection(BrainRelayOptions.SectionName)[nameof(BrainRelayOptions.EnablePollLoop)];
                if (!string.IsNullOrWhiteSpace(explicitPollLoop))
                {
                    options.EnablePollLoopExplicitlyConfigured = true;
                }

                if (!options.AutoBindDeviceCredentials || options.RelayBaseUri is not null)
                {
                    return;
                }

                if (sp.GetService<IServiceAssertionSigner>() is { IsConfigured: true })
                {
                    return;
                }

                var serverUri = sp.GetService<IDeviceLinkCredentialsProvider>()?.TryGetServerUri();
                if (serverUri is null)
                {
                    return;
                }

                options.RelayBaseUri = serverUri;

                if (!options.EnablePollLoopExplicitlyConfigured)
                {
                    options.EnablePollLoop = true;
                }
            });
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
                sp.GetService<ICallerAmbient>(),
                sp.GetService<IDeviceLinkCredentialsProvider>(),
                sp.GetService<ILogger<KernelBrainRelayClientProvider>>()));
        services.TryAddSingleton<IBrainRelayClientProvider>(static sp =>
            sp.GetRequiredService<KernelBrainRelayClientProvider>());

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
                replayCache: replayCache,
                includeSenderPublicKeys: IsFriendHandshakeCall);
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
                replayCache: replayCache,
                allowUnpinnedHandshake: IsAllowedUnpinnedFriendHandshake);
        });

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BrainRelayPollService>());
        return services;
    }

    internal static bool IsFriendHandshakeCall(NeuronId target, RuntimeInvocation call, Caller caller)
        => target == FriendsDefaults.Id(target.Brain.Value)
            && string.Equals(call.Method, nameof(IFriends.Accepted), StringComparison.Ordinal)
            && caller.Via is { } via
            && via == FriendsDefaults.Id(caller.Brain.Value);

    internal static bool IsAllowedUnpinnedFriendHandshake(
        BrainEnvelope envelope,
        RemoteInvocationRequest request,
        BrainPublicKeys senderPublicKeys)
        => request.Target == FriendsDefaults.Id(envelope.To.Value)
            && string.Equals(request.Call.Method, nameof(IFriends.Accepted), StringComparison.Ordinal)
            && request.ViaProvenance is { } viaProof
            && viaProof.Via == FriendsDefaults.Id(envelope.From.Value)
            && senderPublicKeys is { SigningKey.Length: > 0, ExchangeKey.Length: > 0 };

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

    internal sealed class BrainRelayPollService(
        IOptions<BrainRelayOptions> options,
        KernelBrainRelayClientProvider clientProvider,
        BrainEnvelopeReceiver receiver,
        TimeProvider timeProvider,
        ILogger<BrainRelayPollService> logger) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var opts = options.Value;
            if (opts.EnablePollLoopExplicitlyConfigured && !opts.EnablePollLoop)
            {
                return;
            }

            if (!opts.EnablePollLoop && !clientProvider.CanWatchDynamicEnrollment)
            {
                return;
            }

            var watchInterval = opts.EnrollmentWatchInterval > TimeSpan.Zero
                ? opts.EnrollmentWatchInterval
                : TimeSpan.FromSeconds(5);

            while (!stoppingToken.IsCancellationRequested)
            {
                var client = clientProvider.TryGetClient();
                if (client is null)
                {
                    if (!clientProvider.CanWatchDynamicEnrollment)
                    {
                        return;
                    }

                    try
                    {
                        await Task.Delay(watchInterval, timeProvider, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                try
                {
                    if (opts.PreferWebSocket)
                    {
                        await client.RunStreamLoopAsync(receiver, stoppingToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await client.RunPollLoopAsync(receiver, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error)
                {
                    logger.LogWarning(error, "Brain relay receiver loop encountered an error; backing off before retry.");
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), timeProvider, stoppingToken).ConfigureAwait(false);
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
