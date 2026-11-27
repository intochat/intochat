# Testing Tiers

DigitalBrain organizes automated verification into three tiers enforced by [`TestTierFacts`](../tests/DigitalBrain.Architecture.Tests.Unit/TestTierFacts.cs) in `DigitalBrain.Architecture.Tests.Unit`. Every test project in `DigitalBrain.slnx` ends in `.Tests.Unit`, `.Tests.Integration`, or `.Tests.E2E`, runs on `Microsoft.Testing.Platform` with xUnit v3 (`xunit.v3.mtp-v2`), and contains at least one `[Fact]` or `[Theory]`.

## 1. Unit (`*.Tests.Unit`)

Unit test projects run fast, deterministic facts in-process without starting an Orleans silo or referencing `DigitalBrain.Runtime.Orleans` or `DigitalBrain.Testing.Silo` (except the dedicated runtime unit assembly `DigitalBrain.Runtime.Orleans.Tests.Unit`):

- **In-process simulation (`DigitalBrain.Simulation`)**: `TestOS.Local()` hosts neurons on an in-memory journal with a manual clock (`os.Clock.Advance`), capability fakes (`With<TCapability>`, `WithStorage`, `WithSecrets`, `WithIdentity`), and `os.Idle()` drain checks; `NeuronHarness.Create<TNeuron>` exercises single-neuron activations with `FakeNeuronContext`.
- **Screen baselines, view fixtures, and module floors (`DigitalBrain.Testing`)**: `ScreenBaseline` generates one theory row per `[On]` reaction of a `Screen<TState>`, `ViewFixtures.Verify` compares rendered `View` JSON against stored fixtures (writing on `DIGITALBRAIN_UPDATE_VIEWS=1`), and `ModuleOnTestOSFacts<TModule>` verifies every `[Neuron]` registers and every contract binds on `TestOS`.
- **UI scenarios (`DigitalBrain.Testing`)**: a `UiScenario` is a list of steps on one app's top-level screen (`Act`, `Expect`, `ExpectShown`), each addressed by key path. `ScenarioRunner.Run` plays it through an `IScenarioDriver` and returns a `RunVerdict` with the driver's evidence for a failing step. `ScenarioJson` writes the shared file every driver reads. The OS has three drivers: a kernel driver on `TestOS`, a Flutter driver (`ui/test/scenarios/flutter_driver.dart`) and a browser driver (`BrowserDriver`, nightly).
- **Roslyn analyzers (`DigitalBrain.Tests.Unit`)**: `Verify<TAnalyzer>` checks analyzer diagnostics and code fixes over source snippets with no runtime dependency.
- **Architecture and template smoke checks**: `DigitalBrain.Architecture.Tests.Unit` checks ring boundaries (`VocabularyRingFacts`, `CoreHasNoOrleansFacts`, `ReferenceClosureFacts`), package rules, delivery-contract invariants (`DeliveryContractFacts`), and tier rules (`TestTierFacts`), while `DigitalBrain.Tests.Unit` (`TemplateSmokeFacts`) validates template scaffolding.

## 2. Integration (`*.Tests.Integration`)

Integration test projects exercise real runtime components in-process (or start real HTTP/template processes) without Aspire or Playwright:

- **Dual-runtime parity (`DigitalBrain.Testing.Silo`)**: `BothRuntimesFixture` executes the same fact body (`NeuronRuntimeKinds.Both`, covering `NeuronRuntimeKind.TestOS` and `NeuronRuntimeKind.Silo`) against both `TestOS.Local()` and an in-process Orleans silo started via `SiloHost.Local()` (`LocalSiloHostFactory` in `DigitalBrain.Runtime.Orleans`), as used in `Calculator.Tests.Integration` and `DigitalBrain.Runtime.Orleans.Tests.Integration`.
- **Capability conformance kits (`DigitalBrain.Testing`)**: Sealed conformance suites (`StorageConformance<TStorage>`, `SecretsConformance<TSecrets>`, `IdentityConformance<TIdentity>`, `ConnectionsConformance<TConnections>`) verify real capability implementations against the framework contracts.
- **HTTP edge and templates**: `DigitalBrain.Runtime.Http.Tests.Integration` exercises `JournalTailEndpoints`, `MapModuleHttpSurfaces`, and storage content endpoints over an in-process `WebApplication` on loopback, and `DigitalBrain.Templates.Tests.Integration` (`TemplateInstanceFacts`) packs local `DigitalBrain.*` packages, instantiates `digitalbrain-app`, builds with warnings as errors, and runs its baseline tests.
- **Cloud emulator conformance (`DigitalBrain.Runtime.Orleans.Azure.Tests.Integration`)**: Verifies Azure blob lease ownership, cluster composition, and cloud storage/Key Vault configuration against Azurite via `AzuriteFixture` (`AZURITE_CONNECTION_STRING` or Docker `mcr.microsoft.com/azure-storage/azurite`; locally skips when Docker is unavailable, and fails if `CI=true` without Azurite).

## 3. End-to-End (`*.Tests.E2E`)

`TestTierFacts.EndToEndTierIsOnlyFrameworkHostConformance` restricts the framework's E2E tier to `DigitalBrain.Framework.Tests.E2E`:

- **Cross-target host conformance (`FrameworkHostConformanceFacts`)**: Derives from `HostConformanceFacts` (`DigitalBrain.Testing.Silo`) and runs the full host-conformance suite across `TestOSTarget`, `LocalOrleansTarget`, and `AzuriteOrleansTarget` (sharing `AzuriteFixture` against Azurite).
- **No silent skips**: `DigitalBrain.Framework.Tests.E2E` enables `Microsoft.CodeAnalysis.BannedApiAnalyzers` with `BannedSymbols.txt` banning `Assert.Skip(string)` so E2E host conformance facts must execute completely and can never be skipped.
