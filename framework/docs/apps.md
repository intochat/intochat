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

## Controls and child screens

A screen can put another neuron in its view with `Child<TChild>(key)`. A control neuron (a check box, a list box, a live chart) takes props as well: `Child<TChild, TProps>(key, props)`, where the child implements `IControl<TProps>` and `TProps` derives from `ControlProps`. `TChild` may be the control neuron or a contract interface that derives from `IControl<TProps>` and names the neuron type with `[DefaultNeuronType]`, so a screen can mount a control without referencing its implementation. Props are what the parent decides and re-render with the parent; the child stores only what the user changes. When the parent owns the value (a preference), it passes it in the props and the child only signals.

A child ref names its child by key, never by id. A command to a child goes through the parent: `IScreen.Dispatch(command)` takes a target of `"."` (the screen), a key, or a key path `"a/b"` that each child resolves one hop further. A key the current view does not show is refused with `ActivationRefusedException.UnknownChild`. Props that implement `ICommandGate` check a command before the child sees it, and a child whose props are disabled refuses every command. `IScreen.ReadChild(path)` reads a child's view the same way.

`FollowChild<TChild, TSignal>(key)` makes a signal of a child reach the parent's `[On]` reaction, where `SignalChild` names the key it came from. `RenderDetached(state)` draws a preview without saving anything.

On the OS edge, a screen's commands go to `POST /brains/{id}/act` and its child views to `GET /brains/{id}/apps/{name}/slots/{path}` (wire `refKind` remains `"slot"` for client compatibility). The host marks a screen's view with `"acts": "act"` (`View.ActRoute`) when it serves it; a view has no such property, so an app cannot set the mark. A request may carry a field's text as `value`, which joins the command's args as `value`. Only the kernel names who acted: the route refuses args that name `by`, and the screen that handles a command (itself, or a child after the parent's hop) finds the caller's person principal under `by`. The act route answers 400 for a malformed request (args naming `by` or a `value` the args already declare included), 409 with a reason for a refusal, and 422 `HandlerFailed` when the handler throws.

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
