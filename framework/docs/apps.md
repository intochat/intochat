# Apps

An app is a class that serves a person through a view and reactions. In the framework an app is a screen: a neuron that derives from `Screen<TState>`.

## The parts

- **State**: an immutable record with a parameterless constructor for the empty state. It belongs to the neuron and survives restarts.
- **Operations**: one signal record per operation, marked `[Signal("app-id.operation")]`.
- **Reactions**: one `[On]` method per operation. It takes the signal and calls `Show(next)`.
- **Rendering**: `Render(state)` returns a `View` built from the nodes in `DigitalBrain.UI`, such as `Column`, `Text` and `Button`.

The class is marked `[Neuron("app.<id>")]`. The neuron type is also the alias the view is stored under.

## How a turn works

1. A signal arrives at an `[On]` reaction.
2. The reaction can call `Check(operation, rules)`. A broken `Rule` publishes `Rejected` and throws `RuleViolationException`; nothing is saved.
3. `Show(next)` saves the next state and its view as one journal entry. It publishes `StateChanged`, and `ViewChanged` when the view differs.
4. `Render` runs inside the save. It cannot await: everything it shows is in the state.

A client reads the view through `IScreen`: `Open()` and `Read()` return the `PublishedView` (alias, generation, view JSON).
`Act(action)` activates a node of the view the client saw. A stale generation is refused.
A node carries an `Emit` built with `Emits(signal)`: activating it brings that signal back to the screen's own `[On]` reaction.
`Follow<TSource, TSignal>(key)` makes a signal of another neuron reach the screen.

## Example

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

Trimmed from `examples/Calculator/CalculatorScreen.cs`. The rule is `examples/Calculator/Rules/DivisorNotZero.cs`.

## The spec and the tests

The spec is a comment at the top of the app file, or an `.spec.md` file beside it. It names `Role`, `Shipped` and `Operations`.
`ScreenBaseline.Cases(typeof(CalculatorScreen))` produces one test case per operation and `ScreenBaseline.RunAsync` runs it on `TestOS.Local()`.
See `examples/Calculator.Tests.Unit/CalculatorFacts.cs`.

## Scaffold

`dotnet new digitalbrain-app -n <AppName>` creates the app, its spec and its tests. `--AppId` sets the app id; it defaults to the lower-case name.
