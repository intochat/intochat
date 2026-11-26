using System.Threading.Channels;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Log;
using IntoChat.OS.Host.Logging;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Modules.Devices;

// Per-process install runner: executes strictly one model download at a time across all brains on this machine,
// deduplicating concurrent requests for the same model and reporting progress back through IDeviceInstallReports.
internal sealed class DeviceInstaller(EdgeAuthority? edge = null) : IHostedService, IDisposable
{
    private sealed record Target(NeuronId DeviceId, IDeviceInstallReports Reports);

    private sealed class Job(ModelId model, ILocalRuntime runtime, Target initialTarget, CancellationToken shutdownToken)
    {
        public ModelId Model { get; } = model;
        public ILocalRuntime Runtime { get; } = runtime;
        public List<Target> Targets { get; } = [initialTarget];
        public CancellationTokenSource Cts { get; } = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        public bool Running { get; set; }
        public bool Completed { get; set; }
        public bool CancelledByUser { get; set; }
        public bool CancelledByShutdown { get; set; }
        public InstallProgress? LastProgress { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly Channel<Job> _channel = Channel.CreateUnbounded<Job>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<ModelId, Job> _active = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;
    private Job? _current;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureWorkerStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? worker;
        lock (_gate)
        {
            foreach (var job in _active.Values)
            {
                job.CancelledByShutdown = true;
                job.Cts.Cancel();
            }
            _active.Clear();
            _current = null;
            worker = _worker;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();

        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    // Enqueues a model install (or attaches this device neuron to an already-queued/running job for the same model).
    // Returns true when this model immediately claims the active download slot.
    internal bool Enqueue(NeuronId deviceId, ModelId model, ILocalRuntime runtime, IDeviceInstallReports reports)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(reports);

        InstallProgress? replay = null;
        bool activeNow;
        lock (_gate)
        {
            EnsureWorkerStartedLocked();

            if (_active.TryGetValue(model, out var existing) && !existing.Completed)
            {
                if (!existing.Targets.Any(t => t.DeviceId == deviceId))
                {
                    existing.Targets.Add(new Target(deviceId, reports));
                    replay = existing.LastProgress;
                }
                activeNow = existing.Running || ReferenceEquals(_current, existing);
            }
            else
            {
                var job = new Job(model, runtime, new Target(deviceId, reports), _shutdown.Token);
                _active[model] = job;
                activeNow = _current is null && _active.Count == 1;
                if (activeNow)
                {
                    job.Running = true;
                    _current = job;
                }
                _channel.Writer.TryWrite(job);
            }
        }

        if (replay is not null)
        {
            _ = DeliverAsync([new Target(deviceId, reports)], replay);
        }

        return activeNow;
    }

    internal bool IsRunning(ModelId model)
    {
        lock (_gate)
        {
            return _active.TryGetValue(model, out var job) && (job.Running || ReferenceEquals(_current, job));
        }
    }

    // Cancels this device's interest in model; if no other brain is waiting on it, cancels the runtime token synchronously.
    internal bool Cancel(NeuronId deviceId, ModelId model)
    {
        CancellationTokenSource? toCancel = null;
        Target? detachedTarget = null;
        bool wasRunning = false;

        lock (_gate)
        {
            if (!_active.TryGetValue(model, out var job) || job.Completed)
            {
                return false;
            }

            wasRunning = job.Running || ReferenceEquals(_current, job);
            if (job.Targets.Count <= 1)
            {
                job.CancelledByUser = true;
                toCancel = job.Cts;
            }
            else
            {
                var index = job.Targets.FindIndex(t => t.DeviceId == deviceId);
                if (index >= 0)
                {
                    detachedTarget = job.Targets[index];
                    job.Targets.RemoveAt(index);
                }
            }
        }

        if (toCancel is not null)
        {
            toCancel.Cancel();
        }
        else if (detachedTarget is not null)
        {
            _ = DeliverAsync([detachedTarget], new InstallProgress(model, InstallState.Cancelled, 0, 0, 0));
        }

        return wasRunning;
    }

    // Called when a DeviceNeuron deactivates on host shutdown: detaches its target and stops the runtime
    // without reporting Cancelled/Failed so the persisted queue survives and resumes on next activation.
    internal void DetachOnShutdown(NeuronId deviceId)
    {
        List<CancellationTokenSource>? toCancel = null;
        lock (_gate)
        {
            foreach (var (model, job) in _active.ToArray())
            {
                job.Targets.RemoveAll(t => t.DeviceId == deviceId);
                if (job.Targets.Count == 0)
                {
                    job.CancelledByShutdown = true;
                    _active.Remove(model);
                    if (ReferenceEquals(_current, job))
                    {
                        _current = null;
                    }
                    toCancel ??= [];
                    toCancel.Add(job.Cts);
                }
            }
        }

        if (toCancel is not null)
        {
            foreach (var cts in toCancel)
            {
                cts.Cancel();
            }
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private void EnsureWorkerStarted()
    {
        lock (_gate)
        {
            EnsureWorkerStartedLocked();
        }
    }

    private void EnsureWorkerStartedLocked()
    {
        _worker ??= Task.Run(RunLoopAsync, CancellationToken.None);
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var job in _channel.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                await ExecuteJobAsync(job).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            OsConsoleLog.For<DeviceInstaller>().LogSwallowed(error, "device installer worker loop terminated unexpectedly");
        }
    }

    private async Task ExecuteJobAsync(Job job)
    {
        lock (_gate)
        {
            if (job.CancelledByShutdown)
            {
                job.Completed = true;
                _active.Remove(job.Model);
                if (ReferenceEquals(_current, job))
                {
                    _current = null;
                }
                return;
            }

            if (job.CancelledByUser)
            {
                job.Completed = true;
                _active.Remove(job.Model);
                if (ReferenceEquals(_current, job))
                {
                    _current = null;
                }
                var cancelledTargets = job.Targets.ToArray();
                _ = DeliverAsync(cancelledTargets, new InstallProgress(job.Model, InstallState.Cancelled, 0, 0, 0));
                return;
            }

            job.Running = true;
            _current = job;
        }

        var initialProgress = new InstallProgress(job.Model, InstallState.Downloading, 0, 0, 0);
        await BroadcastAsync(job, initialProgress).ConfigureAwait(false);

        InstallProgress? terminal = null;
        try
        {
            await foreach (var progress in job.Runtime.Install(job.Model, job.Cts.Token).ConfigureAwait(false))
            {
                if (job.CancelledByShutdown || _shutdown.IsCancellationRequested)
                {
                    return;
                }

                await BroadcastAsync(job, progress).ConfigureAwait(false);
                if (progress.State is InstallState.Ready or InstallState.Failed or InstallState.Cancelled)
                {
                    terminal = progress;
                    break;
                }
            }

            if (terminal is null && !job.CancelledByShutdown && !_shutdown.IsCancellationRequested)
            {
                var synthetic = job.CancelledByUser
                    ? new InstallProgress(job.Model, InstallState.Cancelled, job.LastProgress?.Percent ?? 0, job.LastProgress?.Bytes ?? 0, job.LastProgress?.TotalBytes ?? 0)
                    : new InstallProgress(job.Model, InstallState.Failed, job.LastProgress?.Percent ?? 0, job.LastProgress?.Bytes ?? 0, job.LastProgress?.TotalBytes ?? 0, "Install stream ended without reporting completion.");
                await BroadcastAsync(job, synthetic).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (job.CancelledByShutdown || _shutdown.IsCancellationRequested)
        {
            // Host shutdown: leave persisted queue intact for resume on next activation.
        }
        catch (OperationCanceledException) when (job.CancelledByUser)
        {
            await BroadcastAsync(
                job,
                new InstallProgress(job.Model, InstallState.Cancelled, job.LastProgress?.Percent ?? 0, job.LastProgress?.Bytes ?? 0, job.LastProgress?.TotalBytes ?? 0))
                .ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (!job.CancelledByShutdown && !_shutdown.IsCancellationRequested)
            {
                OsConsoleLog.For<DeviceInstaller>().LogSwallowed(error, $"installing model '{job.Model.Value}' on runtime '{job.Runtime.Id.Value}'");
                await BroadcastAsync(
                    job,
                    new InstallProgress(job.Model, InstallState.Failed, job.LastProgress?.Percent ?? 0, job.LastProgress?.Bytes ?? 0, job.LastProgress?.TotalBytes ?? 0, error.Message))
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate)
            {
                job.Running = false;
                job.Completed = true;
                if (_active.TryGetValue(job.Model, out var current) && ReferenceEquals(current, job))
                {
                    _active.Remove(job.Model);
                }
                if (ReferenceEquals(_current, job))
                {
                    _current = null;
                }
            }
            job.Cts.Dispose();
        }
    }

    private Task BroadcastAsync(Job job, InstallProgress progress)
    {
        Target[] targets;
        lock (_gate)
        {
            if (job.CancelledByShutdown)
            {
                return Task.CompletedTask;
            }
            job.LastProgress = progress;
            targets = [.. job.Targets];
        }
        return DeliverAsync(targets, progress);
    }

    private async Task DeliverAsync(Target[] targets, InstallProgress progress)
    {
        foreach (var target in targets)
        {
            try
            {
                using var scope = edge?.Enter(edge.Module(DevicesModule.ManifestName, target.DeviceId.Brain));
                await target.Reports.Report(progress).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                OsConsoleLog.For<DeviceInstaller>().LogSwallowed(error, $"reporting install progress for '{progress.Model.Value}' to '{target.DeviceId}'");
            }
        }
    }
}
