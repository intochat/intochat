using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Mcp;

internal sealed class McpServersBootstrap : IHostedService, IDisposable
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(500);

    private readonly IServiceProvider _services;
    private readonly IOptions<McpModuleOptions> _options;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly IBrainObserver? _brains;
    private readonly ILogger<McpServersBootstrap>? _logger;
    private readonly IReadOnlyList<IMcpConnectionSeedProvider> _seedProviders;
    private readonly Func<NeuronId, IMcpToolConnection> _resolveToolConnection;
    private readonly Func<NeuronId, IConnection> _resolveConnection;
    private readonly TimeSpan _retryBackoff;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenRegistration _startedRegistration;
    private Task? _bootstrapTask;
    private bool _disposed;

    [ActivatorUtilitiesConstructor]
    public McpServersBootstrap(
        IServiceProvider services,
        IOptions<McpModuleOptions> options,
        IHostApplicationLifetime? lifetime = null,
        IBrainObserver? brains = null,
        ILogger<McpServersBootstrap>? logger = null,
        IEnumerable<IMcpConnectionSeedProvider>? seedProviders = null)
        : this(services, options, lifetime, brains, logger, seedProviders, resolveToolConnection: null, resolveConnection: null, retryBackoff: null)
    {
    }

    internal McpServersBootstrap(
        IServiceProvider services,
        IOptions<McpModuleOptions> options,
        IHostApplicationLifetime? lifetime,
        IBrainObserver? brains,
        ILogger<McpServersBootstrap>? logger = null,
        IEnumerable<IMcpConnectionSeedProvider>? seedProviders = null,
        Func<NeuronId, IMcpToolConnection>? resolveToolConnection = null,
        Func<NeuronId, IConnection>? resolveConnection = null,
        TimeSpan? retryBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _options = options;
        _lifetime = lifetime;
        _brains = brains;
        _logger = logger;
        _seedProviders = seedProviders?.ToArray() ?? [];
        _resolveToolConnection = resolveToolConnection ?? (id => Resolve<IMcpToolConnection>(services, id));
        _resolveConnection = resolveConnection ?? (id => Resolve<IConnection>(services, id));
        _retryBackoff = retryBackoff ?? DefaultBackoff;
    }

    private static T Resolve<T>(IServiceProvider services, NeuronId id) where T : class
        => SiloNeurons.Get<T>(services, id);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var hasServers = _options.Value.Servers is { Count: > 0 };
        if (!hasServers && _seedProviders.Count == 0)
        {
            return Task.CompletedTask;
        }

        if (_lifetime is not null)
        {
            _startedRegistration = _lifetime.ApplicationStarted.Register(BeginBootstrap);
        }
        else
        {
            BeginBootstrap();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _startedRegistration.Dispose();

        Task? bootstrapTask;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _cts;
            bootstrapTask = _bootstrapTask;
            _cts = null;
            _bootstrapTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (bootstrapTask is not null)
        {
            try
            {
                await bootstrapTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    internal Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        Task? task;
        lock (_lock)
        {
            task = _bootstrapTask;
        }

        return task is null ? Task.CompletedTask : task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _startedRegistration.Dispose();

        CancellationTokenSource? cts;
        lock (_lock)
        {
            cts = _cts;
            _cts = null;
            _bootstrapTask = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private void BeginBootstrap()
    {
        lock (_lock)
        {
            if (_disposed || _bootstrapTask is not null)
            {
                return;
            }

            _cts = _lifetime is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(_lifetime.ApplicationStopping)
                : new CancellationTokenSource();
            var token = _cts.Token;
            _bootstrapTask = Task.Run(() => RunBootstrapAsync(token), token);
        }
    }

    private async Task RunBootstrapAsync(CancellationToken cancellationToken)
    {
        if (_brains is null)
        {
            return;
        }

        var servers = _options.Value.Servers ?? new Dictionary<string, McpServerOptions>(StringComparer.OrdinalIgnoreCase);
        if (servers.Count == 0 && _seedProviders.Count == 0)
        {
            return;
        }

        var reconciled = new HashSet<(string Brain, string Server)>();
        var seeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            string[] brainIds;
            try
            {
                var edge = _services.GetService<EdgeAuthority>();
                using (edge?.Enter(edge.RelayedPerson(IntoChatOSNames.DefaultOwnerBrainId, IntoChatOSNames.DefaultOwnerBrainId, new BrainId(IntoChatOSNames.DefaultOwnerBrainId))))
                {
                    brainIds = await _brains.Brains(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Startup MCP bootstrap must tolerate silo warm-up errors and never crash the host
            catch (Exception error)
#pragma warning restore CA1031
            {
                if (attempt == MaxAttempts)
                {
                    _logger?.LogWarning(error, "Could not list brains for MCP server bootstrap after {Attempts} attempts.", MaxAttempts);
                    return;
                }

                _logger?.LogDebug(error, "MCP server bootstrap brain listing attempt {Attempt} deferred; retrying.", attempt);
                if (!await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                continue;
            }

            var anyPending = false;
            foreach (var brainId in brainIds)
            {
                if (string.IsNullOrWhiteSpace(brainId))
                {
                    continue;
                }

                var brain = new BrainId(brainId);
                foreach (var (name, serverOptions) in servers)
                {
                    if (serverOptions is null || reconciled.Contains((brainId, name)))
                    {
                        continue;
                    }

                    try
                    {
                        await ReconcileServerAsync(brain, name, serverOptions, cancellationToken).ConfigureAwait(false);
                        reconciled.Add((brainId, name));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
#pragma warning disable CA1031 // A single unreachable MCP server must not stop startup or other servers
                    catch (Exception error)
#pragma warning restore CA1031
                    {
                        anyPending = true;
                        if (attempt == MaxAttempts)
                        {
                            _logger?.LogWarning(error, "Could not reconcile MCP server '{Server}' for brain '{Brain}' after {Attempts} attempts.", name, brainId, MaxAttempts);
                        }
                        else
                        {
                            _logger?.LogDebug(error, "MCP server '{Server}' for brain '{Brain}' attempt {Attempt} failed; retrying.", name, brainId, attempt);
                        }
                    }
                }
            }

            if (brainIds.Contains(IntoChatOSNames.DefaultOwnerBrainId, StringComparer.Ordinal))
            {
                var ownerBrain = new BrainId(IntoChatOSNames.DefaultOwnerBrainId);
                foreach (var provider in _seedProviders)
                {
                    McpConnectionSeed? seed;
                    try
                    {
                        seed = await provider.ResolveAsync(IntoChatOSNames.DefaultOwnerBrainId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
#pragma warning disable CA1031
                    catch (Exception error)
#pragma warning restore CA1031
                    {
                        _logger?.LogDebug(error, "MCP seed provider {Provider} failed.", provider.GetType().Name);
                        continue;
                    }

                    if (seed is null || string.IsNullOrWhiteSpace(seed.Name) || seeded.Contains(seed.Name))
                    {
                        continue;
                    }

                    try
                    {
                        await ApplySeedAsync(ownerBrain, seed, cancellationToken).ConfigureAwait(false);
                        seeded.Add(seed.Name);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
#pragma warning disable CA1031
                    catch (Exception error)
#pragma warning restore CA1031
                    {
                        anyPending = true;
                        if (attempt == MaxAttempts)
                        {
                            _logger?.LogWarning(error, "Could not seed MCP connection '{Server}' for owner brain after {Attempts} attempts.", seed.Name, MaxAttempts);
                        }
                        else
                        {
                            _logger?.LogDebug(error, "MCP seed '{Server}' attempt {Attempt} failed; retrying.", seed.Name, attempt);
                        }
                    }
                }
            }

            if (!anyPending)
            {
                return;
            }

            if (attempt < MaxAttempts && !await DelayBeforeRetryAsync(attempt, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private async Task ReconcileServerAsync(BrainId brain, string name, McpServerOptions serverOptions, CancellationToken cancellationToken)
    {
        var edge = _services.GetService<EdgeAuthority>();
        using var edgeScope = edge?.Enter(edge.Module(McpModule.ManifestName, brain));

        var id = IMcpToolConnection.NeuronIdOf(brain, name);
        var server = _resolveToolConnection(id);
        var state = await server.ReadState(cancellationToken).ConfigureAwait(false);

        if (!serverOptions.Enabled)
        {
            if (state.Connected)
            {
                await server.DisconnectServer(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var title = string.IsNullOrWhiteSpace(serverOptions.Title) ? name : serverOptions.Title.Trim();
        var allowedChanged = !SameTools(state.AllowedTools, serverOptions.AllowedTools);
        var modeChanged = state.ConfirmationMode != serverOptions.ConfirmationMode;
        if (!state.Connected
            || !string.Equals(state.Url, serverOptions.Url, StringComparison.Ordinal)
            || allowedChanged
            || modeChanged)
        {
            await server.ConnectServer(
                new McpConnectionConfig(
                    serverOptions.Url,
                    title,
                    AllowedTools: serverOptions.AllowedTools,
                    ConfirmationMode: serverOptions.ConfirmationMode),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ApplySeedAsync(BrainId ownerBrain, McpConnectionSeed seed, CancellationToken cancellationToken)
    {
        var edge = _services.GetService<EdgeAuthority>();
        using var edgeScope = edge?.Enter(edge.RelayedPerson(ownerBrain.Value, ownerBrain.Value, ownerBrain));

        var id = IMcpToolConnection.NeuronIdOf(ownerBrain, seed.Name);
        var toolConnection = _resolveToolConnection(id);
        var state = await toolConnection.ReadState(cancellationToken).ConfigureAwait(false);
        if (state.Connected || !string.IsNullOrWhiteSpace(state.Url))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(seed.Token))
        {
            await toolConnection.ConnectServer(seed.Config, cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = SerializeSeedPayload(seed.Config, seed.Token);
        var connection = _resolveConnection(id);
        await connection.Connect("bearer-token", payload).ConfigureAwait(false);
    }

    internal static string SerializeSeedPayload(McpConnectionConfig config, string token)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["url"] = config.Url,
            ["title"] = config.Title,
            ["header"] = config.HeaderName,
            ["prefix"] = config.HeaderPrefix,
            ["token"] = token,
            ["allowedTools"] = config.AllowedTools,
            ["confirmationMode"] = config.ConfirmationMode.ToString(),
        };
        return JsonSerializer.Serialize(payload);
    }

    private static bool SameTools(string[]? left, string[]? right)
    {
        if (left is null && right is null) { return true; }
        if (left is null || right is null) { return false; }
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private async Task<bool> DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        if (_retryBackoff <= TimeSpan.Zero)
        {
            return !cancellationToken.IsCancellationRequested;
        }

        try
        {
            await Task.Delay(TimeSpan.FromTicks(_retryBackoff.Ticks * attempt), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
