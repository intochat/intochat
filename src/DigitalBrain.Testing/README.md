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

## TestOS

`TestOS.Local()` hosts neurons and apps in the test process, with no Orleans, no network and a clock that moves only when told
([TestOS.cs](TestOS/TestOS.cs)). Turns run one at a time per neuron; `[On]` reactions arrive ordered per source and deduped.

```csharp
await using var os = TestOS.Local()
    .With<IStorageLike>("drive", new FakeStorage("drive"))
    .With<IStorageLike>("c", new FakeStorage("c", used: 10, total: 100));
var files = await os.Install<Files>();

await files.Request("open");
await os.Idle();

Assert.Contains(files.Side.Entries, e => e.Id == "c" && e.Trailing == "10 GB of 100 GB");
```

### Capability fakes

- `With<TCapability>(name, fake)` binds a fake to a `[Capability(name, version)]` interface; `With<TCapability>(fake)` uses the
  capability name. A second binding of the same contract and name throws, and so does a contract without `[Capability]`.
- Neurons and apps take bound fakes through their constructors: `TCapability` for the only binding, `IReadOnlyList<TCapability>`
  for all of them. `ICapabilityBroker` resolves from the same fakes.
- Nothing resolves unless it is bound: an unbound capability throws `CapabilityNotBoundException` naming `name@version`
  ([CapabilityFakes.cs](TestOS/Capabilities/CapabilityFakes.cs)).
- Capability-specific helpers such as `WithStorage` ship with their contracts as extension methods over `With<TCapability>`.

### Installing an app

- `os.Install<TApp>()` reads the app's manifest: its static `AppManifest Manifest` property, or else the `[App]` id, the capability
  contracts its constructor takes and the `[Neuron]` classes in its namespace. A required capability with no bound fake fails the
  install with the capability named, before anything is registered ([TestOSAppExtensions.cs](TestOS/Apps/TestOSAppExtensions.cs)).
- `app.Request(operation, argument)` runs the `[On]` method of that name (case-insensitive) as a turn. A null argument is the empty
  input. `app.Operations` lists the operations, so a baseline test can request each one.
- `os.Idle()` completes when no request, turn, reaction or deferred work is left.

### Presentation

An app publishes through the `DigitalBrain.UI.IPresenter` it takes in its constructor, or through public `Side` and `Main` properties
that are read after every turn. The latest of each wins ([PresentationAssertions.cs](TestOS/Presentation/PresentationAssertions.cs)).

- `app.Side` and `app.Main` are `PresentedView`s: `IsSet`, `Title`, `Entries`, `Texts`, `Fields`, `Ids`, `Find(id)` and `Entry(id)`.
- `app.Toasts` lists the toast messages; `app.Presentation` is the raw `AppPresentation`; `app.PresentationError` holds what a
  `Side` or `Main` property threw.
- `Find` throws `PresentationAssertionException` listing the ids that exist. The accessors return plain values, so any assertion
  library works.
