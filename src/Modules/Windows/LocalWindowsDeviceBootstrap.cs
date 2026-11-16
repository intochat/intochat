using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Capabilities.Connections;
using DigitalBrain.Capabilities.Connections.Requests;
using DigitalBrain.Kernel;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Framework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Windows;

internal sealed class LocalWindowsDeviceBootstrap(
    IServiceProvider services,
    MachineIdentity identity,
    IOptions<WindowsOptions> options,
    IHostApplicationLifetime? lifetime = null,
    IBrainObserver? brains = null,
    ILogger<LocalWindowsDeviceBootstrap>? logger = null) : IHostedService, IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenRegistration _startedRegistration;
    private Task? _bootstrapTask;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !options.Value.AutoEnrollThisPc)
        {
            return Task.CompletedTask;
        }

        if (lifetime is not null)
        {
            _startedRegistration = lifetime.ApplicationStarted.Register(BeginBootstrap);
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

    public async Task<bool> EnsureEnrolledAsync(string brainId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(brainId);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var edge = services.GetService<EdgeAuthority>();
        var hasHumanUser = DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.TryGet(out var caller)
            && caller.Kind == DigitalBrain.Capabilities.CallerKind.User
            && !DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.IsModelOriginated(caller);
        using var edgeScope = hasHumanUser
            ? null
            : edge?.Enter(edge.Module(WindowsModule.ManifestName, new BrainId(brainId)));
        var key = ConnectionKeys.For(brainId, WindowsModule.ManifestName, identity.Id);
        var connection = SiloNeurons.Get<IConnection>(services, ConnectionKeys.IdFor(key));
        var view = await connection.Read().ConfigureAwait(false);
        if (view.Status == ConnectionStatus.Connected)
        {
            return false;
        }

        var requests = SiloNeurons.Get<IConnectionRequests>(services, IConnectionRequests.Key(brainId));
        _ = await requests.ListConnections().ConfigureAwait(false);

        var result = await connection.Connect("device", identity.Id).ConfigureAwait(false);
        return result.Status == ConnectionStatus.Connected;
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

            _cts = lifetime is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping)
                : new CancellationTokenSource();
            var token = _cts.Token;
            _bootstrapTask = Task.Run(() => RunBootstrapAsync(token), token);
        }
    }

    private async Task RunBootstrapAsync(CancellationToken cancellationToken)
    {
        if (brains is null)
        {
            return;
        }

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string[] brainIds;
                var edge = services.GetService<EdgeAuthority>();
                using (edge?.Enter(edge.RelayedPerson(AccountSession.DefaultLogin, AccountSession.DefaultLogin, new BrainId(AccountSession.DefaultLogin))))
                {
                    brainIds = await brains.Brains(cancellationToken).ConfigureAwait(false);
                }

                foreach (var brainId in brainIds)
                {
                    if (string.IsNullOrWhiteSpace(brainId))
                    {
                        continue;
                    }

                    await EnsureEnrolledAsync(brainId, cancellationToken).ConfigureAwait(false);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
#pragma warning disable CA1031 // Host startup enrollment must tolerate silo warm-up errors and never crash the host
            catch (Exception error)
#pragma warning restore CA1031
            {
                if (attempt == maxAttempts)
                {
                    logger?.LogWarning(error, "Could not auto-enroll Windows device '{MachineId}' after {Attempts} attempts.", identity.Id, maxAttempts);
                    return;
                }

                logger?.LogDebug(error, "Windows device auto-enroll attempt {Attempt} deferred; retrying.", attempt);
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
