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

## Workspace files

Each brain has a workspace: a confined folder on the host, offered as a storage connection (`CloudModule`). Code reaches it by path in two ways, both under the caller the kernel stamped:

- A screen declares an `IWorkspaceFiles` slot (read, write, list and find by path, `/work/...` in stored state). Only callers of the brain pass, and an app needs the shipped grant; Data Studio, Markdraw, Ino and the ArtCraft studios hold it.
- A web page, such as an ArtCraft studio, uses the storage content route the host serves (`StorageContentEndpoints`, hosted by `StorageContentHosting`): `/brains/{brain}/workspace/item?path=` finds a file, `/brains/{brain}/storage/{storage}/items/{id}/content` reads it, and an upload ticket writes it. `{storage}` is the connection key in one encoded form (`StorageContentEndpoints.Segment`). Replacing a file waits for the person's answer. The Files screen's previews use the same route.

## Friends and groups

A friend is a link two people confirmed. One makes a single-use 24-hour `Code` through `IFriends` (stored under neuron type `friends.links`), the other redeems it, and each brain records the other's account in its one friend roster.

A folder or file of the workspace can be shared with a confirmed friend as Viewer or Editor. The file stays in its owner's brain.

- The owner's side is `IFileShares`: the list of shares, and the only file contract a person of another brain can reach. It checks `IFriends.IsFriend` and decides every request from the caller the kernel stamped and from its own list, so removing a share takes effect at once.
- The friend's side is `ISharedWithMe`: what friends shared with this person, and the route their requests take.
- Only a person can share or open a share. An app, an agent or a unit is refused.
- Removing a friend ends their access at once: the door asks the roster on every call of a visitor. The roster publishes `FriendRemoved`, and the door then drops every share it held for that person.

Limits today: both brains must run on the same kernel; a share names a path, so renaming the folder ends it; when two people save the same file, the last save wins; only the workspace's own storage can be shared.

A group chat between people is a room (`IRoom`) in its host's brain; it admits its host and the friends the host invited. A person's rooms index (`IRooms`) lists the rooms and the friends' conversations they were invited to. A friend can also sit as a guest in an `IConversation` in its creator's brain. Friends reach it only through the `[Shareable]` door via `IFriends`, admitted only while they remain both a participant and a friend, and see only `Visibility.Everyone` entries.

A person's posts are kept by one post store in their brain (`IPosts`). A post carries an `Audience` (public, friends, or a fixed list of accounts), and the store shows it to a person of another brain only when that audience admits them and they are a friend at that moment; public means the same as friends for now.

Neurons whose contracts carry `[Offered]` (such as `ITable`) can also be shared with a confirmed friend through `IOffers` at a `Role` (`Role.Viewer` or `Role.Editor`). Access is checked on every call; revoking the offer or unfriending takes effect immediately.

## Component

A component is the one packaging unit that replaces the module and the system app, one at a time. It lives in `src/Components/<Name>` and owns everything about one capability:

- **Contracts**: the neuron interfaces, signals and records other code may use, in `Contracts/IntoChat.<Name>.Contracts.csproj`. Other modules and components are meant to reference a component only through this assembly; `ComponentFacts` checks that direction for a component's own references, not yet for a module's.
- **Neurons**: the implementation, in `Neurons/` inside `IntoChat.<Name>.csproj`.
- **Signals**: the facts its neurons publish, declared beside the contracts.
- **Screens**: the apps a person opens, in `Screens/`. A screen is a neuron whose name starts `app.`; C# decides what is on screen and Flutter draws it.

Aspire is the example: `src/Components/Aspire` holds the contracts (`IAspire`, `ICandidate`, `ICandidates`, `IDeployment`), the telemetry, candidate, deployment and verification neurons, and the `app.aspire` screen (`AspireApp`). `IAspire` reads the running app's resources, logs and traces. Its one writing verb is `Restart`: a person who calls it directly restarts the resource at once; any other caller gets `"pending"` and the restart runs when a person approves it. Refusals are returned values, not exceptions. The rules a component follows are enforced by `ComponentFacts`. See [modules](modules.md).

Only Aspire has moved so far. Every other module is still in `src/Modules` and every other system app in `src/IntoChat.OS.Apps`; the two layouts coexist.

## App

An app is a DigitalBrain app that runs in a brain. There are two kinds:

- **System apps** are compiled into the product and ship with it. Most still have a folder in `src/IntoChat.OS.Apps`, for example `Desktop`, `Settings`, `Tasks`, `Creator` and `CustomerResearcher`. A moved app is a screen of its component, for example `AspireApp` in `src/Components/Aspire/Screens`; the module that owns it registers it, so it ships whenever that module is loaded. A brain that stored the shipped, unedited app before the move keeps working, because the check on a host-loaded shipped app does not resolve its stored type name and does not rewrite it; an edited copy of a moved app does not, because its stored type name no longer resolves and the app shows as failed.
- **Apps made at runtime** are synapse units. They are not source in this repository. A unit is loaded inside the kernel process behind an allow-list; an app package runs in the sandbox worker, outside the kernel. See [sandbox and live behaviors](sandbox-and-live-behaviors.md).

Apps can declare their neuron dependencies as `[Slot]` partial properties (for example `CustomerResearcherApp` in `src/IntoChat.OS.Apps/CustomerResearcher/CustomerResearcherApp.cs`). Each installation binds its slots through `IInstalls` using typed `Binding` targets (`BindingTarget.Local` for a neuron in the same brain, or `BindingTarget.Offered` for a neuron offered by a friend via `IOffers`).

## Ino

Ino is the agent that does work for you. It is a system app, `InoApp`, in `src/IntoChat.OS.Apps/Ino`. `InoAgentDefinition` holds its persona, the contracts it may act on and the contexts it reads each turn.

Each conversation is one aggregate neuron (`ConversationNeuron`, addressed through `IConversation` at `conversations/{id}` and listed by `ISessionList`). It owns one append-only log of `ConversationEntry` records (`Said`, `Called`, `Returned`, `Summarized`) together with its seated agents and active run; `TurnRunner` executes each agent turn against a projected model context, while `InoApp` follows `ConversationChanged` and renders `InoThread` directly from `ConversationView` without keeping a second copy of messages.

Ino acts through neurons in your brain, like any other app. It cannot approve a confirmation that is meant for a person. See [Ino and memory](ino-and-memory.md).
