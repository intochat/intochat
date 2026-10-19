/*
# Ino

The brain's conversational assistant. A message runs one agent turn through the brain's agent
runner with the assistant's system prompt and tools (capability discovery, neuron inventory,
forms and views). The transcript shows each message, the tools the turn used and the answer.
Conversations are durable; "new" starts another one. Startup publishes one immutable view as main content.

Before a message reaches the language model, Jev (the router) reads it against the contract catalog.
Sending asks the router and ends; the router answers with a `Routed` signal carrying the request id,
and a second reaction carries the answer out, so a slow model never holds a message. A message
longer than 2000 characters skips Jev and goes straight to the model, and the status line says so.
The surface's composer is the other way in: its submission starts the turn in the selected
conversation directly (no Jev), is settled once the turn starts, and clears the status line.
A route the router is at least `RouteGate` (default 0.8) sure of, and that opens an app or is a pure
read, is carried out directly: the app's "open" is requested (the route Jev decided, never decided
again), a pure read goes through the router. The status line shows the route and its outcome, and no
language model turn runs. Jev is often confident about the wrong app, so a route to any operation of
an app the message does not name (by its name or its title) is never carried out: the status line says
the message does not name the app, and the model answers the message itself with Jev's guess only as a
weak hint. Any other operation of a named app needs its input filled from the message (Jev would post
the whole message, so "add 17+25 with the calculator" would reach "add" as that sentence), so the
model answers with the route as a hint: it posts the request with the input the operation expects and
reads the app's answer neuron. A route that names the assistant's own
operations is never carried out: a message sent to the assistant is the model's to answer, so it
never loops back into "send" or changes focus. Any other route runs the agent turn as usual, with
the likely capabilities appended to the system prompt. Either way the `Routed` signal shows which path answered. A turn that
cannot start (a response is already running, the Model setting names an unavailable model) says so
on the status line and later messages still work; a message that arrives while a response is
running is put into the composer's draft when the draft is empty, so sending it again is one click.

## Scenario: Opening the assistant composes its surface

Invoking "open" composes and publishes the Ino view in main: an inline header row
(durable status and inline New conversation action), transcript chat and composer. Ino owns this view.
The transcript and composer remain durable references, preserving read receipts, drafts and voice state.
Repeated opens recover the native chat selection and reuse an unchanged view; program revisions replace its generation.

## Scenario: The dock is the assistant's toolbar

The manifest's toolbar is a text field for "send" and a mic button for "voice". Submitting the
field posts "send" with the text; the transcript shows the answer. The shell renders this toolbar
below the focused sphere.

## Scenario: The dock's mic begins a voice message

Invoking "voice" composes the surface and begins a voice capture on the composer under the request
id (its voice status is "recording"). Audio the composer captures for it is transcribed into the
draft; a failed transcription leaves the draft unchanged and says so.

## Scenario: A message is answered in the transcript

After sending "hello", the transcript's only turn shows "hello" and the model's answer, and its
status is done.

## Scenario: A plain answer leaves nothing behind but its turn

A turn answered without tools keeps no step cards: the answer streams into the turn itself, so
a long reply never leaves one stored card per piece of text.

## Scenario: The tools a turn uses appear in the transcript

When the model calls a tool before answering, the turn's steps show that tool's call and result
before the answer.

## Scenario: A failed turn says so and the next message still works

When the model fails, the turn's status is failed with the failure as its answer; a following
message is answered normally in the same conversation.

## Scenario: A confident route answers without the language model

Sending "open calculator" while Jev routes it to calculator.open with confidence above `RouteGate`
requests the calculator's "open": the calculator window opens, the status line names the route and
its confidence, and no turn is added to the transcript.

## Scenario: An app operation that needs input is answered by the model

Sending "Use the calculator to add 17+25 and tell me the sum" while Jev routes it to calculator.add
posts nothing directly: the status line says the model fills the input, and the model requests
"add" with "17+25", reads the calculator's sum neuron and answers 42 in the transcript.

## Scenario: A route back into the assistant is answered by the model

Sending "hello" while Jev routes it to one of the assistant's own operations (for example
assistant.open) carries nothing out: the status line says Jev pointed back at the assistant and the
model answers in the transcript.

## Scenario: A route to an app the message does not name is answered by the model

Sending "Use your tools to look up which apps are installed" while Jev routes it to another app's
operation (for example router.ask) posts nothing to that app: the status line names the suggested
route and says the message does not name the app, and the model answers in the transcript, its
steps showing the tools it used.

## Scenario: An unsure route runs the agent turn with Jev's hints

Sending a message Jev routes with confidence below `RouteGate` runs the agent turn; the turn's
system prompt ends with the likely capabilities Jev named, and the status line says Jev was unsure.
A message for which Jev finds no capability at all runs the agent turn without hints, and the
status line says Jev was unsure because nothing fits (never that Jev was unavailable).

## Scenario: Jev being unavailable never blocks a message

When the System One integration is not ready, the message runs the agent turn as before and the
status line says Jev was unavailable.

Stop is a behavior-defined chat action, present while the selected conversation is running.
Messages and actions share one versioned projection. Its ID includes the immutable run ID; clicks
carry the displayed source, generation and action ID, and the chat neuron publishes only the current
matching action's canonical payload. A delayed click cannot cancel a newer run or conversation.
Streaming versions keep the same action valid. Replay or delayed signal delivery cancels only the
captured run. Cancellation preserves durable history, the partial answer and inspectable activity.
Selection changes cancel and drain the old projection scope before following the next conversation;
the chat and composer also reject older generations. No separate Stop projection can race the chat.

Projection may cache successfully delivered source versions during a behavior lifetime to avoid
repeating identical writes. Versioned lifecycle signals at or below a successfully projected version
may also skip the source read. Startup, reconnect and unversioned signals still reconcile from neurons.
Restart always reconstructs from neurons; no cache owns conversation state.

Invocation handlers select their operation names before reading pending work. Surface composition
handles open; conversation commands handle send, transcript and new. Both recover their own pending
operations on startup and still check durable state before handling a matching live notification.
Delivery is at-least-once and a fresh subscription is seeded with the app's retained requests, so every
request synapse keeps the ids it has handled in a ledger text of its own (`handled/{synapse}`) and skips
a replayed id: an old "open" never reopens a window the person closed, an old "new" never reselects its
conversation, an old "voice" never begins a finished capture again, and Jev is asked once per message.

An operation failure settles that invocation with an understandable error and does not stop later
commands. A transcript exceeding the app response limit directs the caller to read the full
conversation in the assistant; it never truncates or deletes durable history. If a command loop
cannot settle an error, its failure reaches the runtime supervisor so pending work can recover.

## Scenario: "make" makes the focused thing do what the sentence says

Invoking "make" with JSON `{"focus": ["<brain>/apps/calculator/window"], "sentence": "…"}` writes one
synapse into the calculator through its fast path (`IApp.WriteSynapse`, operation id = the request id)
and ends; the outcome is the calculator's synapse record under that id. The first focus key that is an
installed app of this brain, or a neuron under one (`{brain}/apps/{name}/…`), names the target; the
assistant never writes into itself. With no such key (or plain text, a sentence with no focus) the
synapse goes into the brain's "scratch" app, created the first time it is needed. Focus is whatever the
caller passes: the shell passes the keys of the windows it shows. A malformed input says so on the
status line and writes nothing.
Presentation: startup publishes one sphere and its main content using stable neuron identities.
Workspace navigation owns focus; composition never changes it.

## Scenario: A vague message becomes a brief

Sending "fix the thing from yesterday" with Memory installed asks Memory's "intend" first, and Ino watches the
answer `{memory}/intents/{requestId}`. When it reads `ok`, the turn starts with the brief Memory wrote for the
message: when the intent is Confirmed the model acts on the brief, otherwise it shows the brief and asks
before acting. When the answer reads `failed: <reason>` the turn answers the message without a brief.

## Scenario: An active intent is shown in a new conversation

When Memory is installed, its context neuron (`working-memory` and `active-intents`) is attached to every
turn. After an intent has been declared and is still active, a message sent in a new conversation is
answered with that intent rendered in the turn's context. The model can also declare an intent itself through
the `IIntents` contract, and it then appears in the next conversation the same way.

## Scenario: Memory not installed answers directly

With Memory not installed, sending a message shows the status "Memory is not installed; answering directly"
and the turn runs on the message alone, with no brief and no Memory context.

## Scenario: Intend is slow

When Memory has not answered within `IntendGate` (default 8 seconds), the status shows "Memory was slow to
intend; answering without a brief" and the turn answers the message without a brief.

Progress states are Ino's mood source: listening when input arrives, working while answering,
completed after an answer, and attention on failure. Each of those writes publishes mood on the
same activity call. No second script watches AppActivity.

*/
using IntoChat.OS.Apps;

static class App
{
    public const string Name = "ino";
    public const string Version = "3.1.0"; // Proposed migration release.
    public static readonly AppManifest Manifest = new(
        Title: "Ino",
        Description: "Talk to your brain, use its capabilities, and create apps.",
        Operations:
        [
            new("open", "Show Ino."),
            new("send", "Route or answer a message."),
            new("new", "Start a new conversation."),
            new("new-conversation", "Alias for new."),
            new("voice", "Begin voice input."),
            new("make", "Apply a sentence to the focused app, or Scratch."),
        ],
        Settings:
        [
            new("Model", "Model catalog id; empty uses the default.", ""),
            new("RouteGate", "Confidence required for a direct route.", "0.8"),
            new("IntendGate", "Seconds to wait for Memory's brief before answering without it.", "8"),
        ],
        Slots: [], Icon: "icon.svg",
        Toolbar:
        [
            new("send", Kind: "text", Label: "Message your brain"),
            new("new-conversation", Icon: "chat", Label: "New conversation"),
            new("voice", Icon: "mic", Label: "Voice"),
        ]);
}
