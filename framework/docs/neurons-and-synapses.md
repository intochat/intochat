# Neurons and Synapses

Neurons hold state and publish signals. Synapses carry signals from one neuron to another.

## Neurons

A neuron is a plain class that derives from `Neuron` or `Neuron<TState>` and is marked `[Neuron("type")]`.

- Its id is a `NeuronId`: a brain, a frozen type name and a key, written `brain/type/key`.
- It runs one turn at a time.
- `Neuron<TState>` has persisted state. The state is an immutable record with a parameterless constructor.
- `Save(next, signal)` writes the state and its signals as one journal entry. There is no save without a signal.
- `Publish(signal)` publishes without a state change. `PublishEphemeral(signal)` publishes without writing to the journal.
- `Check(operation, rules)` publishes `Rejected` and throws `RuleViolationException` when a `Rule` is broken.
- `Get<T>(id)` returns a typed interface to another neuron of the same brain.
- `RequireCaller()` returns the caller of the turn and `RequireSameBrain()` returns it when it acts in the neuron's brain; both refuse a turn with no caller, so a neuron never decides on a missing caller. `IsSameBrainCaller()` is false for no caller.
- Its awaits stay on the turn: a bare `await` or `ConfigureAwait(true)`. `ConfigureAwait(false)` inside a neuron, or inside kernel code marked `[TurnBound]`, is the error `DB0050`, because on Orleans the rest of the method would run off the activation's scheduler, beside a call that interleaves with it. A method marked `[ReadOnlyCall, Interleaves]` writes no state.

```csharp
[Neuron("APP_ID.counter")]
public sealed class CounterNeuron : Neuron<CounterState>, ICounter
{
    public async Task<int> Increment()
    {
        var next = State with { Count = State.Count + 1 };
        await Save(next, new Counted(next.Count));
        return next.Count;
    }

    public Task<int> Count() => Task.FromResult(State.Count);
}
```

From `templates/DigitalBrain.Templates/content/digitalbrain-app/AppName/CounterNeuron.cs`.

### State with a lifecycle

When a neuron's state has phases, the phases are a `union` of sealed records. Each phase has only the methods that are legal in it, so an illegal move does not compile.
A method that can say no returns `Outcome<T>`, which is either a `Transition<T>` (the next state and its signals) or a `Refusal`.
A refusal is a value, not an exception. Each refusal has a stable dotted code, and `Refusal.ToRejected` turns it into the published `Rejected`.
`Rule` and `Check` stay for input validation that reports every broken rule at once. Stored unions keep their case under `$case`, and only exact case types can be stored.

## Signals

A signal is a record that derives from `Signal`. `[Signal("alias")]` gives it the frozen alias the journal stores.
Published signals reach subscribers under the [delivery contract](delivery-contract.md).

## Reactions and wires

A reaction is a neuron method marked `[On]`. It takes one signal and runs when that signal arrives.

A wire is a durable route: signals of one type published by one neuron reach another neuron.
A neuron manages the wires it holds through `IWires` (`Context.Wires`):

- `Ensure<TSignal>(from)` adds a route from one neuron. `Remove(from)` drops it.
- `EnsureBrainWide(signals)` hears the signal types from every other neuron of the brain.

A synapse is behavior, not a route: the `[Synapse]` functions below, and in time the other ways to express behavior. The route that carries signals to it is a wire.
- The source decides who may subscribe in `AdmitSynapse`. The default admits neurons of the same brain.

## Synapse functions

A function marked `[Synapse]` is a pure, public static function over one state record. The record is marked `[State("alias")]`.

- A reaction takes a signal and the state and returns the next state, or a `Reaction<TState>` with signals to publish:
  `[Synapse(typeof(TSignal), "alias")]`.
- An alias reaction takes a `SignalEnvelope`, so it needs no reference to the signal type: `[Synapse("signal.alias", "alias")]`.
- A view takes only the state and returns a value to show: `[Synapse("alias")]`.
- A `[Slot]` parameter is a capability the installer connects. Its type is an interface marked `[Capability]`.
- `Every` on a reaction to `Tick` sets a schedule.

```csharp
[Synapse(typeof(Charging), "week")]
public static WeekSpend WeeklyCap(Charging s, WeekSpend t) =>
    t with { Spent = t.Spent + s.Amount };
```

From `tests/DigitalBrain.Tests.Unit/RuntimeSynapses/Compile/SynapseCompilerFacts.cs`.

## What the build and the host check

`SynapseCompiler` in `DigitalBrain.Simulation` (`RuntimeSynapses`) compiles synapse source and runs the analyzers:

- `DB0041`: every `[Synapse]` is a reaction or a view.
- `DB0042`: a synapse's state record is declared in its own assembly.
- `DB0043` (warning): state carries no status-like member.
- `DB0044`: no `[On]` reactions in a synapse assembly.
- `DB0045`: flags banned APIs at the line (network, disk, processes, environment, reflection, native code, `dynamic`, thread pool).

The analyzer is advice. The boundary is the host: `UnitLoader` checks every build before it loads it into a collectible load context.

- Every type and member the build references must be on the allow-list `AllowedApis`, or be an interface marked `[Capability]`.
- The build may declare no P/Invoke and no explicit-layout types.
- A refused build fails the deploy with a `DB0045` diagnostic.

The allow-list in short (the exact list is `AllowedApis` in `src/DigitalBrain.Simulation/RuntimeSynapses/Host/AllowedApis.cs`):

- Whole namespaces: `System`, `System.Collections`, `System.Collections.Generic`, `System.Collections.Immutable`,
  `System.Collections.Frozen`, `System.Linq`, `System.Text`, `System.Text.Json` (with `.Serialization` and `.Nodes`),
  `System.Text.RegularExpressions`, `System.Globalization`, `System.Numerics`, `System.Buffers`.
- DigitalBrain: `DigitalBrain`, `DigitalBrain.Rules`, `DigitalBrain.Time`, `DigitalBrain.UI`, `DigitalBrain.Capabilities` and
  `DigitalBrain.Capabilities.Compute`, `.Connections`, `.Http`, `.Identity`, `.Secrets`, `.Storage`, `.Tools`.
  Every other DigitalBrain namespace is closed.
- Single types such as `Task`, `ValueTask`, `CancellationToken` and what the compiler emits are allowed member by member
  (`MemberAllowList`). A member a later .NET adds is refused until it is ruled on.

A synapse function reaches the outside only through a `[Slot]` capability.
