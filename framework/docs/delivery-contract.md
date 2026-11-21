# Signal Delivery Contract

How a journaled signal reaches the neurons that hold a synapse to its publisher.

## Guarantees
Signals a neuron publishes with `Save(next, signals)` or `Publish(signal)` are written to the brain journal and delivered **at-least-once** to every subscriber. `PublishEphemeral(signal)` (and any `[EphemeralSignal]` passed to `Publish`) is not journaled and is outside this durable contract: it reaches silo-local listeners and subscribed session synapses (`runtime.session`) best-effort with sequence `0`, and is dropped if a session's pending queue is full. Durable neuron synapses never receive ephemeral offers.

## Payload
The journal keeps each signal as a `JournaledSignal`: the publisher's `NeuronId`, the publisher sequence, the typed `Signal`, the time, the trace id and the causation id. A typed reaction gets the `Signal`. An alias reaction gets a `SignalEnvelope` with the alias and the raw JSON payload.

## Ordering and dedupe
Signals of one publisher arrive in sequence order. Each subscriber keeps a cursor per source and stores it with the reaction's own save. A signal at or below the cursor is dropped as a duplicate. When a reaction throws, the cursor moves back and the signal is delivered again.

## Catch-up
A subscriber does not depend on the wake-up after an append. When it activates or is nudged, it reads the journal from its cursor forward until it reaches the end (`CatchUp` on the delivery of its host).

## Retention floor and Gap
The journal trims entries below a floor. A subscriber whose cursor is below the floor of a source receives one `Gap(Source, From, To)` signal in place of the missing signals, and rebuilds from the source's current state. On the Orleans runtime `RetentionOptions` sets the floor: `HistoryWindowAge`, `HistoryWindowSignals`.

## Dead subscribers
A cursor holds the floor back only while it is live. A cursor that has not moved for `StaleCursorAfter` stops pinning the floor. If that subscriber returns, it gets a `Gap`.

## Write-then-crash window
State, signals and the cursor move are one journal entry: durable as a whole or not at all. If the process dies after the append and before subscribers are woken, the signal is still in the journal and catch-up delivers it. A `[Synapse]` gate is the exception: its state can lag one signal behind the publisher's entry.

## Authorization
Before a synapse is created, the runtime asks the source neuron: `AdmitSynapse` gets a `SynapseRequest` with the subscriber, the signal types and the caller the runtime stamped. The default admits subscribers of the same brain. A refusal throws `UnauthorizedAccessException` and no synapse is recorded.

## Topologies
`TestOS.Local()` (in process, deterministic) and the Orleans host started by `SiloHost.Local()` honor the same contract.

## Stream identity
A synapse is identified by the publisher's `NeuronId` (`brain/type/key`), the signal's `[Signal]` alias and the subscriber's `NeuronId`. A brain-wide synapse has no single publisher: it reads the whole brain's journal in order.
