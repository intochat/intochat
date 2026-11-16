# Concepts

These are the product's own concepts. Neuron, signal and synapse come from the framework; read [Neurons and Synapses](../framework/docs/neurons-and-synapses.md) for those.

## Brain

A brain is one person's or team's workspace. It holds data, apps and connections. Everything in a brain is owned by that brain, and a neuron's key starts with the brain's id.

## Where a brain runs

You choose where your brain runs:

1. **Sign in.** Your brain runs in IntoChat Cloud.
2. **Run on this computer.** In-memory to try, persistent to keep.
3. **Connect to your own server.** The same kernel runs in a container.

The shell speaks one protocol in all three. Only the brain's address differs. On phones the shell connects to a brain that runs elsewhere; the kernel does not run on phones.

In code this choice is the topology, set with `IntoChatOS:Topology`. The values are `local`, `local+cloud` and `cloud` (`IntoChatOSNames`). Unset means `local`. `Program.cs` in `src/IntoChat.OS.Kernel/IntoChat.OS.Kernel` reads the topology and loads only the modules that belong to it. The desktop launcher in `src/IntoChat.OS.Desktop` starts the kernel with `local` and keeps state under `~/.intochat`.

## Connection

A connection links a brain to an outside service, such as one GitHub account. Its key is `{brain}/connection/{connector}/{id}`, so one brain can hold several connections to the same service.

The connection owns its credential as sealed state. Apps never receive the credential. The contract is `IConnection`, and `ConnectionStatus` says whether a connection is connected, expired, failing or disconnected.

## Capability

A capability is something a brain can do that a connection provides, for example storage or secrets. An app asks for a capability, not for a named service. `ICapabilityBroker` finds the connections of the brain that implement it. The capabilities the framework ships contracts for are listed in `Capability`.

Modules bring connections and capabilities. See [modules](modules.md).

## Circle

A circle is a brain that several accounts share. Its members share apps, a chat and a share of the owner's compute. The contract is `ICircle`; the module is in `src/Modules/Circles`.

- The creator invites accounts. An invited account accepts and becomes a member.
- Owners and admins can make invite codes.
- When a member shares an app, the connections that app uses are bound for the circle. They are withdrawn when the sharer leaves.

The circle server is in `src/IntoChat.Cloud/Circle/Server`. It loads only in the `cloud` topology.

## App

An app is a DigitalBrain app that runs in a brain. There are two kinds:

- **System apps** are compiled into the product. Each has a folder in `src/IntoChat.OS.Apps`, for example `Desktop`, `Settings`, `Tasks` and `Creator`.
- **Apps made at runtime** are synapse units. They are not source in this repository. A unit is loaded inside the kernel process behind an allow-list; an app package runs in the sandbox worker, outside the kernel. See [sandbox and live behaviors](sandbox-and-live-behaviors.md).

## Ino

Ino is the agent that does work for you. It is a system app, `InoApp`, in `src/IntoChat.OS.Apps/Ino`. `InoAgentDefinition` holds its persona, the contracts it may act on and the contexts it reads each turn.

Ino acts through neurons in your brain, like any other app. It cannot approve a confirmation that is meant for a person. See [Ino and memory](ino-and-memory.md).
