digitalbrain: the framework for building AI-native apps. IntoChat OS is built on it.

# digitalbrain

You write neurons, apps and modules against a small vocabulary. A runtime package hosts them in a topology:
`local`, `local+cloud` or `cloud`.

## Quick start

```bash
dotnet new install DigitalBrain.Templates
dotnet new intochat-app -n MyApp
dotnet new intochat-module -n MyModule
cd MyApp && dotnet test
```

See [docs/getting-started.md](docs/getting-started.md).

## Vocabulary

- **Neuron**: an addressable unit with an id `brain/type/key` that runs one turn at a time.
- **Signal**: a typed fact a neuron publishes.
- **Synapse**: a durable route that carries one signal type from one neuron to another.
- **Brain**: the scope every neuron lives in; `@kernel` is the reserved system scope.
- **App**: a class that serves a person through views and reactions.
- **Module**: the unit of code that provides capabilities and declares what it needs.
- **Capability**: a versioned contract, such as `storage@1`, that a module offers and an app requires.
- **Connection**: a person's authorised use of a module in a brain.
- **Topology**: the shape a brain runs in: `local`, `local+cloud` or `cloud`.
- **Reaction**: a method that runs when a signal arrives.

## Packages

| Package | What it is |
|---------|------------|
| `DigitalBrain` | The vocabulary, attributes and SPI. No runtime, no Orleans. Bundles the analyzers. |
| `DigitalBrain.UI` | The UI view model (value trees). |
| `DigitalBrain.Testing` | Fakes and the test harness, including `SiloHost.Local()`. |
| `DigitalBrain.Analyzers` | Roslyn analyzers DB0001-DB0003. |
| `DigitalBrain.Templates` | `dotnet new intochat-app` and `dotnet new intochat-module`. |
| `DigitalBrain.Runtime.Orleans` | The Orleans runtime with the `local` and `local+cloud` topologies. |
| `DigitalBrain.Runtime.Orleans.Azure` | The `cloud` topology on Azure Storage and Key Vault. |
| `DigitalBrain.Runtime.Http` | The ASP.NET Core edge seam for module routes and session cookies. No Orleans. |

TestOS arrives in a later DigitalBrain release.

## Documentation

- [Overview](docs/index.md)
- [Getting started](docs/getting-started.md)
- [Packages and versioning](docs/packages.md)
- [Modules](docs/modules.md)
- [Topology](docs/topology.md)

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) and [SECURITY.md](SECURITY.md). Licensed under the [MIT licence](LICENSE).
