# DigitalBrain.Testing

Test helpers for DigitalBrain neurons, screens and modules. No Orleans is referenced.
The simulator itself (`TestOS`, the fakes, the in-memory reference implementations) ships in `DigitalBrain.Simulation`, which this package brings in.

## TestOS

`TestOS.Local()` hosts neurons in the test process, with no Orleans, no network and a clock that moves only when told
([TestOS.cs](../DigitalBrain.Simulation/TestOS/TestOS.cs)).

```csharp
await using var os = TestOS.Local();
os.Register(typeof(CalculatorScreen).Assembly);

var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.calculator", "default"));

await screen.Request<CalculatorPress>(new("7"));
await os.Idle();

var view = await screen.Read();
Assert.NotNull(view);
var presented = PresentedView.From(view);
```

Trimmed from `examples/Calculator.Tests.Unit/CalculatorFacts.cs`.

- `os.Register(assembly)` and `os.Register<TNeuron>()` make neuron classes known.
- `os.Get<T>(id)` returns a typed interface to the neuron with that `NeuronId`.
- `os.Idle()` completes when no queued work is left.
- `os.Clock.Advance(by)` moves time and fires due timers.
- `os.Restart()` stops every activation and starts again over the same journal.
- `os.Failures` lists the exceptions neurons threw.

### Capability fakes

- `With<TCapability>(name, fake)` binds a fake to a `[Capability]` interface; `With<TCapability>(fake)` uses the
  capability name. A second binding of the same contract and name throws, and so does a contract without `[Capability]`.
- `os.Services` resolves the bound fakes and an `ICapabilityBroker` over them.
- Nothing resolves unless it is bound: an unbound capability throws `CapabilityNotBoundException`
  ([CapabilityFakes.cs](../DigitalBrain.Simulation/TestOS/Capabilities/CapabilityFakes.cs)).
- `WithStorage`, `WithSecrets` and `WithIdentity` are extension methods over `With<TCapability>`.

### Reading a view

`PresentedView.From(view)` wraps what `IScreen.Read` returns
([PresentationAssertions.cs](../DigitalBrain.Simulation/TestOS/Presentation/PresentationAssertions.cs)).

- It exposes `IsSet`, `Title`, `Nodes`, `Entries`, `Texts`, `Fields`, `Ids`, `Find(id)` and `Entry(id)`.
- `Find` throws `PresentationAssertionException` when the id is missing. The accessors return plain values, so any assertion
  library works.

## Screen baseline

`ScreenBaseline` is the baseline test of a screen: a theory with one row per `[On]` reaction, named `Screen/signal`
([ScreenBaseline.cs](Screens/ScreenBaseline.cs)).

```csharp
public static IEnumerable<TheoryDataRow<ScreenOperationCase>> Operations => ScreenBaseline.Cases(typeof(CalculatorScreen));

[Theory]
[MemberData(nameof(Operations))]
public Task Baseline(ScreenOperationCase operation) => ScreenBaseline.RunAsync(operation);
```

From `examples/Calculator.Tests.Unit/CalculatorFacts.cs`.

Each row starts a fresh `TestOS`, reads the first view, requests the signal with the empty input and reads the view again.
It fails with `ScreenBaselineException` when no view is presented, the request throws, a neuron fails or the row takes longer
than `ScreenBaseline.Budget`. Bind fakes with `ScreenBaseline.Cases(screenType, fakes)`.

## View fixtures

`ViewFixtures.Verify(view, directory)` compares a view with the JSON file stored for it in `directory`.
With `DIGITALBRAIN_UPDATE_VIEWS=1` it writes the file instead. `ViewFixtures.Write` always writes.

## Single-neuron fakes

- `NeuronHarness.Create<TNeuron>(id)` attaches a `FakeNeuronContext` to a new neuron and runs its activation.
- `FakeNeuronContext` records published signals and exposes the fakes below.
- `InMemoryNeuronState`, `ManualTimers`, `ManualReminders`, `RecordingSynapses`, `FakeOperations`,
  `FakeNeuronProxies`, `FakeCapabilityBroker` and `ManualTime` stand in for the runtime services.
- `harness.AssertPublished<TSignal>(predicate)` returns the matching signal or fails. `AssertNothingPublished()` is the opposite check.

## In-process host

`SiloHost.Local()` starts a whole brain host inside the test process. The host itself ships in the
`DigitalBrain.Runtime.Orleans` package; a test project that calls `SiloHost.Local()` must reference it.
Without that reference the call fails with a message naming the package.

`BothRuntimesFixture` in `DigitalBrain.Testing.Silo` runs one fact body on `TestOS` and on that host.

## Module floor

A module's unit test project subclasses `ModuleOnTestOSFacts<TModule>` once. It checks that every `[Neuron]` class of the
module registers on `TestOS` and that every contract binds.

## Storage: conformance kit and fake

A module providing `storage@1` proves it with a sealed subclass of
`StorageConformance<TStorage>` ([StorageConformance.cs](Conformance/Storage/StorageConformance.cs)):

- `Connect(StorageSeed seed, ct)` returns a `StorageSubject` holding the storage, an `IOperationsProbe` and optional
  `HostileIds`. The seed is `StorageSeed.Canonical`. Override `SeedFolder` to place it elsewhere.
- `MissingId()` may be overridden with a module-shaped unknown id.
- Facts for optional actions skip, with a reason, only when `StorageDescriptor.Flags` does not offer the action.

`InMemoryStorage` (with `InMemoryOperations`) is the reference implementation that passes the kit. `FakeStorage` is the same
storage plus a `Calls` record; bind it with `os.WithStorage("drive", new FakeStorage("Work Drive"))` or
`os.WithStorage("c", label: "C:", configure: fake => fake.AddFile(fake.Root, "a.txt", bytes))`.

## Secrets, identity and connections: conformance kits and fakes

`secrets@1` and `identity@1` take no caller parameter; the implementation reads the caller the runtime stamped. Both kits
switch that caller through a `CallerScope` the subclass supplies (`As(Caller)` stamps a caller until disposed);
`AmbientCallerScope` is the scope the in-memory subjects read, and `EdgeCallerScope` stamps through a TestOS or silo `EdgeAuthority`.

- `SecretsConformance<TSecrets>`: `Connect(ct)` returns a `SecretsSubject`. Override `Owner` and `Stranger` if your callers
  need provisioned ids.
- `IdentityConformance<TIdentity>`: `Connect(ct)` returns an `IdentitySubject`; its sign-in function is optional.
- `ConnectionsConformance<TConnections>`: `Connect(ct)` returns a `ConnectionsSubject`.

`InMemorySecrets` and `InMemoryIdentity` are the reference implementations. `FakeSecrets` and `FakeIdentity` add a
`Calls` record; bind them with `os.WithSecrets("vault")`, `os.WithIdentity("identity")` or
`os.WithIdentity(name, identity, grants)`.
