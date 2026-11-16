# DigitalBrain

The vocabulary for writing neurons, screens and modules. Designed to be used by humans and agents.
No runtime and no Orleans: a runtime package hosts what you write here.

## Glossary

- **Neuron**: an addressable unit with a `NeuronId` (brain, type, key) that runs one turn at a time. Classes carry `[Neuron]` and derive from `Neuron<TState>`.
- **Signal**: a typed fact a neuron publishes. Records carry `[Signal]` and derive from `Signal`.
- **Reaction**: an `[On]` method that runs when a signal arrives.
- **Synapse**: a durable route that carries one signal type from one neuron to another (`ISynapses`, `[Synapse]`).
- **Brain**: the scope every neuron lives in, named by a `BrainId`; `BrainId.Kernel` is the reserved system scope.
- **Screen**: a neuron whose state has one stored view (`Screen<TState>`, `IScreen`).
- **Rule**: a named invariant (`Rule`); `Check` refuses the turn when one is broken.
- **Module**: the unit of code that provides capabilities and declares what it needs (`Module`, `[Module]`, `ModuleManifest`).
- **Capability**: a versioned contract, such as `storage@1`, that a module provides and other code requires (`[Capability]`, `CapabilityId`).
- **Need**: a runtime resource a module asks its host for, such as `Need.State` or `Need.HttpClient`.
