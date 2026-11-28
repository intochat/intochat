using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Identity.Local.Machine;
using DigitalBrain.Log;
using DigitalBrain.Runtime.Orleans.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Devices;

// Wakes this machine's owner IDevice on host startup (so any persisted queue resumes) and queues DevicesOptions.Prefetch models.
internal sealed class DevicePrefetch(
    IServiceProvider services,
    MachineId thisMachine,
    IOptions<DevicesOptions> options,
    IHostApplicationLifetime? lifetime = null) : IHostedService, IDisposable
{
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private CancellationTokenRegistration _startedRegistration;
    private Task? _runTask;
    private bool _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (lifetime is not null)
        {
            _startedRegistration = lifetime.ApplicationStarted.Register(BeginPrefetch);
        }
        else
        {
            BeginPrefetch();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _startedRegistration.Dispose();

        CancellationTokenSource? cts;
        Task? runTask;
        lock (_gate)
        {
            cts = _cts;
            runTask = _runTask;
            _cts = null;
            _runTask = null;
        }

        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            cts.Dispose();
        }

        if (runTask is not null)
        {
            try
            {
                await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public void Dispose()
    {
        _startedRegistration.Dispose();

        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            cts = _cts;
            _cts = null;
        }

        cts?.Cancel();
        cts?.Dispose();
    }

    private void BeginPrefetch()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed || _runTask is not null)
            {
                return;
            }
            _cts = new CancellationTokenSource();
            token = _cts.Token;
            _runTask = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        string? machineId = null;
        try
        {
            var brainId = IntoChatOSNames.DefaultOwnerBrainId;
            var edge = services.GetService<EdgeAuthority>();
            using var scope = edge?.Enter(edge.Module(DevicesModule.ManifestName, new BrainId(brainId)));

            machineId = thisMachine.Value;
            var device = SiloNeurons.Get<IDevice>(services, IDevice.Key(brainId, machineId));
            _ = await device.Read().ConfigureAwait(false);

            var hardware = services.GetService<IHardwareProbe>()?.Read();
            foreach (var raw in options.Value.Prefetch)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                var modelId = ResolveFitModel(raw.Trim(), hardware);
                try
                {
                    await device.Install(modelId).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    OsConsoleLog.For<DevicePrefetch>().LogSwallowed(error, $"prefetching model '{modelId.Value}' on device '{machineId}'");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            OsConsoleLog.For<DevicePrefetch>().LogSwallowed(error, $"starting device prefetch for '{machineId ?? "<unknown>"}'");
        }
    }

    private static ModelId ResolveFitModel(string rawId, Hardware? hardware)
    {
        var requested = new ModelId(rawId);
        if (hardware is null)
        {
            return requested;
        }
        var card = KnownModels.Find(rawId);
        if (card is { Bytes: > 0 } && card.Can.HasFlag(Abilities.Text) && FitRule.Of(card.Bytes, hardware) == Fit.WontFit)
        {
            return KnownModels.Qwen35.Id;
        }
        return requested;
    }
}
