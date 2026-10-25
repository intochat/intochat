# DigitalBrain.Testing

Test helpers for DigitalBrain neurons and modules. No runtime is referenced.

## Fakes

- `NeuronHarness.Create<TNeuron>(id)` attaches a `FakeNeuronContext` to a new neuron and runs its activation.
- `FakeNeuronContext` records published signals (`Published`) and exposes the fakes below.
- `InMemoryNeuronState`, `ManualTimers`, `ManualReminders`, `RecordingSynapses`, `FakeOperations`,
  `FakeNeuronProxies`, `FakeCapabilityBroker` and `ManualTime` stand in for the runtime services.
- `harness.AssertPublished<TSignal>(predicate)` fails with the list of recorded signals.

## In-process host

`SiloHost.Local()` starts a whole brain host inside the test process. The host itself ships in the
`DigitalBrain.Runtime.Orleans` package; a test project that calls `SiloHost.Local()` must reference it.
Without that reference the call fails with a message naming the package.

TestOS arrives in a later DigitalBrain release.
