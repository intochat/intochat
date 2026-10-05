# Framework restructuring — ratified 2026-10-05

## The finding

The repo carried two layering systems for one boundary. The boundary that matters — *scripts
never see the credential ring* — is enforced at runtime: contract discovery
(`ModuleInventory.ContractAssemblies` + `ContractVocabulary`), `[PlatformAssembly]` on the
credential implementation, `[PlatformOnly]` per contract. The eight-project compile-time lattice
(`DigitalBrain` → `Contracts` → `Kernel` → `Kernel.AspNetCore` → `Sdk` → `Platform.Contracts` →
`Platform` → `OS`) pretended to be that boundary but enforced nothing, so every concept smeared
across rings (Integrations over four projects, Data over three, IntentContext halved).

## The principle

One package per consumer. The runtime enforces the boundary; the project graph mirrors the
audiences:

| Package | Consumer | Contents |
|---|---|---|
| `DigitalBrain` | scripts (via ScriptContracts), module `*.Contracts`, Clients | the four words + Data, Types, Signals, Edge.V1, Integrations refs, Enforcement shapes, names, configuration grammar, `ModuleId`, `IModuleOptions`, platform attributes |
| `DigitalBrain.Framework` | module authors | neuron runtime, `Module` declaration (`Configure(ISiloBuilder)`), composition, enforcement machinery, http surface, sdk helpers, platform-facing interfaces (`ISecrets`, `IntegrationDefinition`, identity options) under `DigitalBrain.Framework.Platform.*` |
| `DigitalBrain.Platform` | host composers only | credential-ring implementation; `[PlatformAssembly]`-marked |
| `DigitalBrain.Client` / `.Client.Orleans` | external apps | unchanged; vocabulary-only dependencies |

The product/framework line runs **above** Platform: `DigitalBrain.OS`, `DigitalBrain.AppHost`,
`src/IntoChat` and the Flutter shell are the product; everything above is the framework a third
party could compose their own brain from.

Rules the migration locked in (guarded by `BoundaryFacts` + `ExtractionFacts`):

- The vocabulary never references Orleans hosting; `Module.Configure` lives in the Framework.
- Module declarations live in module **runtime** assemblies; `*.Contracts` assemblies reference
  only the vocabulary (and each other).
- Nothing below the OS references the Platform implementation; module code reaches credentials
  only through the Framework's platform-facing interfaces.

## Deferred product-string leaks (named violations, each needs its own change)

1. `"intochat.account"` / `"intochat.brain"` identity claims (Platform) — baked into issued
   cookies; needs a dual-read window.
2. `"intochat-identity-directory"`, `"intochat-grants-"` neuron ids (Framework platform surface)
   — persisted state; needs a data migration.
3. `"x-intochat-*"` schema extension keys (Framework `SchemaExtensions`) — wire format shared
   with the Flutter shell; rename both sides together.

`"intochat.caller-context"` (transient Orleans RequestContext key) was renamed to
`"digitalbrain.caller-context"` in this migration.

## Pre-existing reds discovered and not caused by this work

- `AppDraftFacts` (12 tests): "Private settings cannot override host-owned configuration" —
  reproduced at a768af559, before any restructuring commit. Follow-up task spawned.
