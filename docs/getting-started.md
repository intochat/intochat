# Getting started

This page shows how to run IntoChat from a clone of this repository.

## Prerequisites

- The .NET SDK pinned in `global.json` (`11.0.100`; prerelease builds are allowed).
- Docker, running Linux containers.
- Ollama on this computer, for the `local` profile.
- The Flutter SDK, for the shell. Without it, the `local` profile starts with no shell.

The `preflight` profile checks all of these. It also checks free disk space, free ports, WSL on Windows and whether a GPU is present.

## Start

Run from the repository root:

```
dotnet run intochat-os.cs preflight   # check the machine, change nothing
dotnet run intochat-os.cs local       # check, pull the model if missing, start against host Ollama
dotnet run intochat-os.cs             # default profile, includes the Flutter shell
```

`intochat.cs` is the same launcher under a shorter name. Both start the Aspire AppHost in `src/IntoChat.OS.AppHost`, which starts the kernel.

## Profiles

The first argument selects the profile.

| Profile | What it does |
|---|---|
| `default` | Starts the product with the Flutter shell. Used when no profile is given. |
| `headless` | Starts the product without the shell. |
| `local` | Runs preflight, pulls the model if it is missing, then starts with the `local` topology and Ollama on this computer. |
| `preflight` | Checks the machine and exits. |
| `reset` | Removes the local run state of this checkout. It lists what it will remove and asks for a typed "yes". |
| `<config>.json` | Starts with an extra configuration file that the AppHost loads. |

Anything after a bare `--` goes to the AppHost unchanged.

## Options

| Option | Meaning |
|---|---|
| `--model <tag>` | The Ollama model to use. The default is `qwen3.5:4b`; `qwen3.5:2b` is the smaller one. |
| `--endpoint <url>` | The Ollama address. The default is `http://localhost:11434`. |
| `--allow-registry` | Allow a model name that pulls from a registry other than the Ollama library. |
| `--pull` | With `preflight`: pull the model if it is missing. |
| `--json` | With `preflight`: print the report as JSON. |
| `--no-fail` | With `preflight`: exit with 0 even when a check fails. |
| `--skip-preflight` | With `local`: start without running the checks. |
| `--fake <file>` | Read the machine state from a fixture file instead of probing. Used by tests. |

`preflight` exits with 0 when the machine passes, 1 when a check fails and 2 for a usage error.

## What is running

- The kernel serves the shell on port 5080 and MCP on port 5081 by default (`IntoChatOS:Http:UiPort`, `IntoChatOS:Http:McpPort`).
- In Development the kernel also serves Microsoft Agent Framework DevUI at `/devui/` on its own address (the **DevUI** link on the kernel resource in the Aspire dashboard). The `ino` agent there is your current Ino conversation, so a chat in DevUI shows in Ino too; `approval-demo` is a sample workflow that waits for an approval. DevUI answers only on a loopback address, so it is not reachable through the shell port. Next to it, `/runs/` lists everything running in your brain (flows, agent turns, and operations such as a file write waiting for approval) and refreshes every two seconds; `/runs.json` gives the same list and `/runs.json?id=...` one run. `/brain/` draws the brain in 3D: every neuron and wire, with neurons that have a running or waiting run pulsing; drag to turn it. Set `IntoChatOS:DevUI:Enabled=false` to turn these pages off.
- Ino can run code blocks: small C# it writes, compiled under the sandbox's rules (no files, network or processes) and run once in the `sandbox` container, never in the kernel; each run shows on `/runs`.
- Ino also speaks AG-UI at `/agui/ino` (server-sent events, the same conversation and sign-in as the shell), on the kernel's address and through the shell port.
- Under a local AppHost, Ino reads the running app through Aspire's own MCP server (`aspire agent mcp`, started by the kernel on first use): resources, console logs, structured logs and traces, read only. The AppHost passes its path to the kernel as `IntoChatOS:Aspire:AppHostPath`; a cloud kernel never gets it.
- Local state lives under `~/.intochat`. Set `IntoChatOS:Home` to move it.
- The kernel listens on this computer only. On any other address (`IntoChatOS:Bind`, or a container) it refuses to start until you set `IntoChatOS:Auth:Username` and `IntoChatOS:Auth:Password`, which make you the single owner, or `IntoChatOS:Auth:AllowOpenOnNetwork=true`. Registering new accounts is switched off there in every case.
- `IntoChatOS:Topology` says where the brain runs. Unset means `local`. See [concepts](concepts.md).
- In the `local` and `local+cloud` topologies the modules that need containers are off by default. Turn one on with `IntoChatOS:Modules:<id>:Enabled=true`.

## Next

- [Concepts](concepts.md)
- [Architecture](architecture.md)
- [Contributing](../CONTRIBUTING.md), for the build and test commands.
