# DigitalBrain

DigitalBrain is the open-source C# framework for building AI-native reactive apps out of neurons, signals and synapses. [IntoChat](../README.md) is a distribution built on it.

## Quick start

```bash
dotnet new install DigitalBrain.Templates
dotnet new digitalbrain-app -n MyApp
cd MyApp
dotnet test --project MyApp.Tests.Unit/MyApp.Tests.Unit.csproj
```

A module starts from the second template. Its id is required:

```bash
dotnet new digitalbrain-module -n MyModule --ModuleId acme.weather
```

[Getting started](docs/getting-started.md) walks through the generated files.

## Example

A screen is a neuron that keeps a state and renders one view from it. Each `[On]` method reacts to one signal.

```csharp
[Signal("calculator.press")]
public sealed record CalculatorPress(string Key = "0") : Signal;

[Neuron("app.calculator")]
public sealed class CalculatorScreen : Screen<CalculatorState>
{
    protected override View Render(CalculatorState state)
        => new("Calculator", new Column([new Text(state.Display)]));

    [On]
    public Task Clear(CalculatorClear signal) => Show(new CalculatorState());

    [On]
    public async Task Press(CalculatorPress signal)
    {
        var (pending, divisor) = State.Pending(signal.Key);
        await Check(nameof(Press), new DivisorNotZero(pending, divisor)).ConfigureAwait(true);
        await Show(State.Apply(signal.Key)).ConfigureAwait(true);
    }
}
```

Trimmed from [`examples/Calculator/CalculatorScreen.cs`](examples/Calculator/CalculatorScreen.cs).

The test runs the screen in the test process, with no host and no network:

```csharp
await using var os = TestOS.Local();
os.Register(typeof(CalculatorScreen).Assembly);

var screen = os.Get<IScreen>(new NeuronId(new BrainId("test"), "app.calculator", "default"));

await screen.Request<CalculatorPress>(new("7"));
await screen.Request<CalculatorPress>(new("+"));
await screen.Request<CalculatorPress>(new("5"));
await screen.Request<CalculatorPress>(new("="));
await os.Idle();

var view = await screen.Read();
Assert.NotNull(view);
var presented = PresentedView.From(view);
Assert.Contains("12", presented.Texts);
```

Trimmed from [`examples/Calculator.Tests.Unit/CalculatorFacts.cs`](examples/Calculator.Tests.Unit/CalculatorFacts.cs).

## Core vocabulary

| Concept | Type or attribute | Purpose |
|---|---|---|
| Neuron | `[Neuron]`, `Neuron<TState>` | An addressable unit with a `NeuronId` (brain, type, key). It runs one turn at a time. `Save` stores the next state and its signals together. |
| Signal | `[Signal]`, `Signal` | An immutable typed fact a neuron publishes. The alias is frozen. |
| Reaction | `[On]` | A method that runs when a signal arrives. |
| Synapse | `[Synapse]`, `ISynapses` | A durable route that carries one signal type from one neuron to another. |
| Screen | `Screen<TState>`, `IScreen` | A neuron whose state has one stored view. `Render` builds the view, `Show` saves the state and the view together. |
| Rule | `Rule` | A named invariant. `Check` refuses the turn when a rule is broken. |
| Module | `[Module]`, `Module`, `ModuleManifest` | A unit of code that declares `Provides`, `Requires`, `Needs`, `Settings` and `Secrets`. |
| Capability | `[Capability]`, `CapabilityId` | A versioned contract such as `storage@1` that a module provides and other code requires. |
| Test kit | `TestOS`, `ScreenBaseline` | `TestOS.Local()` hosts neurons in the test process. `ScreenBaseline` gives one test row per `[On]` reaction of a screen. |

## Packages

| Package | Holds |
|---|---|
| `DigitalBrain` | Neurons, signals, synapses, brains, screens, rules and modules. No Orleans. |
| `DigitalBrain.UI` | The view model: `View`, `Column`, `Row`, `Text`, `Field`, `Button`, `Collection`, `Entry` and more. |
| `DigitalBrain.Analyzers` | Build-time rules for modules, screens and neurons. |
| `DigitalBrain.Simulation` | `TestOS`: neurons on an in-memory journal and a clock that moves only when told. No test framework. |
| `DigitalBrain.Testing` | `ScreenBaseline`, `ViewFixtures`, the capability conformance kits and `SiloHost`. |
| `DigitalBrain.Testing.Silo` | `BothRuntimesFixture`: one fact body on `TestOS` and on the in-process silo. |
| `DigitalBrain.Templates` | The `digitalbrain-app` and `digitalbrain-module` templates. |
| `DigitalBrain.Runtime.Orleans` | The Orleans runtime: topology, storage, reminders, hosting. |
| `DigitalBrain.Runtime.Orleans.Azure` | The cloud topology on Azure storage and Key Vault. |
| `DigitalBrain.Runtime.Http` | The HTTP edge for modules on ASP.NET Core. No Orleans. |
| `DigitalBrain.Runtime.Synapses` | Compiles synapse source to assemblies and loads them. No Orleans. |
| `DigitalBrain.Identity.Local` | Local single-owner identity, secrets vault and connections. No Orleans. |
| `DigitalBrain.Graph` | The brain as one graph, derived from existing state. |
| `DigitalBrain.Capabilities.Storage` | The `storage@1` contract. |
| `DigitalBrain.Capabilities.Secrets` | The `secrets@1` contract. |
| `DigitalBrain.Capabilities.Identity` | The `identity@1` contract. |
| `DigitalBrain.Capabilities.Connections` | The connections contract. |
| `DigitalBrain.Capabilities.Compute` | The compute contract: costs, funding and metering. |
| `DigitalBrain.Capabilities.Http` | The `http@1` contract. |
| `DigitalBrain.Capabilities.Tools` | The `tool@1` contract. |

## Documentation

- [Framework documentation](docs/README.md)
- [Contributing](CONTRIBUTING.md)

## License

[MIT](LICENSE).
