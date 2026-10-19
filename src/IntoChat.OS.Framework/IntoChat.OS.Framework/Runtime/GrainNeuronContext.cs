using IntoChat.OS.Framework.Compute;
using IntoChat.OS.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Framework;

// The runtime context of a neuron that is still an Orleans grain (clean-core PR 4). Each member maps a neutral
// SPI call onto the Orleans feature neurons use today; the Orleans provider (PR 5) takes this over.
internal sealed class GrainNeuronContext(Grain grain, Action<TimeSpan> delayDeactivation, Action deactivateOnIdle, ISignalFanout fanout) : INeuronContext
{
    private GrainTimers? _timers;

    // The grain id's type and key are exactly the address's neuron type and key, so StorageKey is GrainId.ToString().
    public static NeuronAddress AddressOf(GrainId id) => new(id.Type.ToString()!, id.Key.ToString()!);

    public NeuronAddress Address => AddressOf(grain.GetGrainId());

    public string Key => Address.Key;

    public IServiceProvider Services => ((IGrainBase)grain).GrainContext.ActivationServices;

    public TimeProvider Time => Services.GetService<TimeProvider>() ?? TimeProvider.System;

    public INeuronTimers Timers => _timers ??= new GrainTimers(grain);

    public ICallerContextCarrier Caller { get; } = new RequestCallerCarrier();

    public ITraceCarrier Trace { get; } = new TraceCarrier();

    public ISignalFanout Fanout => fanout;

    public INeuronResolver Resolver => Services.GetRequiredService<INeuronResolver>();

    public T Self<T>() where T : class => grain.AsReference<T>();

    // DelayDeactivation(TimeSpan.Zero) is how neurons cancel an earlier delay today (TimerNeuron.Stop).
    public void KeepAlive(TimeSpan? duration) => delayDeactivation(duration ?? TimeSpan.Zero);

    public void Deactivate() => deactivateOnIdle();

    internal sealed class RequestCallerCarrier : ICallerContextCarrier
    {
        public IDisposable Suppress()
        {
            var stamped = RequestContext.Get(CallerContextStamper.RequestContextKey);
            RequestContext.Remove(CallerContextStamper.RequestContextKey);
            return new Restore(stamped);
        }

        private sealed class Restore(object? stamped) : IDisposable
        {
            public void Dispose()
            {
                if (stamped is null) { RequestContext.Remove(CallerContextStamper.RequestContextKey); }
                else { RequestContext.Set(CallerContextStamper.RequestContextKey, stamped); }
            }
        }
    }

    internal sealed class TraceCarrier : ITraceCarrier
    {
        public Trace? Current => TraceContext.Current;

        public IDisposable Enter(Trace trace) => TraceContext.Enter(trace);
    }

    // The registry is locked: an interleaving turn or tick may start or stop a timer while another does.
    // Timers are grain timers that neither keep the activation alive nor interleave unless asked; reminders wait
    // for the provider.
    private sealed class GrainTimers(Grain grain) : INeuronTimers
    {
        private readonly Dictionary<string, IGrainTimer> _running = new(StringComparer.Ordinal);

        public IDisposable Start(string name, TimeSpan due, TimeSpan? period, Func<CancellationToken, Task> tick, bool interleave = false, bool keepAlive = false)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(tick);
            var timer = grain.RegisterGrainTimer(tick, new GrainTimerCreationOptions
            {
                DueTime = due,
                Period = period ?? Timeout.InfiniteTimeSpan,
                Interleave = interleave,
                KeepAlive = keepAlive,
            });
            IGrainTimer? previous;
            lock (_running)
            {
                _running.Remove(name, out previous);
                _running[name] = timer;
            }
            previous?.Dispose();
            return new Stop(this, name, timer);
        }

        // Framework does not reference the Orleans reminder service; neurons keep IReminderRegistry until the
        // provider (PR 5) maps reminders.
        public Task Remind(string name, TimeSpan due, TimeSpan period) =>
            throw new NotSupportedException("Reminders are mapped by the Orleans provider; use IReminderRegistry until then.");

        public Task Forget(string name) =>
            throw new NotSupportedException("Reminders are mapped by the Orleans provider; use IReminderRegistry until then.");

        private sealed class Stop(GrainTimers owner, string name, IGrainTimer timer) : IDisposable
        {
            public void Dispose()
            {
                lock (owner._running)
                {
                    if (owner._running.TryGetValue(name, out var current) && ReferenceEquals(current, timer)) { owner._running.Remove(name); }
                }
                timer.Dispose();
            }
        }
    }
}
