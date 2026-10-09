# Assistant

The brain's conversational assistant. A message runs one agent turn through the brain's agent
runner with the assistant's system prompt and tools (capability discovery, neuron inventory,
forms and views). The transcript shows each message, the tools the turn used and the answer.
Conversations are durable; "new" starts another one. Opening docked answers the same surface for
the shell's side panel without opening a window.

Before a message reaches the language model, Jev (the router) reads it against the contract catalog.
A route the router is at least `RouteGate` (default 0.8) sure of, and that is an app operation
(`IApp.Request`) or a pure read, is carried out directly through the router; the status line shows
the route and its outcome, and no language model turn runs. Any other route runs the agent turn as
usual, with the likely capabilities appended to the system prompt. Either way the router leaves a
`Routed` signal, so the shell can show which path answered.

## Scenario: Opening the assistant composes its surface

Invoking "open" answers a window titled "Assistant" whose surface holds the transcript, the draft
field and the send and new-conversation buttons.

## Scenario: Opening docked answers the surface without a window

Invoking "open" with "docked" answers id "assistant-docked", the same surface and docked true.

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

Sending "open calculator" while Jev routes it to `IApp.Request` with confidence above `RouteGate`
dispatches the request through the router: the calculator window opens, the status line names the
route and its confidence, and no turn is added to the transcript.

## Scenario: An unsure route runs the agent turn with Jev's hints

Sending a message Jev routes with confidence below `RouteGate` runs the agent turn; the turn's
system prompt ends with the likely capabilities Jev named, and the status line says Jev was unsure.

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

An operation failure settles that invocation with an understandable error and does not stop later
commands. A transcript exceeding the app response limit directs the caller to read the full
conversation in the assistant; it never truncates or deletes durable history. If a command loop
cannot settle an error, its failure reaches the runtime supervisor so pending work can recover.
