using DigitalBrain;
using DigitalBrain.Access;
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
    private readonly Func<NeuronId, IMcpServer> _resolveServer;
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
        ILogger<McpServersBootstrap>? logger = null)
        : this(services, options, lifetime, brains, logger, resolveServer: null, retryBackoff: null)
    {
    }

    internal McpServersBootstrap(
        IServiceProvider services,
        IOptions<McpModuleOptions> options,
        IHostApplicationLifetime? lifetime,
        IBrainObserver? brains,
        ILogger<McpServersBootstrap>? logger = null,
        Func<NeuronId, IMcpServer>? resolveServer = null,
        TimeSpan? retryBackoff = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        _services = services;
        _options = options;
        _lifetime = lifetime;
        _brains = brains;
        _logger = logger;
        _resolveServer = resolveServer ?? (id => SiloNeurons.Get<IMcpServer>(services, id));
        _retryBackoff = retryBackoff ?? DefaultBackoff;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Value.Servers is null || _options.Value.Servers.Count == 0)
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

        var servers = _options.Value.Servers;
        if (servers is null || servers.Count == 0)
        {
            return;
        }

        var reconciled = new HashSet<(string Brain, string Server)>();
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

        var id = new NeuronId(brain, "mcp.server", "mcp/" + name);
        var server = _resolveServer(id);
        var state = await server.Read().ConfigureAwait(false);

        if (!serverOptions.Enabled)
        {
            if (state.Connected)
            {
                await server.Disconnect(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (!state.Connected || !string.Equals(state.Url, serverOptions.Url, StringComparison.Ordinal))
        {
            var title = string.IsNullOrWhiteSpace(serverOptions.Title) ? name : serverOptions.Title.Trim();
            await server.Connect(new McpServerSpec(title, serverOptions.Url), cancellationToken).ConfigureAwait(false);
        }
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
