# IntoChat OS

[![ci](https://github.com/intochat/os/actions/workflows/ci.yml/badge.svg)](https://github.com/intochat/os/actions/workflows/ci.yml)
[![license](https://img.shields.io/github/license/intochat/os)](LICENSE)

A runtime for AI apps and agents, built on .NET, Orleans and Aspire.

## How it works

Everything in the runtime is a **neuron**, a **signal** or a **synapse**.

- **Neuron**: a stateful, addressable capability that lives in a brain (an ownership scope): a timer, an inbox, a running program. Durable state lives in neurons, never in a process.
- **Signal**: a typed fact a neuron publishes. Signals are the only thing that flows between neurons. The runtime stamps each one with its publisher, so provenance cannot be forged.
- **Synapse**: a durable connection saying "this neuron receives every signal of this kind from that neuron", surviving restarts.

```mermaid
flowchart LR
    A["Neuron A<br/>publishes Number"] -- "signal" --> S(("synapse"))
    S -- "signal" --> B["Neuron B<br/>reacts, publishes Heard"]
```

A neuron is an Orleans grain that derives from `Neuron`. This shape is taken from the integration tests: an emitter publishes `Number` signals, and a listener subscribes to them and publishes `Heard` in response.

```csharp
[GenerateSerializer] public sealed record Number([property: Id(0)] int Value) : Signal;
[GenerateSerializer] public sealed record Heard([property: Id(0)] int Value) : Signal;

[Alias("test.emitter")]
public interface ITestEmitter : INeuron { Task Emit(int value); }

[GrainType("test-emitter")]
public sealed class TestEmitter : Neuron, ITestEmitter
{
    public Task Emit(int value) => PublishAsync(new Number(value));
}

// Inside another neuron: subscribe to the emitter's Number signals.
var source = ServiceProvider.GetRequiredService<IIntoChatOS>().Get<ITestEmitter>(sourceId);
var subscription = await ServiceProvider.GetRequiredService<IBrainSubscriptions>().SubscribeAsync<Number>(source);
await foreach (var number in subscription.ReadAllAsync()) { await PublishAsync(new Heard(number.Value)); }
```

Full listings: [`TestNeurons.cs`](src/IntoChat.OS.Client/IntoChat.OS.Client.Orleans.Tests.Integration/TestNeurons.cs). Contracts: [`INeuron`](src/IntoChat.OS/Neurons/INeuron.cs), [`Signal`](src/IntoChat.OS/Signals/Signal.cs), [`Synapse`](src/IntoChat.OS/Synapses/Synapse.cs).

## Quick start

Prerequisites: the .NET SDK pinned in [`global.json`](global.json) (.NET 11 preview), Docker (Linux containers) and, for the local profile, [Ollama](https://ollama.com) running on the host.

```
dotnet run intochat-os.cs preflight   # check the machine, change nothing
dotnet run intochat-os.cs local       # start a lean profile against host Ollama
dotnet run intochat-os.cs             # default profile, includes the Flutter shell
```

Run from the repository root. Profiles and options are described in the header of [`intochat-os.cs`](intochat-os.cs).

## Packages and images

- NuGet packages (GitHub Packages): `https://nuget.pkg.github.com/intochat/index.json`
- Container images: `ghcr.io/intochat/os` (kernel), `ghcr.io/intochat/os-ui` (Flutter web shell), `ghcr.io/intochat/os-sandbox` (C# sandbox)

Releases are tagged `v<VERSION>` from the [`VERSION`](VERSION) file. See the [changelog](CHANGELOG.md).

## Documentation

Concepts, architecture and guides: [intochat/os.docs](https://github.com/intochat/os.docs).

## Contributing and license

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). Licensed under the terms in [LICENSE](LICENSE).
