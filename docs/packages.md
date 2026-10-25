# Packages and versioning

This repository publishes exactly ten packages to nuget.org:

| Package | Depends on Orleans | Purpose |
|---------|-------------------|---------|
| `DigitalBrain` | no | Vocabulary, attributes, SPI; bundles the analyzers |
| `DigitalBrain.UI` | no | UI view model |
| `DigitalBrain.Testing` | no | Fakes, harness, `SiloHost.Local()` entry point |
| `DigitalBrain.Analyzers` | no | Roslyn analyzers DB0001-DB0003 |
| `DigitalBrain.Templates` | no | `intochat-app` and `intochat-module` templates |
| `DigitalBrain.Runtime.Orleans` | yes | Orleans runtime, `local` and `local+cloud` topologies |
| `DigitalBrain.Runtime.Orleans.Azure` | yes | `cloud` topology on Azure Storage and Key Vault |
| `DigitalBrain.Capabilities.Storage` | no | The `storage@1` capability contract |
| `DigitalBrain.Capabilities.Secrets` | no | The `secrets@1` capability contract |
| `DigitalBrain.Capabilities.Identity` | no | The `identity@1` capability contract (`IIdentity`, `IGrants`, `ISignInHandler`) |

## Versions

- The `VERSION` file holds the next release, for example `0.6.0`. All ten packages share it.
- Every push to `main` that passes the `ci` gate publishes `<VERSION>-preview.<run number>` of all ten packages.
- A release publishes `<VERSION>` without a suffix. The release tag must be `v<VERSION>`; the release workflow
  refuses any other tag.
- A local inner-loop build uses `<VERSION>-preview.local.<yyMMddHHmmss>`; see [CONTRIBUTING](../CONTRIBUTING.md).

Packages are published from CI through nuget.org Trusted Publishing; no API key is stored in the repository.
