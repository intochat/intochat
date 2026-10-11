/*
# Engineer

The brain's engineer: a person says what they want changed, and the Engineer decides at which level the
change belongs and hands it to the builder for that level. Each "ask" is classified with one language
model call into one of three kinds:

- **synapse**: one reaction ("when X happens, do Y") the installed contracts already express. The fast
  path writes it into the target app: the app the request names, or else the Engineer's "scratch" app,
  created the first time it is needed. The request's own words are the sentence.
- **app**: a whole new program. The Creator (the brain's app studio) authors and builds it.
- **module**: a capability no installed contract offers (a new external system, a new neuron type or
  signal, a change to the OS itself). Module builds are gated (NG-1..NG-5) and the Module Builder is not
  installed, so the request is recorded as blocked with that reason: a wish for the person to decide.

The requests are the Engineer's wish queue and live in neurons, never in a script: `{app}/requests/{id}`
holds each request (its words, kind, target, status, outcome and timings) and `{app}/requests` lists the
ids. Nothing waits for a builder: "ask" records the request, starts the builder and ends; a separate
reaction follows each written synapse from its target app (its outcome in the app's synapse list, matched
by the request id) and another runs app builds one at a time. A request delivered twice is handled once.
For a written synapse the request records how long the fast path took and how long it took from the ask
to the live synapse.

"ask" takes plain text, or JSON `{"request": "…", "app": "…"}` where `app` names the target app of a
synapse and `focus` may list neuron keys to write against (neuron keys named in the request are its focus
too). The setting Model picks the classifier's model; empty uses DeepSeek V4.1 Flash.

## Scenario: Opening shows the requests

Invoking "open" opens an "Engineer" window whose surface holds `{app}/status` (how many requests, by
status) and `{app}/list`, every request newest first with its status, kind, target and outcome. Opening
"docked" composes the same surface without the window.

## Scenario: A synapse request becomes a live synapse in the scratch app

Asking "when 'run' is requested on owner/apps/scratch, write the input reversed into the text neuron
owner/apps/scratch/echo" classifies as a synapse, creates the scratch app if it is missing and writes the
synapse there through the fast path. Once the synapse is written, `{app}/requests/{id}` shows Written
with the synapse's path, the fast path's time and the ask-to-live time, and the list shows it. Requesting
"run" with "hello" on the scratch app then writes "olleh" into `owner/apps/scratch/echo`.

## Scenario: A module request is recorded as blocked

Asking "add a module that streams live Bitcoin prices from an exchange" classifies as a module: the
request shows Blocked with the reason (module builds are gated by NG-1..NG-5) and nothing is built.

## Scenario: A malformed request is refused

Asking with an empty input, JSON that does not parse, or a JSON object without a `request` text records
the request as Refused with the reason ("Request refused: …"); no model is called and nothing is built,
and the next request is handled as usual.
*/

using DigitalBrain.Apps;

static class App
{
    public const string Name = "engineer";
    public const string Version = "1.0.4";

    public static readonly AppManifest Manifest = new(
        Title: "Engineer",
        Description: "Say what to change in your brain: the Engineer writes a synapse, builds an app, or records a module wish.",
        Operations:
        [
            new("open", "Show the requests and their outcomes; input \"docked\" composes the surface without a window."),
            new("ask", "A change in plain text, or JSON {request, app?, focus?}. It is classified as a synapse, an app or a module and recorded in {app}/requests/{requestId}."),
        ],
        Settings:
        [
            new("Model", "Model catalog id for the classifier (\"preset:<Marker>\" or \"profile:<name>\"); empty uses preset:IDeepSeekV41Flash.", ""),
        ],
        Slots: [],
        Icon: "icon.svg",
        Toolbar:
        [
            new("open", Label: "Engineer"),
            new("ask", Kind: "text", Label: "Ask the Engineer"),
        ]);
}
