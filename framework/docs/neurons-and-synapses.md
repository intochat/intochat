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

## Signals

A signal is a record that derives from `Signal`. `[Signal("alias")]` gives it the frozen alias the journal stores.
Published signals reach subscribers under the [delivery contract](delivery-contract.md).

## Reactions and synapses

A reaction is a neuron method marked `[On]`. It takes one signal and runs when that signal arrives.

A synapse is a durable route: signals of one type published by one neuron reach another neuron.
A neuron manages the synapses it holds through `ISynapses` (`Context.Synapses`):

- `Ensure<TSignal>(from)` adds a route from one neuron. `Remove(from)` drops it.
- `EnsureBrainWide(signals)` hears the signal types from every other neuron of the brain.
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

From `tests/DigitalBrain.Runtime.Synapses.Tests.Unit/Compile/SynapseCompilerFacts.cs`.

## What the build and the host check

`SynapseCompiler` in `DigitalBrain.Runtime.Synapses` compiles synapse source and runs the analyzers:

- `DB0041`: every `[Synapse]` is a reaction or a view.
- `DB0042`: a synapse's state record is declared in its own assembly.
- `DB0043` (warning): state carries no status-like member.
- `DB0044`: no `[On]` reactions in a synapse assembly.
- `DB0045`: flags banned APIs at the line (network, disk, processes, environment, reflection, native code, `dynamic`, thread pool).

The analyzer is advice. The boundary is the host: `UnitLoader` checks every build before it loads it into a collectible load context.

- Every type and member the build references must be on the allow-list `AllowedApis`, or be an interface marked `[Capability]`.
- The build may declare no P/Invoke and no explicit-layout types.
- A refused build fails the deploy with a `DB0045` diagnostic.

The allow-list in short (the exact list is `AllowedApis` in `src/DigitalBrain.Runtime.Synapses/Host/AllowedApis.cs`):

- Whole namespaces: `System`, `System.Collections`, `System.Collections.Generic`, `System.Collections.Immutable`,
  `System.Collections.Frozen`, `System.Linq`, `System.Text`, `System.Text.Json` (with `.Serialization` and `.Nodes`),
  `System.Text.RegularExpressions`, `System.Globalization`, `System.Numerics`, `System.Buffers`.
- DigitalBrain: `DigitalBrain`, `DigitalBrain.Rules`, `DigitalBrain.Time`, `DigitalBrain.UI`, `DigitalBrain.Capabilities` and
  `DigitalBrain.Capabilities.Compute`, `.Connections`, `.Http`, `.Identity`, `.Secrets`, `.Storage`, `.Tools`.
  Every other DigitalBrain namespace is closed.
- Single types such as `Task`, `ValueTask`, `CancellationToken` and what the compiler emits are allowed member by member
  (`MemberAllowList`). A member a later .NET adds is refused until it is ruled on.

A synapse function reaches the outside only through a `[Slot]` capability.
