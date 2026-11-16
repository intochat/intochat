# Getting started

From nothing to a running, tested app.

## Create an app

```bash
dotnet new install DigitalBrain.Templates
dotnet new digitalbrain-app -n MyApp
cd MyApp
dotnet test --project MyApp.Tests.Unit/MyApp.Tests.Unit.csproj
```

The tests run on `TestOS`, in the test process. No host, no silo and no network are needed.
The optional `--AppId` sets the app id. It defaults to the lower-case name.

The template writes these files. `AppName` becomes the name you passed with `-n`.

| File | Holds |
| --- | --- |
| `AppName/AppName.cs` | The screen: a `Screen<TState>` class with one `[On]` method per operation |
| `AppName/AppName.spec.md` | The spec comment: description, `Role`, `Shipped`, `Operations` and the scenarios |
| `AppName/CounterNeuron.cs` | A `Neuron<TState>` that keeps durable state |
| `AppName.Tests.Unit/AppNameFacts.cs` | The `ScreenBaseline` theory and one fact per scenario |
| `AppName.Tests.Integration/AppNameRuntimeFacts.cs` | Facts on both runtimes through `BothRuntimesFixture` |

## Read the screen

A screen keeps a state and renders one view from it. `Show` saves the next state and its view together.

```csharp
[Neuron("app.APP_ID")]
public sealed class APP_CLASS : Screen<APP_CLASSState>
{
    [On]
    public Task Open(Opened signal) => Show(new APP_CLASSState("Welcome to APP_CLASS."));

    protected override View Render(APP_CLASSState state) => new("APP_CLASS", new Column([new Text(state.Message)]));
}
```

From `templates/DigitalBrain.Templates/content/digitalbrain-app/AppName/AppName.cs`. The template replaces `APP_ID` and `APP_CLASS`.

## Add an operation

1. Declare a signal: a record with `[Signal]` that derives from `Signal`.
2. Add an `[On]` method that takes it and calls `Show` with the next state.
3. Add it to `Operations` in the spec comment and write a scenario for it.
4. Add one fact for the scenario. `ScreenBaseline` picks up the new `[On]` method by itself.

## Read the real example

`examples/Calculator` is a complete screen with a rule. From the `framework` folder:

```bash
dotnet test --project examples/Calculator.Tests.Unit/Calculator.Tests.Unit.csproj
```

- `examples/Calculator/CalculatorScreen.cs`: three signals, the state and the screen.
- `examples/Calculator/Rules/DivisorNotZero.cs`: a `Rule` the screen checks before it divides.
- `examples/Calculator.Tests.Unit/CalculatorFacts.cs`: the baseline, facts on `TestOS` and a `ViewFixtures` check.
- `examples/Calculator.Tests.Integration/CalculatorRuntimeFacts.cs`: the same behaviour on both runtimes.

## Next

A module starts with `dotnet new digitalbrain-module -n MyModule --ModuleId acme.weather`; see [Modules](modules.md).
Then read [Neurons and synapses](neurons-and-synapses.md), [Apps](apps.md) and [Testing tiers](testing-tiers.md).
