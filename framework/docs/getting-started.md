# Getting started

You need the .NET SDK pinned in `global.json` (.NET 11).

## Install the templates

```bash
dotnet new install DigitalBrain.Templates
```

Prerelease builds are published as `0.6.0-preview.<N>`; pass `DigitalBrain.Templates::<version>` to install a
specific one.

## Create an app

```bash
dotnet new intochat-app -n MyApp
cd MyApp
dotnet test
```

The app references `DigitalBrain` and has a neuron and its tests.

## Create a module

```bash
dotnet new intochat-module -n MyModule
cd MyModule
dotnet test
```

A module declares what it offers and what it needs; see [Modules](modules.md).

## Run a neuron in a test silo

Reference `DigitalBrain.Testing` and `DigitalBrain.Runtime.Orleans`, then:

```csharp
await using var host = await SiloHost.Local();
// host.Services resolves IGrainFactory; host.Restart() stops and starts over the same Home.
```

`SiloHost.Local()` starts an in-process silo on the `local` topology in a temporary Home folder and deletes it on
dispose.
