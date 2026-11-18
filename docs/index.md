# IntoChat

IntoChat is a personal AI operating system.

You have a brain: one person's or team's workspace, with its data, apps and connections. An agent called Ino does work for you in that brain. You choose where the brain runs.

## Pages

- [Getting started](getting-started.md): run the product from a clone.
- [Concepts](concepts.md): brain, where it runs, connection, capability, friends and groups, app, Ino.
- [Architecture](architecture.md): how the parts fit together.
- [Modules](modules.md): connectors and capabilities.
- [Ino and memory](ino-and-memory.md): the agent and what it remembers.
- [Sandbox and live behaviors](sandbox-and-live-behaviors.md): how apps made at runtime are run.
- [Deployment and Pulumi IaC](deployment.md): universal environment deployment, sandbox modes, and Pulumi synthesis.
- [Native AOT readiness](native-aot-readiness.md): Native AOT compilation, Roslyn DevKit boundary, and desktop packaging.

## The framework

IntoChat is built on DigitalBrain, which lives in `framework/` in this repository. DigitalBrain defines the vocabulary: neuron, signal, synapse, app, module. These pages use those words and do not explain them again. Read the [framework documentation](../framework/docs/README.md) first if they are new to you.

## The four parts

| Part | What it is | Where |
|---|---|---|
| Kernel | The host process that runs a brain on a machine | `src/IntoChat.OS.Kernel`, `src/IntoChat.OS.Sandbox` |
| System apps | Ino, Desktop, Settings, Tasks, Creator and the rest | `src/IntoChat.OS.Apps` |
| Modules | Connectors and capabilities | `src/Modules` |
| Shell | What the person sees | `ui` (Flutter), `src/IntoChat.OS.Desktop` (launcher) |

## Contributing

See [CONTRIBUTING.md](../CONTRIBUTING.md) at the repository root.
