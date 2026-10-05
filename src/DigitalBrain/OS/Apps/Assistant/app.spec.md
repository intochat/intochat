# Assistant

The brain's conversational assistant. A message runs one agent turn through the brain's agent
runner with the assistant's system prompt and tools (capability discovery, neuron inventory,
forms and views). The transcript shows each message, the tools the turn used and the answer.
Conversations are durable; "new" starts another one. Opening docked answers the same surface for
the shell's side panel without opening a window.

## Scenario: Opening the assistant composes its surface

Invoking "open" answers a window titled "Assistant" whose surface holds the transcript, the draft
field and the send and new-conversation buttons.

## Scenario: Opening docked answers the surface without a window

Invoking "open" with "docked" answers id "assistant-docked", the same surface and docked true.

## Scenario: A message is answered in the transcript

After sending "hello", the transcript's only turn shows "hello" and the model's answer, and its
status is done.

## Scenario: The tools a turn uses appear in the transcript

When the model calls a tool before answering, the turn's steps show that tool's call and result
before the answer.

## Scenario: A failed turn says so and the next message still works

When the model fails, the turn's status is failed with the failure as its answer; a following
message is answered normally in the same conversation.
