using System.Reflection;
using System.Text.Json;
using DigitalBrain.Contracts;
using DigitalBrain.Invocation;
using DigitalBrain.Kernel;
using DigitalBrain.Neurons;
using DigitalBrain.UI;
using DigitalBrain.Units;

namespace DigitalBrain;

/// <summary>A screen: a neuron whose state has one stored view. The view's alias is the neuron type.</summary>
public abstract class Screen<TState> : Neuron<TState>, IScreen where TState : class, new()
{
    private PublishedView? _view;
    private Dictionary<string, SlotEntry> _children = new(StringComparer.Ordinal);
    private Dictionary<string, SlotEntry>? _rendering;
    private string _cause = "reaction";

    /// <summary>The alias the view is stored under and the owner its emits name: the neuron type.</summary>
    protected string Alias => Id.Type;

    /// <summary>The view of <paramref name="state"/>. It runs inside the save, so it cannot await: what it shows is in the state.</summary>
    protected abstract View Render(TState state);

    /// <summary>Saves <paramref name="next"/> and its view as one journal entry; <paramref name="facts"/> follow <see cref="StateChanged"/>.</summary>
    protected Task Show(TState next, params Signal[] facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return Save(next, [new StateChanged(Id.ToString(), _cause), .. facts]);
    }

