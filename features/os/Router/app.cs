/*
# Router

Route a request to the right capability and carry it out, without a chat model deciding. The
router names the capabilities it may act on: timers, notes and C#, plus the PC's windows, console
and files once this brain has a windows connection. A router neuron of its own for each request
asks a System One model (TypeSafe Jev) which of them and which method fit the request and gates on
Jev's confidence; then a language model fills only the grain key and arguments the call needs, the
call runs, and its result feeds the next decision until Jev says the request is done. A request
nothing fits is refused, not guessed. Asking the same thing again carries it out again.

A capability the brain cannot reach is left out, because every extra option dilutes Jev's choice.
Apps are not a capability here: an app is driven by its own operations (the assistant's job), and
the router never calls an app's own methods.

## Scenario: Opening the console shows the prompt

Startup and "open" publish the main layout with answer text, timer status, prompt and Send.

## Scenario: Send asks

Clicking Send posts the prompt's text as an "ask" request; a click delivered twice asks once.

## Scenario: A request is routed and carried out

Invoking "ask" with "set a timer for 5 minutes" routes to `ITimer.Start`, starts the timer, and the
result neuron answers with the chosen method.

## Scenario: A timer is shown in main

When the routed contract is the timer, the router adds it to its timer list (the key, its duration,
when it was asked for and set), updates the timer text in main and focuses the Router sphere. The text part is rebuilt from that list and the ticks heard: the newest timer
with its duration and the time it rings by, earlier ones listed below. When the timer fires, the text
part says the time is up. A tick counts only for a timer it can belong to (the same key, no earlier
than that timer's due time, before a later timer on the key replaced it), so a tick heard late, twice,
or from an older timer never says a running timer is up. Showing the router never replaces the
routed answer.

## Scenario: An unclear request is refused

When Jev's confidence is below its gate, "ask" answers without dispatching and the result neuron
says the request was not clear enough to route.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "router";
    public const string Version = "1.1.9";

    public static readonly AppManifest Manifest = new(
        Title: "Router",
        Description: "Route a request to the right capability with Jev, then carry it out.",
        Operations:
        [
            new("open", "Open the Jev console: a prompt, a send button and the route it answers."),
            new("ask", "Route a request through Jev and carry it out."),
        ],
        Settings: [],
        Slots: [],
        Icon: "icon.svg");
}
