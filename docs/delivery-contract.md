# Delivery contract

This contract fixes how a signal written by one neuron reaches the reactions (`[On]` methods) of the neurons that hold a synapse to it. It is written before any stream code (Phase 4, D-07); every runtime change that touches synapses must keep it true. Facts in `tests/DigitalBrain.Architecture.Tests.Unit/DeliveryContractFacts.cs` guard its shape.

## Guarantees

- **At-least-once** delivery from the stream; **exactly-once** observation by reactions, because the runtime dedupes by cursor (see below).
- **Ordered per source by journal sequence.** Signals from one publisher reach a subscriber in the order of the publisher's journal sequence. No ordering is promised across publishers.
- **The journal is the truth; the stream is a doorbell.** There is no replay from Orleans streams: `EarliestAvailable`, rewind tokens and Orleans-side queue retention are not durability and the runtime never depends on them.
- **No silent loss.** A subscriber either sees every signal after its cursor or receives `Gap(From, To)` naming what it missed.

## Payload

The stream item is a reference only: `(publisher NeuronId, sequence)` (D-01). Signal content never travels on the stream, so the Azure Queue 48 KiB message cap does not apply and reactions never read signal content from the stream. The runtime reads the content from the publisher's journal through `ISignalHistory`, which checks the brain, before invoking a reaction.

## Ordering and dedupe

The runtime keeps a per-source cursor (last delivered sequence per publisher) in the subscriber's state. Any item at or below the cursor is dropped; an item above `cursor + 1` triggers catch-up for the missing range first. `[On]` methods therefore never see duplicates or reordering (D-03). The cursor advances in the same journal entry as the reaction's `Save` (or `Publish`): the subscriber's state, the signals it publishes and its advanced cursor are one durable write, so a stop in the middle of a reaction leaves either none of them or all of them, and a reaction whose save is durable never runs again. A reaction that saves nothing gets one cursor-only entry. Catch-up on activation reads the cursor from that same journal record.

## Catch-up

- A synapse is added once with `IStreamSubscriptionManager.AddSubscription` (implicit subscriptions off, `ExplicitSubscribeOnly`; `StreamId` only, as on Orleans 10.4) and resumed with `ResumeAsync` in `IStreamSubscriptionObserver.OnSubscribed` on every activation. The runtime never calls `SubscribeAsync` per activation (D-12).
- On every activation the subscriber catches up from its cursor through `ISignalHistory.Read(since, limit)` before handling live items.
- Every producer appends to its journal before publishing. Publishing is a notification of a durable fact, never the fact itself.

## Retention floor and Gap

- Retention floor per publisher = `min(lowest live synapse cursor, Last - HistoryWindow)`.
- `HistoryWindow` defaults to the larger of 7 days or 10 000 signals per publisher, configurable under `IntoChatOS:Signalling:Retention` (D-11).
- A subscriber whose cursor is behind the floor receives `Gap(From, To)` and its cursor moves to `To`; it never loses signals silently (D-04).

## Dead subscribers

- A cursor not advanced for `IntoChatOS:Signalling:Retention:StaleCursorAfter` (default 7 days) stops pinning the floor; when its owner returns it receives `Gap(From, To)` for anything trimmed meanwhile.
- Session synapses (scripts, in-process clients) end with the session plus `SessionTtl`, 10 minutes after the last acknowledgement.

## Write-then-crash window

A silo can die between the journal append and the stream publish. Two mechanisms close the window, both idempotent because catch-up is cursor-deduped:

- The publisher re-publishes its own `Last` on activation.
- Every starting silo reconciles subscribers whose cursor is behind the publisher's `Last`, enumerating brains from the durable synapse registry index (`ISynapseRegistryIndex`), never from local files.

## Authorization

Subscribing is authorized at subscribe time under the subscriber's caller identity through `AdmitSynapse`. A synapse that is not admitted is never added to the stream; catch-up reads go through the same brain-checked `ISignalHistory`.

## Topologies

- `LocalProfile` and `LocalCloudProfile` bind memory streams plus a file `PubSubStore`.
- `CloudProfile` binds Azure Queue streams plus a table `PubSubStore`.
- The topology validator refuses a non-durable `PubSubStore` outside `local` (D-09).

## Stream identity

- Namespace `brain.signals.<signal namespace>`, key = brain key: one stream per (brain, signal namespace), per the binding Phase 1 spike verdict (5 streams instead of 50 for 50 neurons across 5 namespaces).
- Fan-out is namespace-wide, so the consumer filters by source publisher.
- Stream ids are pinned in `ContractManifest.json` (D-06).
