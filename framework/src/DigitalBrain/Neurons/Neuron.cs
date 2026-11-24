using DigitalBrain.Components;
using DigitalBrain.Kernel;
using DigitalBrain.Log;
using DigitalBrain.Rules;
using Microsoft.Extensions.Logging;

namespace DigitalBrain.Neurons;

/// <summary>
/// A neuron: a plain class, addressed by <see cref="NeuronId"/>, that reacts to signals one turn at a time.
/// Reactions are <see cref="OnAttribute"/> methods; they receive signals deduped and ordered per source.
/// </summary>
public abstract class Neuron
{
    private INeuronContext? _context;
    private INeuronSeat? _seat;
    private Slots? _slots;

    /// <summary>This neuron's id.</summary>
    protected NeuronId Id => Context.Id;

    /// <summary>What the runtime gives this neuron. Available once attached.</summary>
    protected INeuronContext Context => _context ?? throw new InvalidOperationException("The neuron is not attached to a runtime.");

    /// <summary>Resolves this neuron's declared <c>[Slot]</c> properties against its activation bindings.</summary>
    protected Slots Slots => _slots ??= new Slots(
        ComponentRegistry.Of(GetType()) ?? new ComponentId(Id.Type),
        Context.Bindings,
        new ContextSlotResolver(Context));

    private sealed class ContextSlotResolver(INeuronContext context) : ISlotTargetResolver
    {
        public BrainId Brain => context.Id.Brain;

        public T Resolve<T>(BindingTarget target) where T : class => context.Get<T>(target);

        public T? TryResolve<T>(BindingTarget target) where T : class
        {
            if (target is BindingTarget.Local local && context.ClassOf(local.Neuron.Type) is null)
            {
                return null;
            }
            return context.Get<T>(target);
        }

        public bool Implements<T>(NeuronId id) where T : class
            => id.Brain == context.Id.Brain
                && context.ClassOf(id.Type) is { } type
                && typeof(T).IsAssignableFrom(type);
    }

    // True once a runtime attached this neuron; a detached instance (a preview) has no id.
    internal bool IsAttached => _context is not null;

    private protected INeuronSeat Seat => _seat ?? throw new InvalidOperationException("This context carries no seat: attach through a runtime, TestOS or FakeNeuronContext.");

