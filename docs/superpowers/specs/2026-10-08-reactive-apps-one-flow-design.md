# Reactive apps, one flow

Date: 2026-10-08. Status: approved in conversation, ready for a plan.

## Problem

One fact, "this app's program with these settings should be running in this brain", is stored in
six places (app bundle, deploy ledgers in `AppState`, one `CSharpFileState` per synapse, the
runtime's `BehaviorAssignment`, the sandbox's loaded set, and the invocation list with in-memory
waiters). Every hop between them is an imperative delta that can land half-way, so each layer grew
its own repair: tracked keys, stored deploy errors, a dead `ProgramGeneration`, per-behavior
failure counts and disarming, reload timers, a 5-minute `Await`, a pending-invocation re-read in
every script. A plain request ("open the assistant") rides a durable pub/sub pipeline into a
long-lived process and, when any layer is stale, the caller waits forever. Wiping storage "fixes"
it only because it resets every copy at once.

## Vision

A brain is neurons exchanging signals. A behavior is **one reaction**: a synapse file that is one
`await foreach` over one `brain.On<T>(source)`, acting through neurons. The sandbox is a process
holding all of a brain's compiled reactions. Nothing is request/response: the shell, the
assistant's agent and other apps all act by publishing signals and watching neurons.

The script-facing contract (`IDigitalBrain`: `Get`, `On`, `Self`; `DigitalBrainClient.ConnectAsync`)
does not change.

```csharp
// assistant/synapses/open.cs
await using var brain = await DigitalBrainClient.ConnectAsync(args);
var app = brain.Get<IApp>(brain.Setting("App")!);
await foreach (var request in brain.On<AppRequested>(app, brain.Stopping))
{
    if (request.Operation != "open") { continue; }
    await Ui.Compose(brain, app);                                                   // lib/Ui.cs, same assembly
    if (request.Input != "docked")
    { await brain.Get<IWorkspace>(brain.Setting("Brain")!).EnsureOpenAsync("assistant", "Assistant", Ui.Window(app), brain.Stopping); }
}

// assistant/synapses/new.cs
await foreach (var clicked in brain.On<ButtonClicked>(brain.Get<IButton>(app.Key + "/new"), brain.Stopping))
{ await brain.Get<IText>(app.Key + "/conversation").Set("c-" + clicked.RequestId.ToString("N")); }
```

Two clicks are two signals in the subscription buffer, handled one after the other, each once.

## What stays (the architecture)

- Source neurons publish on `Save`; `BrainRuntimeNeuron` is the Orleans observer (`Watch`/`Unwatch`
  with lease renewal), one per brain.
- `Subscription(Pending, Delivered, Dropped)` with watermark and acknowledgement: the durability.
- Sandbox session over SSE and the HTTP runtime edge with a session token; heartbeat, silence
  limit, reconcile reminder, token rotation: the brain is the only thing that can relaunch a dead
  process.
- `NeuronProxy` calls from scripts through the edge, kernel-stamped as the app.
- `CSharpFileNeuron` for standalone behaviors a person writes outside an app.
- The loop form of `On<T>` and the `onSubscribed` recovery hook.

## What changes

### 1. Operations are one signal the app publishes about itself

`IApp` loses `Invoke`, `Respond`, `Await`, `Pending`, `ReadInvocation`, `ReadInvocations`. It gains:

```csharp
// Publishes AppRequested. The caller never waits for a script: it reads the neurons the script writes.
Task Request(string operation, string input);
```

```csharp
// Apps.Contracts/Signals — replaces AppInvoked and AppInvocationCompleted
public sealed record AppRequested(string Operation, string Input, Guid RequestId) : Signal;
```

`Request` validates the operation against the manifest (as `Invoke` does), bounds the input at
64 KiB, stamps a fresh `RequestId`, and `Save`s with the signal. No invocation list, no waiters,
no cap. `AppState.Invocations` and `AppCalls` (`Ask`, `AwaitResponse`) are removed; `AppInvocations`
(the `Invocations(...)` helper) is removed.

Surface edges:

- HTTP: `POST /brains/{id}/apps/{name}/request {operation, input}` answers 202 with `{requestId}`.
  `POST .../ask` is removed. `AgentPrompts` and the Creator teach `Request`.
- Shell: opening an app calls `Request("open", docked ? "docked" : "")` and renders the app's
  surface neuron `{brain}/apps/{name}/surface`, which every shipped app already composes. The
  window for the undocked case is opened by the script through `IWorkspace`, as today.
- Agents and other apps: `brain.Get<IApp>(key).Request(op, input)`, then read the neuron the app's
  spec names. There is no reply signal; a result is neuron state.
- An app that needs to re-trigger itself (`refresh` in DataVisualizer, `refresh-connections` in
  Settings) calls `app.Request(...)` and the reacting synapse handles it; nothing awaits it.

### 2. One assembly per app, deployed by hash

`AppNeuron` compiles the bundle's `.cs` files (synapses and shared `lib/*.cs`) into one assembly and
assigns its hash to the brain runtime:

```csharp
private async Task Deploy()
{
    var sources = Snapshot.Files.Where(f => f.Key.EndsWith(".cs", StringComparison.Ordinal))
        .ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
    if (sources.Count == 0) { await Runtime.Retire(this.GetPrimaryKeyString()); return; }
    var compiled = compiler.Compile(sources);
    if (!compiled.Success) { throw new InvalidOperationException("Does not compile: " + Describe(compiled.Errors)); }
    await assemblies.Put(compiled.Assembly, CancellationToken.None);
    await Runtime.Assign(this.GetPrimaryKeyString(), compiled.Assembly.Hash, Configuration(), Snapshot.Owner!);
}
```

Every mutation that touches a `.cs` file or a setting ends with `await Deploy();` (`WriteBehavior`,
`CompleteBuild`, `Configure`, `Rollback`, `Import`, `Replace`). `Delete` calls `Runtime.Retire`.
A write whose program does not compile fails the write with the compiler's message; nothing is
stored "red".

Removed: `IDeployableRuntime`, `RuntimeDeployment`, `CSharpAppRuntime`, `AppProgramKey`,
`ICSharpAppBinding` and `CSharpAppBindingGuard`, `BindApp`/`RequireBoundAccess`/`AppId`/`AppBrain`
on `CSharpFileNeuron`; from `AppState`: `StorageFiles`, `DeployedFiles`, `BehaviorErrors`,
`ProgramGeneration`, `Invocations`. `IAppStorageHook` keeps working on the app key alone
(`OnRetired(brain, appKey)`), since storage is per app, not per file.

The compiler is the existing `CSharpScriptCheck`, extended to compile a set of files into one
assembly where each `synapses/*.cs` is a top-level program (its own entry point) and `lib/*.cs`
are ordinary classes shared by all of them. Publish keeps `IScriptSandbox.Check` as the gate.

### 3. The runtime grain assigns programs, not files

`BehaviorAssignment` becomes:

```csharp
// Owner is the app key (or a standalone file key); Principal is who the program acts as.
internal sealed record ProgramAssignment(string Owner, string Hash, Dictionary<string, string> Settings, CallerContext Principal);
```

`Assign(owner, hash, settings, principal)` replaces the file-keyed `Assign`; `Retire(owner)` likewise.
A subscription is keyed by behavior `{hash}/{synapse path}`; `Retire` and a re-`Assign` with a new
hash drop the old hash's subscriptions. Identity for calls (`Identity(session, behavior)`) resolves
the behavior's program by hash and answers its principal and owner (the app it acts as).

Removed: per-behavior `Status`, `Failures`, `LastFailure`, `ExitCode`; `Reported`, `ReloadLater`,
`MaximumFailures`, disarming; the contracts-fingerprint refusal on `Connected` (the hash includes the
contracts fingerprint, so a host built from other contracts simply cannot load the assembly and
reports that). `Describe` answers `(hash, running synapses, last error per synapse)` from the
sandbox's latest report, kept in memory on the grain, not persisted.

### 4. The sandbox loads one assembly per program

`AssignmentEvent(Owner, Hash, AssemblyUrl, SymbolsUrl, Settings, Synapses[])`. The session keeps
`owner → ProgramRun`; a `ProgramRun` holds one `BehaviorRun` per synapse entry point (as today,
one task each, `AmbientSession` with behavior id `{hash}/{path}`). An assignment with the hash
already running is a no-op; a new hash disposes the old run and starts the new one. A synapse that
exits with an error is logged, reported once, and restarted by the sandbox with the exponential
backoff it has now, forever: nothing is disarmed. `RetireEvent(Owner)` unloads the program.

### 5. Shipped apps

Every shipped app is rewritten to the one-reaction-per-file form: `AppRequested` loops instead of
`Invocations`/`Pending`/`Respond`; `WhenAny`-joined loops split into files; shared code moves to
`lib/`. Apps: `os/assistant`, `os/datavisualizer`, `os/settings`, `intochat/customerresearcher`,
`intochat/wordcount`. `CustomerResearcher.activity.cs`, which reads `ReadInvocations()` to project
history, keeps its own history in a neuron it owns.

## Reliability, by case

- Click while the sandbox is down: buffered in the subscription, delivered on reconnect, handled once.
- Write that does not compile: the write fails with the compiler's message.
- Sandbox dies: heartbeat silence, reminder relaunch, `Assignments` resends the programs, synapses
  re-subscribe, `Subscribed` pushes what waited.
- Two mirrors remain, both legitimate: the app's bundle (truth) and the runtime's `owner → hash`
  (what to run), kept equal by one idempotent `Assign` on every bundle change and resent on every
  reconnect.

## Not in scope

Cross-brain subscription scopes, the renderable palette, run-token tightening (open fronts).
Renaming `IKernel` to `IBrain`. The loop-form `On<T>` API stays as is.

## Verification

Unit: Apps and CSharp module suites rewritten for `Request`/`AppRequested`, program assignment and
the sandbox's program runs. E2E: `DigitalBrain.OS.Tests.E2E/BrainRuntimeFacts` drives a shipped
app through `Request` and reads the surface neuron. Live: `aspire run`, open the assistant through
the shell, click "New conversation" twice, see two `AppRequested`-free `ButtonClicked` reactions in
traces and the transcript cleared each time; write a non-compiling synapse through the Creator and
see the write refused.