    /// <summary>The emit that brings <paramref name="signal"/> back to this screen's <c>[On]</c> reaction.</summary>
    protected Emit Emits<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        return new Emit(Alias, SignalAliases.Of(typeof(TSignal)), PooledJson.Encode(signal, typeof(TSignal)));
    }

    /// <summary>The command that brings <paramref name="signal"/> back to this screen's <c>[On]</c> reaction: a <see cref="Button"/> sends it to ".".</summary>
    protected static Command Sends<TSignal>(TSignal signal) where TSignal : Signal
    {
        ArgumentNullException.ThrowIfNull(signal);
        return new Command(".", SignalAliases.Of(typeof(TSignal)), PooledJson.Encode(signal, typeof(TSignal)));
    }

    /// <summary>A neuron of this brain by its contract's neuron type and a brain-relative <paramref name="key"/>.</summary>
    protected T Get<T>(string key) where T : class => Get<T>(IdOf(typeof(T), key));

    /// <summary>Ensures <typeparamref name="TSignal"/> published by that neuron reaches this screen's <c>[On]</c> reaction. Durable and idempotent.</summary>
    protected Task Follow<TSource, TSignal>(string key) where TSource : class where TSignal : Signal
        => Context.Wires.Ensure<TSignal>(IdOf(typeof(TSource), key));

    /// <summary>The view node of this screen's child <paramref name="key"/>, a <typeparamref name="TChild"/> neuron. Rendering it creates nothing; the client's first read of the slot (or a <see cref="Send"/>) does.</summary>
    /// <exception cref="InvalidOperationException">Called outside <see cref="Render"/>, or the key is used twice in one view.</exception>
    /// <exception cref="ArgumentException">The key is empty, ".", ".." or contains '/'.</exception>
    protected NeuronRef Slot<TChild>(string key) where TChild : class => AddSlot<TChild>(key, props: null);

    /// <summary>
    /// The view node of child <paramref name="key"/> with <paramref name="props"/>: the setup this screen decides (label, header,
    /// enabled, content). They are written on the slot node and re-render with this screen; the child stores only what the user
    /// changes. A command sent through this screen to a slot whose props are disabled is refused with <see cref="ControlProps.Disabled"/>.
    /// Rendering it creates nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">Called outside <see cref="Render"/>, or the key is used twice in one view.</exception>
    /// <exception cref="ArgumentException">The key is empty, ".", ".." or contains '/'.</exception>
    /// <exception cref="InvalidOperationException">The props hold a slot: control content is data nodes only, because a slot there would
    /// be routed under the control's key instead of this screen's.</exception>
    protected NeuronRef Slot<TChild, TProps>(string key, TProps props) where TChild : class, IControl<TProps> where TProps : ControlProps
    {
        ArgumentNullException.ThrowIfNull(props);
        return AddSlot<TChild>(key, props);
    }

    private NeuronRef AddSlot<TChild>(string key, ControlProps? props)
    {
        ScreenExtensions.CheckChildKey(key);
        var rendering = _rendering ?? throw new InvalidOperationException($"Slot '{key}' of '{Alias}' is rendered outside Render.");
        var child = new NeuronId(Id.Brain, TypeOf(typeof(TChild)), ScreenExtensions.ChildAddress(Id, key));
        if (!rendering.TryAdd(key, new SlotEntry(child, props))) { throw new InvalidOperationException($"The view of '{Alias}' names slot '{key}' twice."); }
        if (props is null) { return new NeuronRef("slot", child.ToString()); }
        var wire = PooledJson.Encode(props, props.GetType());
        if (HoldsSlot(wire))
        {
            throw new InvalidOperationException($"The props of slot '{key}' of '{Alias}' hold a slot; control content is data nodes only.");
        }
        return new NeuronRef("slot", child.ToString(), wire);
    }

    // True when the JSON holds a neuronRef node of refKind "slot" at any depth.
    private static bool HoldsSlot(JsonElement json) => json.ValueKind switch
    {
        JsonValueKind.Array => json.EnumerateArray().Any(HoldsSlot),
        JsonValueKind.Object => (json.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "neuronRef"
                && json.TryGetProperty("refKind", out var refKind) && refKind.ValueKind == JsonValueKind.String && refKind.GetString() == "slot")
            || json.EnumerateObject().Any(property => HoldsSlot(property.Value)),
        _ => false,
    };

    /// <summary>Sends <paramref name="command"/> to its target: "." is this screen, any other target a child key (optionally "./key") of the current view, or a key path "a/b" resolved hop by hop by each child's own gate.</summary>
    /// <exception cref="ArgumentException">The target key is empty, "..", or contains '/' after the optional "./" (returned as a faulted task).</exception>
    /// <exception cref="ActivationRefusedException">The current view names no slot with that key (<see cref="ActivationRefusedException.UnknownChild"/>).</exception>
    protected Task Send(Command command)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Target);
            ArgumentException.ThrowIfNullOrWhiteSpace(command.Signal);
            if (command.Target == ".") { return Handle(command.Signal, ViewNodes.Payload(command.Args, fieldValue: null, by: null)); }
            var path = command.Target.StartsWith("./", StringComparison.Ordinal) ? command.Target[2..] : command.Target;
            var slash = path.IndexOf('/', StringComparison.Ordinal);
            var key = slash < 0 ? path : path[..slash];
            var rest = slash < 0 ? "." : path[(slash + 1)..];
            ScreenExtensions.CheckChildKey(key);
            if (!_children.TryGetValue(key, out var slot)) { throw new ActivationRefusedException(ActivationRefusedException.UnknownChild); }
            // The parent decides whether a slot is enabled: its props in the current view gate every command routed to the child.
            if (slot.Props is { IsEnabled: false }) { throw new ActivationRefusedException(ControlProps.Disabled); }
            if (rest != "." && rest.Split('/').Any(hop => hop is "" or "." or "..")) { throw new ArgumentException($"Target path '{command.Target}' has an empty, '.' or '..' hop.", nameof(command)); }
            // Each hop is the next parent's Dispatch, which applies its own gate; awaited, so the child's failure reaches the caller.
            return Get<IScreen>(slot.Id).Dispatch(command with { Target = rest });
        }
        catch (Exception error) when (error is ArgumentException or ActivationRefusedException)
        {
            return Task.FromException(error);
        }
    }

    /// <summary>Stores the first view. An override calls the base first.</summary>
    protected override async Task OnActivate(CancellationToken cancellationToken)
    {
        if (_view is not null)
        {
            // A stored legacy view: the slots it names are the current state's.
            _ = RenderValidated(State, out _children);
            return;
        }
        if (ReferenceEquals(_cause, LoadedMarker))
        {
            _cause = "reaction";
            _view = new PublishedView(Alias, 1, RenderValidated(State, out var children));
            _children = children;
            return;
        }
        _cause = "activate";
        try { await Show(State).ConfigureAwait(true); }
        finally { _cause = "reaction"; }
    }

    /// <inheritdoc />
    public Task<PublishedView?> Open() => Read(Alias);

    /// <inheritdoc />
    public Task Act(ScreenAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Activate(action.Alias ?? Alias, action.Generation, action.NodeKey, action.FieldValue, Context.Caller?.ToString());
    }

    /// <inheritdoc />
    public Task<PublishedView?> Read(string? alias = null)
        => Task.FromResult(_view is { } view && (alias is null || string.Equals(view.Alias, alias, StringComparison.Ordinal)) ? view : null);

    /// <inheritdoc />
    public Task Activate(string alias, long generation, string nodeKey, string? fieldValue = null, string? by = null)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(nodeKey);
        if (_view is not { } view || !string.Equals(view.Alias, alias, StringComparison.Ordinal) || view.Generation != generation)
        {
            throw new ActivationRefusedException(ActivationRefusedException.Stale);
        }

        var node = ViewNodes.Find(view.View, nodeKey) ?? throw new ActivationRefusedException(ActivationRefusedException.UnknownNode);
        // A node's command: "." is this screen (the activator stays on the payload), any other target goes the way of Dispatch.
        if (ViewNodes.CommandOf(node) is { } command)
        {
            return command.Target == "."
                ? Handle(command.Signal, ViewNodes.Payload(command.Args, fieldValue, by))
                : Send(command);
        }
        if (ViewNodes.EmitOf(node) is not { } emit)
        {
            throw new ActivationRefusedException(ActivationRefusedException.NoEmit);
        }
        if (!string.Equals(emit.Owner, alias, StringComparison.Ordinal))
        {
            throw new ActivationRefusedException(ActivationRefusedException.ForeignOwner);
        }
        return Handle(emit.Signal, ViewNodes.Payload(emit.Args, fieldValue, by));
    }

    /// <inheritdoc />
    public Task Dispatch(Command command) => Send(command);

    /// <inheritdoc />
    public Task Request(string signal, JsonElement? args = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signal);
        return Handle(signal, ViewNodes.Payload(args, fieldValue: null, by: null));
    }

    private async Task Handle(string alias, JsonElement payload)
    {
        var type = ScreenSignals.Find(GetType(), alias)
            ?? throw new ArgumentException($"Screen '{Alias}' has no [On] reaction for '{alias}'.", nameof(alias));
        var signal = (Signal?)RuntimeProtocol.Deserialize(payload, type)
            ?? throw new ArgumentException($"The payload of '{alias}' is null.", nameof(payload));
        _cause = alias;
        try { _ = await ReactionTable.React(this, signal, CancellationToken.None).ConfigureAwait(true); }
        finally { _cause = "reaction"; }
    }

    private static readonly string LoadedMarker = new("loaded".ToCharArray());

    internal override async Task<TState?> LoadState()
    {
        _cause = "reaction";
        _view = null;
        if (await Seat.Load<System.Text.Json.Nodes.JsonObject>().ConfigureAwait(true) is { } json)
        {
            var rawBytes = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
            if (IsLegacyStoredStateEnvelope(json))
            {
                var legacy = (StoredState<TState>?)RuntimeProtocol.Deserialize(rawBytes, typeof(StoredState<TState>));
                _view = legacy?.View;
                if (legacy?.State is not null && legacy.View is not null) { _cause = LoadedMarker; }
                return legacy?.State;
            }
            var loaded = (TState?)RuntimeProtocol.Deserialize(rawBytes, typeof(TState));
            if (loaded is not null) { _cause = LoadedMarker; }
            return loaded;
        }
        if (await Seat.Load<TState>().ConfigureAwait(true) is { } direct)
        {
            _cause = LoadedMarker;
            return direct;
        }
        var stored = await Seat.Load<StoredState<TState>>().ConfigureAwait(true);
        _view = stored?.View;
        if (stored?.State is not null && stored.View is not null) { _cause = LoadedMarker; }
        return stored?.State;
    }

    internal override Task StoreState(TState next, IReadOnlyList<Signal> signals)
    {
        var wire = RenderValidated(next, out var children);
        var shown = _view is { } current && current.View.GetRawText() == wire.GetRawText()
            ? current
            : new PublishedView(Alias, (_view?.Generation ?? 0) + 1, wire);
        IReadOnlyList<Signal> entry = ReferenceEquals(shown, _view) ? signals : [.. signals, new ViewChanged(Alias, shown.Generation)];
        return SaveAndCacheView(next, entry, shown, children);
    }

    private async Task SaveAndCacheView(TState next, IReadOnlyList<Signal> entry, object shown, Dictionary<string, SlotEntry> children)
    {
        await Seat.Save(next, entry).ConfigureAwait(true);
        _view = (PublishedView)shown;
        _children = children;
        _cause = "reaction";
    }

    private JsonElement RenderValidated(TState state, out Dictionary<string, SlotEntry> children)
    {
        children = new Dictionary<string, SlotEntry>(StringComparer.Ordinal);
        _rendering = children;
        View view;
        try { view = Render(state); }
        finally { _rendering = null; }
        var wire = PooledJson.Encode(view, typeof(View));
        if (System.Text.Encoding.UTF8.GetByteCount(wire.GetRawText()) > UnitLimits.MaxStateBytes)
        {
            throw new InvalidOperationException($"The view of '{Alias}' exceeds the {UnitLimits.MaxStateBytes}-byte cap.");
        }
        if (ViewNodes.DeclaresBy(wire))
        {
            throw new InvalidOperationException($"The view of '{Alias}' declares the host-reserved 'by' in an emit's args.");
        }
        return wire;
    }

    private static bool IsLegacyStoredStateEnvelope(System.Text.Json.Nodes.JsonObject json)
    {
        if (json.Count is 0 or > 2) { return false; }
        var hasState = false;
        var hasView = false;
        foreach (var pair in json)
        {
            if (string.Equals(pair.Key, "state", StringComparison.OrdinalIgnoreCase)) { hasState = true; }
            else if (string.Equals(pair.Key, "view", StringComparison.OrdinalIgnoreCase))
            {
                if (pair.Value is not null && (pair.Value is not System.Text.Json.Nodes.JsonObject viewObj
                    || !viewObj.Any(p => string.Equals(p.Key, "alias", StringComparison.OrdinalIgnoreCase))
                    || !viewObj.Any(p => string.Equals(p.Key, "generation", StringComparison.OrdinalIgnoreCase))))
                {
                    return false;
                }
                hasView = true;
            }
            else { return false; }
        }
        return hasView || (hasState && !typeof(TState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(p => string.Equals(p.Name, "State", StringComparison.OrdinalIgnoreCase)));
    }

    // A child slot of the current view: its address and the props its parent gave it (null for a plain slot).
    private readonly record struct SlotEntry(NeuronId Id, ControlProps? Props);

    private NeuronId IdOf(Type contract, string key) => new(Id.Brain, TypeOf(contract), key);

    private static string TypeOf(Type contract)
        => PlainNeuronContracts.TypeOf(contract)
            ?? contract.GetCustomAttribute<DefaultNeuronTypeAttribute>(inherit: false)?.Type
            ?? contract.GetCustomAttribute<NeuronAliasAttribute>(inherit: false)?.Alias
            ?? contract.GetCustomAttribute<NeuronAttribute>(inherit: false)?.Type
            ?? throw new InvalidOperationException($"{contract.Name} names no neuron type.");
}

/// <summary>Internal snapshot combining user domain state and published view for persistence.</summary>
internal sealed record StoredState<TState>(TState? State, PublishedView? View) where TState : class;
