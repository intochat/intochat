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

In code this choice is the topology, set with `IntoChatOS:Topology` (`IntoChatOSNames.TopologyKey`). The values are `local`, `local+cloud` and `cloud` (`IntoChatOSNames`); unset means `local`. The `local` and `local+cloud` topologies run in `IntoChat.OS.Kernel` (`Program.cs` configures the host via `KernelHost` in `src/IntoChat.OS.Kernel/KernelHost.cs`, which filters discovered modules with `LoadsUnder` and applies `LocalRunDefaults`); the hosted `cloud` topology runs in IntoChat Cloud on top of `KernelHost` (container images backed by Azure Storage and Key Vault also run with `IntoChatOS__Topology=cloud`). The desktop launcher in `src/IntoChat.OS.Desktop` starts the kernel with `local` and keeps state under `~/.intochat`.

## Connection

A connection links a brain to an outside service, such as one GitHub account. Its key is `{brain}/connection/{connector}/{id}`, so one brain can hold several connections to the same service.

The connection owns its credential as sealed state. Apps never receive the credential. The contract is `IConnection`, and `ConnectionStatus` says whether a connection is connected, expired, failing or disconnected.

## Capability

A capability is something a brain can do that a connection provides, for example storage or secrets. An app asks for a capability, not for a named service. `ICapabilityBroker` finds the connections of the brain that implement it. The capabilities the framework ships contracts for are listed in `Capability`.

Modules bring connections and capabilities. See [modules](modules.md).

## Friends and groups

A friend is a link two people confirmed. One makes a single-use 24-hour `Code` through `IFriends` (stored under neuron type `friends.links`), the other redeems it, and each brain records the other's account in its one friend roster.

A folder or file of the workspace can be shared with a confirmed friend as Viewer or Editor. The file stays in its owner's brain.

- The owner's side is `IFileShares`: the list of shares, and the only file contract a person of another brain can reach. It checks `IFriends.IsFriend` and decides every request from the caller the kernel stamped and from its own list, so removing a share takes effect at once.
- The friend's side is `ISharedWithMe`: what friends shared with this person, and the route their requests take.
- Only a person can share or open a share. An app, an agent or a unit is refused.
- Removing a friend removes every share between the two.

Limits today: both brains must run on the same kernel; a share names a path, so renaming the folder ends it; when two people save the same file, the last save wins; only the workspace's own storage can be shared.

A group is an `IConversation` in its creator's brain with friends invited as participants. Friends reach it only through the `[Shareable]` door via `IFriends`, admitted only while they remain both a participant and a friend, and see only `Visibility.Everyone` entries. Older circle brains in `src/Modules/Circles` remain read-only legacy data so existing records stay readable without wiping state.

## App

An app is a DigitalBrain app that runs in a brain. There are two kinds:

- **System apps** are compiled into the product. Each has a folder in `src/IntoChat.OS.Apps`, for example `Desktop`, `Settings`, `Tasks` and `Creator`.
- **Apps made at runtime** are synapse units. They are not source in this repository. A unit is loaded inside the kernel process behind an allow-list; an app package runs in the sandbox worker, outside the kernel. See [sandbox and live behaviors](sandbox-and-live-behaviors.md).

## Ino

Ino is the agent that does work for you. It is a system app, `InoApp`, in `src/IntoChat.OS.Apps/Ino`. `InoAgentDefinition` holds its persona, the contracts it may act on and the contexts it reads each turn.

Each conversation is one aggregate neuron (`ConversationNeuron`, addressed through `IConversation` at `conversations/{id}` and listed by `ISessionList`). It owns one append-only log of `ConversationEntry` records (`Said`, `Called`, `Returned`, `Summarized`) together with its seated agents and active run; `TurnRunner` executes each agent turn against a projected model context, while `InoApp` follows `ConversationChanged` and renders `InoThread` directly from `ConversationView` without keeping a second copy of messages.

Ino acts through neurons in your brain, like any other app. It cannot approve a confirmation that is meant for a person. See [Ino and memory](ino-and-memory.md).
