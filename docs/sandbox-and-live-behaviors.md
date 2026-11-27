# Sandbox and live behaviors

Code made at run time is C# source held in a brain, not in this repository. The kernel compiles it, checks it and runs it. There are two shapes of such code, and they run in different places:

- A **unit** is synapse source: state records and `[Synapse]` methods. See [neurons and synapses](../framework/docs/neurons-and-synapses.md). A deployed unit is loaded inside the kernel process, behind an allow-list.
- An **app package** is a class that derives from `BrainApp<TState, TSettings>` in `src/IntoChat.OS/Sdk`. It runs in the sandbox worker, outside the kernel process.

Both are compiled by the same compiler.

## Compiling

`SynapseCompiler` (`framework/src/DigitalBrain.Simulation/RuntimeSynapses`) takes a `SynapseSource`: a key and its files.

- Only `.cs` files compile. Other files, such as `spec.md` and `scenarios.json`, travel with the source and count toward its hash.
- Every DigitalBrain analyzer and the synapse rules run over the code. Any error fails the build, with file, id, line and message. A crashed analyzer also fails the build.
- Unsafe code is off. The compiled assembly has a size cap.
- A successful build is cached by a hash of the key, the files and the references.

The Roslyn module in `src/Modules/Roslyn` is a different thing. It is a workspace over a solution on disk, used to read and edit a repository. It does not compile units.

## Units: deploy and load

`ISynapseStudio` is the neuron contract that deploys a unit; Ino and Creator both use it. It hands the source to the framework's `UnitDeployer`, which does this in order:

1. Compile. A failed build returns its diagnostics and nothing is loaded.
2. Verify. `SynapseCompiler.Verify` refuses a build that references anything outside the surface a synapse assembly may use.
3. Load. `UnitLoader` checks the allow-list again, before any assembly load, then loads the build into a collectible `SynapseLoadContext`. One context serves every brain that runs the same build.
4. Propose. The unit's deployment neuron records the build and the unit's needs. A new unit, or a new version that changes its needs, waits for your consent. Until then the old version stays live. The product never approves for you.

A unit's reactions run as synapses, on the signals they name. That is what makes a unit's behavior live: no restart is needed after a deploy.

Creator can run a unit's kept scenarios from `scenarios.json` through `ScenarioTranscripts` and show the transcript.

## App packages: the sandbox worker

`AppNeuron` in the Catalog module holds an installed app. When the app's files contain a `BrainApp` class or a package manifest, `AppPackageCompiler` compiles them and then reads the compiled metadata. It refuses the package if it uses a banned type, such as file, process, socket or `HttpClient` types.

The kernel then deploys the assembly through `ISandboxRuntimeBridge`, implemented by `HttpSandboxRuntimeBridge`. Where it runs depends on `SandboxOptions`:

- When `Url` is set, the kernel posts to a sandbox worker at that address, for example a container built from `src/IntoChat.OS.Sandbox/Dockerfile`. The worker serves `/apps/deploy`, `/apps/dispatch` and `/apps/unload`. When an enroll key is configured, each request must carry it.
- Otherwise the kernel starts `IntoChat.OS.Sandbox` as a child process with `--unit-host`. The two talk in JSON lines over standard input and output. The child opens no socket. `ChildEnvironmentPolicy` scrubs its environment so the kernel's secrets do not reach it. On Windows the child is attached to a job object.

Inside the worker, `AppSandboxHost` loads each app into its own collectible `AppLoadContext`. That context refuses to load process, networking, kernel and Orleans runtime assemblies. From the OS it admits `IntoChat.OS`, `IntoChat.OS.UI`, `IntoChat.OS.Contracts`, and each module's `Contracts` assembly (except identity's), never a module implementation. The `notifications@1` slot interface is in `IntoChat.OS.Contracts` for that reason.

## What a sandboxed app can do

On deploy the worker finds the app's handlers and reports them to the kernel:

- `[OnSignal]` handles a brain signal by name.
- `[Every]` runs on a schedule.
- `[OnCommand]` is a command the shell, an agent or MCP can call.
- `Render` returns the app's `View`.

The kernel dispatches signals, schedule ticks and commands to the worker, and publishes each new view to the shell.

The app reaches the brain only through `IntoChatClient`, which calls back to the kernel's sandbox edge, mapped by `SandboxEdgeEndpoints`:

| Endpoint | Purpose |
| --- | --- |
| `/edge/sandbox/invoke` | Call a capability: notifications, a folder slot, AI, a table, or another declared slot. |
| `/edge/sandbox/signals` | Publish a signal into the brain. |
| `/edge/sandbox/logs` | Write a log line. |

Every call carries an app token. `AppTokenService` issues the token at deploy time and binds it to one brain and one app key. The edge rejects a call whose token is invalid or does not match the brain and app it claims. A valid call runs as that app, so the brain's access rules apply to it. The app never holds credentials; see [modules](modules.md).