    internal void Attach(INeuronContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_context is not null) { throw new InvalidOperationException("The neuron is already attached."); }
        _context = context;
        _seat = context as INeuronSeat;
    }

    // Loads persisted state before the first turn; Neuron<TState> overrides it.
    internal virtual Task Load() => Task.CompletedTask;

    internal Task Activate(CancellationToken cancellationToken) => OnActivate(cancellationToken);

    internal Task Deactivate(DeactivationCause cause, CancellationToken cancellationToken) => OnDeactivate(cause, cancellationToken);

    /// <summary>Publishes a signal without a state change. A state change publishes through <c>Save(next, signals)</c>.</summary>
    protected Task Publish(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Context.Publish(signal);
    }

    /// <summary>Publishes an ephemeral signal without persisting to the journal.</summary>
    protected Task PublishEphemeral(Signal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        return Context.PublishEphemeral(signal);
    }

    /// <summary>Publishes Rejected as an ephemeral signal and throws RuleViolationException when any rule is broken.</summary>
    /// <exception cref="RuleViolationException">A rule is broken.</exception>
    protected async Task Check(string operation, params Rule[] rules)
    {
        if (DigitalBrain.Rules.Rules.Judge(operation, rules) is not { } rejected) { return; }
        await PublishEphemeral(rejected).ConfigureAwait(true);
        throw new RuleViolationException(rejected);
    }

    /// <summary>The caller stamped on this turn. A neuron never decides on a missing caller: none is refused.</summary>
    /// <exception cref="UnauthorizedAccessException">The turn carries no caller.</exception>
    protected Access.Caller RequireCaller()
        => Context.Caller ?? throw new UnauthorizedAccessException($"{Id}: the call carries no caller.");

    /// <summary>The caller stamped on this turn when it acts in this neuron's brain; refuses no caller and a caller of another brain.</summary>
    /// <param name="refused">The refusal to throw instead of the default one, for a neuron whose refusals say one thing whatever the reason.</param>
    /// <exception cref="UnauthorizedAccessException">The turn carries no caller, or one of another brain.</exception>
    protected Access.Caller RequireSameBrain(Func<UnauthorizedAccessException>? refused = null)
    {
        if (Context.Caller is not { } caller)
        {
            throw refused?.Invoke() ?? new UnauthorizedAccessException($"{Id}: the call carries no caller.");
        }
        return caller.Brain == Id.Brain
            ? caller
            : throw refused?.Invoke() ?? new UnauthorizedAccessException($"{Id}: a caller of brain {caller.Brain} may not call this.");
    }

    /// <summary>Whether the caller stamped on this turn acts in this neuron's brain; false when the turn carries no caller.</summary>
    protected bool IsSameBrainCaller() => Context.Caller is { } caller && caller.Brain == Id.Brain;

    /// <summary>Whether <paramref name="id"/> names a neuron of this brain whose registered class implements <typeparamref name="TContract"/>; it calls nothing.</summary>
    protected bool Implements<TContract>(NeuronId id) where TContract : class
        => id.Brain == Id.Brain && Context.ClassOf(id.Type) is { } type && typeof(TContract).IsAssignableFrom(type);

    /// <summary>A typed interface to another neuron of the same brain.</summary>
    /// <exception cref="ArgumentException"><paramref name="id"/> belongs to another brain.</exception>
    protected T Get<T>(NeuronId id) where T : class
    {
        if (id.Brain != Id.Brain) { throw new ArgumentException($"Neuron {id} belongs to another brain than {Id}.", nameof(id)); }
        return Context.Get<T>(id);
    }

    /// <summary>Runs <paramref name="work"/> after the current turn ends.</summary>
    protected void Defer(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Context.Defer(work);
    }

    /// <summary>
    /// Decides whether <paramref name="subscriber"/> may hold a synapse from this neuron for <paramref name="signals"/>.
    /// Runtimes call it only through <see cref="NeuronAttachment.Admit"/>, under the subscriber's caller identity, before a synapse is created.
    /// The default admits subscribers of the same brain; an override refuses by throwing <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The subscriber is not admitted.</exception>
    protected virtual Task AdmitSynapse(NeuronId subscriber, IReadOnlyList<Type> signals)
    {
        if (subscriber.Brain != Id.Brain) { throw new UnauthorizedAccessException($"Neuron {subscriber} of another brain may not subscribe to {Id}."); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Decides on <paramref name="request"/>. Override it to authorize by the stamped caller of the asking call; the default runs
    /// <see cref="AdmitSynapse(NeuronId, IReadOnlyList{Type})"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The subscriber is not admitted.</exception>
    protected virtual Task AdmitSynapse(SynapseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AdmitSynapse(request.Subscriber, request.Signals);
    }

    internal Task Admit(SynapseRequest request) => AdmitSynapse(request);

    /// <summary>Runs when the activation starts, after state is loaded.</summary>
    protected virtual Task OnActivate(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs when the activation ends.</summary>
    protected virtual Task OnDeactivate(DeactivationCause cause, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Runs when the named timer or reminder fires. Ticks are turns.</summary>
    protected virtual Task OnReminder(string name, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Returns a host logger for <paramref name="category"/>, falling back to trace logging when no host factory is wired.</summary>
    protected ILogger GetLogger(string? category = null)
    {
        var name = string.IsNullOrWhiteSpace(category) ? GetType().FullName ?? GetType().Name : category;
        return TraceLogger.Resolve(_seat?.Services, name);
    }

    internal Task Remind(string name, CancellationToken cancellationToken) => OnReminder(name, cancellationToken);
}
