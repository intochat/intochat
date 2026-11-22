# IntoChat OS

[![ci](https://github.com/intochat/intochat/actions/workflows/ci.yml/badge.svg)](https://github.com/intochat/intochat/actions/workflows/ci.yml)
[![license](https://img.shields.io/github/license/intochat/intochat)](LICENSE)

IntoChat OS is a personal AI operating system: a distribution built on [DigitalBrain](https://github.com/intochat/intochat/tree/master/framework).

You have a brain: your data, your apps, your connections and an agent called Ino that does work for you. You choose where the brain runs.

The vocabulary (neuron, signal, synapse, app, module) is defined by the DigitalBrain framework; read it [there](framework/README.md#core-vocabulary).

## What is in this repo

| Part | What it is | Where |
|---|---|---|
| Kernel | The host process that runs a brain on a machine | [`src/IntoChat.OS.Kernel`](src/IntoChat.OS.Kernel), [`src/IntoChat.OS.Sandbox`](src/IntoChat.OS.Sandbox) |
| System apps | Ino, Desktop, Settings, Tasks, Creator and the rest, written as ordinary DigitalBrain apps | [`src/IntoChat.OS.Apps`](src/IntoChat.OS.Apps) |
| Modules | Connectors and capabilities: GitHub, Google, Postgres, AI | [`src/Modules`](src/Modules) |
| Shell | What the person sees | [`ui`](ui) (Flutter), [`src/IntoChat.OS.Desktop`](src/IntoChat.OS.Desktop) (launcher) |

## Where a brain runs

1. **Sign in:** your brain runs in IntoChat Cloud.
2. **Run on this computer:** in-memory to try, persistent to keep.
3. **Connect to your own server:** the same kernel in a container.

The shell speaks one protocol. The only difference between the three is the brain's address. On Android and iOS the shell connects to a brain elsewhere; the kernel does not run on phones.

## Quick start

Prerequisites: the .NET SDK pinned in [`global.json`](global.json) (.NET 11 preview), Docker (Linux containers) and, for the local profile, [Ollama](https://ollama.com) running on the host.

```
dotnet run intochat.cs preflight   # check the machine, change nothing
dotnet run intochat.cs local       # start a lean profile against host Ollama
dotnet run intochat.cs             # default profile, includes the Flutter shell
```

Run from the repository root. Profiles and options are described in the header of [`intochat.cs`](intochat.cs).

## The framework

The DigitalBrain framework (neurons, signals, synapses, brains, apps and modules) lives in [`framework/`](framework/README.md) ([`intochat/intochat`](https://github.com/intochat/intochat/tree/master/framework)) and is consumed directly by project reference (and packed as `DigitalBrain.*` NuGet packages).

## Module model

A module is a folder and a `Module` class. Create one with `dotnet new digitalbrain-module`; adding it needs zero edits outside its folder, because the catalog discovers modules by reflection. Every module README lists its options and secret names from the code that binds them (`ModuleDocumentationFacts` fails when they drift; run it with `UPDATE_DOCS=1` to rewrite them). See [modules](docs/modules.md).

## .NET Aspire integration

An Aspire AppHost can run the IntoChat OS container through `IntoChatOSHostingExtensions` (`IntoChat.OS.Aspire.Hosting`, in [`src/IntoChat.OS.AppHost/Hosting/IntoChatOSHostingExtensions.cs`](src/IntoChat.OS.AppHost/Hosting/IntoChatOSHostingExtensions.cs)):

```csharp
using IntoChat.OS.Aspire.Hosting;

var os = builder.AddIntoChatOSContainer("os", httpPort: 5080)
    .WithImageTag("latest")
    .WithTopology("local")
    .WithBootstrapOwner("operator", "bootstrap-secret")
    .WithEnvironment("IntoChatOS__Home", "/app/home");
```

`AddIntoChatOSContainer` defaults to `image: "ghcr.io/intochat/os"` and `tag: "latest"`, exposes the `"http"` endpoint targeting port `8080` (bound to `httpPort` when supplied), and registers an HTTP health check on `/health`. `WithBootstrapOwner` sets `IntoChatOS__Auth__Username` and `IntoChatOS__Auth__Password` (required when listening beyond loopback without a sign-in module unless `IntoChatOS__Auth__AllowOpenOnNetwork=true` is set), and `WithTopology` sets `IntoChatOS__Topology`. `IntoChat.OS.AppHost` is `IsPackable=true` and is consumed as a NuGet package by external AppHosts (such as IntoChat Cloud).

### Your own AppHost

```csharp
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;

builder.AddIntoChatOS("Modules")
    .WithAI(ai => ai.Cloud(c => c.Roles(r => r.Chat(KnownModels.DeepSeekV41Flash))))
    .AddKernel("../IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj");
```

## Packages and images

The [release workflow](.github/workflows/release.yml) produces:

- **NuGet packages**: packs [`framework/DigitalBrain.slnx`](framework/DigitalBrain.slnx) and [`framework/templates/DigitalBrain.Templates/DigitalBrain.Templates.csproj`](framework/templates/DigitalBrain.Templates/DigitalBrain.Templates.csproj) (`DigitalBrain.*` packages and `DigitalBrain.Templates`) and publishes them on release to GitHub Packages (`https://nuget.pkg.github.com/intochat/index.json`), `nuget.org` (`https://api.nuget.org/v3/index.json`, when configured), and GitHub Release assets.
- **Self-contained kernel binary**: publishes [`src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj`](src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj) (`linux-x64`, self-contained) as `kernel-bin.tar.gz`.
- **Container image**: builds the unified OS image (kernel, embedded Flutter web shell, and ArtCraft web bundles) from [`src/IntoChat.OS.Kernel/Dockerfile`](src/IntoChat.OS.Kernel/Dockerfile) and pushes `acrintochattest.azurecr.io/os:<VERSION>` and `acrintochattest.azurecr.io/os:latest` alongside `release-manifest.json` (while [`Container.pubxml`](src/IntoChat.OS.Kernel/Properties/PublishProfiles/Container.pubxml) and `AddIntoChatOSContainer` reference `ghcr.io/intochat/os`, and [`.github/workflows/artcraft-images.yml`](.github/workflows/artcraft-images.yml) publishes prebuilt `ghcr.io/intochat/os-artcraft-<app>-<mcp|web>` images).
- **Windows desktop bundle**: runs [`eng/release/package-release.ps1`](eng/release/package-release.ps1) (`win-x64`) to attach `intochat-<VERSION>-win-x64.zip`, `SHA256SUMS`, `latest.json`, and `winget/IntoChat.OS.yaml` to the GitHub Release.

Releases are tagged `v<VERSION>` from the [`VERSION`](VERSION) file (applied across the solution via [`Directory.Build.props`](Directory.Build.props)). See the [changelog](CHANGELOG.md).

## Documentation

Concepts, architecture and guides: [`docs/`](docs/index.md). The framework has its own: [`framework/docs/`](framework/docs/README.md).

## Contributing and license

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). Licensed under the terms in [LICENSE](LICENSE).
