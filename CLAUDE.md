# DigitalBrain

DigitalBrain is a personal "alive OS": an Orleans-based brain whose every capability is a
**neuron** (a stateful, addressable grain) publishing **signals** (typed facts stamped with their
publisher). Users program it at runtime by talking to it — the system writes itself — and what
they build is shareable, forkable and verified before it reaches anyone else. IntoChat
(`src/IntoChat`) is the product host; the Flutter shell
(`src/Modules/Google/Flutter/app/shell`) is its workspace UI.

Everything is made of four words. Anything that looks like a fifth concept is a design smell.

- **Neuron** — charts, timers, buttons, twitter accounts, packages. State lives here, never in a
  process.
- **Signal** — published only by its source neuron (`Signal.Publisher` is stamped by the kernel);
  provenance is inviolable, there is no publish-as-someone-else anywhere.
- **Behavior** — a C# script (`dotnet run app.cs` in the sandbox) that subscribes and acts through
  neuron contracts over the script edge. The unit of user-programmed logic.
- **Connector** — the bridge between an edge neuron and its external system: Gmail, Twitter, and
  also the Flutter shell, which is the *human's* connector. UI is not a layer; a button is an edge
  neuron whose external system is the person, so `On<Clicked>(button)` and `On<Posted>(elon)` are
  the same mechanism.

A **brain** is itself a neuron (`IBrain`); a user has one or more. The registry and every scope are
per brain: `BrainScope.CurrentId()` reads the brain from the kernel-stamped caller, and module
endpoints mount under `BrainRoutes.Group` → `/brains/{brainId}` (id validation + membership filter
applied once). "Workspace" survives only as the Flutter window-layout vocabulary.

The full ratified standard: `docs/superpowers/specs/2026-09-29-programmable-brain-vision-design.md`.

## The self-programming model

A user app is a **package** (`IPackage`, keyed `owner/name`) with git semantics — commit, fork,
pull, proposals, publish — containing:

```
app.spec.md      the program, in free natural language; "## Scenario: <name>" headings
tests.cs         thin deterministic checks using AppScenarioSuite and scratch installs
behaviors/*.cs   the implementation: one script per concern (plus legacy single Source as app.cs)
app.authoring.json optional stable scenario IDs and scenario-to-source associations
```

- **Spec is the program.** The Author agent turns a request into `app.spec.md`; the Builder agent
  compiles it into `tests.cs` and behaviors (`AppDraft` in DigitalBrain.Apps, prompts in
  `Apps/Authoring/AgentPrompts.cs`). The LLM is a compiler, never a runtime interpreter: no model sits in the
  publish gate or the signal path. There is no step vocabulary or sentence grammar — **the type
  system is the grammar**: a scenario is expressible exactly when its test compiles against the
  installed contracts (`ScriptContracts` exposes the composed modules' contracts assemblies).
- **Tests are the publish gate.** `IAppVerification` runs `tests.cs` through `ITestScriptRunner`
  (default: an ordinary sandbox script). The script installs GUID-scoped scratch apps, drives them
  through real contracts (`IApp`, `IScriptedLLM`, `IGroupChat`, …), prints one
  `dbtest:json <AppScenarioVerdict>` line per scenario and exits non-zero on failure. Legacy
  `dbtest:pass <name>` / `dbtest:fail <name>\t<message>` remains supported. Structured documents
  require exactly one result for every non-live scenario; results bind to stable scenario IDs.
  Green requires exit 0 AND ≥1 scenario AND all passed — silence never passes. Tests play
  the connector (scripted models, real timers); never a live model in the gate. `Publish` refuses
  any revision carrying a spec or tests until its verification is green. Verification runs
  scripts for every runtime, so it needs the host's C# sandbox (composing `CSharpModule` in a
  repository declares it).
- **Subscriptions are code, durability is the brain's.** A script's `brain.On<T>(source)` registers
  a durable, grain-side subscription on its `ICSharpFile` with a buffered watermark (64/subscription,
  oldest dropped). Processes are caches: idle scripts are reaped, a signal for a reaped script
  wakes a fresh run, which re-runs from the top and resumes at the watermark. Setup code before
  loops must be cheap and idempotent; run-to-run state lives in neurons; delivery is at-least-once,
  handlers dedup through neuron state. Derived state (a window arrangement, a summary) is re-derived
  wholesale, never incrementally patched. `Arm`/`Trigger` (run-per-signal with
  `brain.Trigger<T>()`) still exists for single-trigger files.
- **Install is per brain** (`IApp`): settings plus account slots bound to the installer's own
  connected accounts — a shared email summarizer runs against each user's Gmail. Each
  `behaviors/*.cs` becomes its own `ICSharpFile`; failure isolation is per behavior. The host binds
  these files to the installed `IApp` identity, shared across scenarios and generations. Standalone
  files retain file identity. Upgrade/reconfigure migrates legacy file-owned Postgres tables without
  changing their physical names or pinned origins. First-party
  apps ship through the same commit→verify→publish pipeline (`ShippedAppPublisher`, folders under
  `src/IntoChat/Apps/`).
- **Module contract:** a module ships contracts + signals + connector + fakes (Testing package) +
  renderables. Modules never ship step definitions or test-only methods on production interfaces.
  A module is a `Module` / `Module<TOptions>` subclass (in the module runtime assembly) whose `ModuleId` is passed to
  the base constructor — no attributes, no reflection; the catalog (`OsCatalog`) holds instances,
  and hosting/deployment adapters claim their module through `ModuleHosting<TModule>` /
  `ModuleDeployment<TModule>` generic parameters, discovered by scanning referenced assemblies.
  Options are ordinary configuration — code-declared values compile to flat keys under
  `DigitalBrain:Modules:{id}:Options:{Property}`, so the AppHost's own configuration (args, env,
  files) overrides any option by standard precedence and tests need no side channel; every
  module-owned key derives from `DigitalBrainConfiguration.ModuleSection`/`ModuleOptionsSection`
  (no private roots — `ModuleKeyGrammarFacts` enforces it, `LegacyConfigurationGuard` refuses
  retired roots at OS startup); `Validate()` runs at compile and at bind and checks only the
  options' own values — cross-ring facts resolve loudly at the owning ring's bind; no secrets in
  options — credentials belong to Connections (user authorization) or Integrations (deployment
  provisioning). Modules ship `ConnectorDefinition`s declaring auth methods, neurons/signals,
  and required/optional Integrations. `IntegrationSchema` describes provisioning fields under
  `DigitalBrain:Integrations:{id}:{Field}`. Platform resolves dependencies and delivers scoped,
  async `IModuleIntegrations<TModule>` handles; modules never mint platform callers, read credential
  configuration, or dial Integration grains. Unprovisioned required dependencies exclude a connector
  from `GET /connectors` with a startup diagnostic. Required host infrastructure fails binding.
  User Connections live at `/brains/{id}/connections`; deployment Integrations are `[PlatformOnly]`.
  Integrations are what the deployment provisions; Connections are what users authorize.


Open design fronts (do not improvise these): cross-app subscription scopes/permissions; the
renderable palette v1 / UiPart; run-token tightening.

## Working in this repo

- Layout: three framework rings, one per consumer. `DigitalBrain` = the vocabulary (the four
  words plus every script/client-facing contract; what `ScriptContracts` ships into the sandbox
  and what module `*.Contracts` projects and the Clients reference — never Orleans hosting).
  `DigitalBrain.Framework` (`DigitalBrain/Framework/`) = what module authors build on: neuron
  runtime, module declaration, http surface, sdk helpers, and the platform-facing interfaces.
  `DigitalBrain.Platform` = the credential-ring implementation scripts can never see
  (`[PlatformAssembly]`-marked); only host composers reference it. Script visibility is enforced
  at runtime (contract discovery + `[PlatformOnly]`), the project graph mirrors it.
- Build/tests per project (never the `.slnx` — Windows handshake bug):
  `dotnet test src/<path-to-test-project>`. After changes: build, run the relevant unit suites,
  and smoke with `aspire run` from `src/DigitalBrain/AppHost/DigitalBrain.AppHost` (all resources Healthy).
  Run module E2E suites alongside their module's unit suite; compile live-gated scenarios when
  their credentials are unavailable. IntoChat E2E contains composition, shipped-content routes,
  and the live-gated product golden journeys only. The former 18-minute monolith is dissolved.
- `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit` covers host composition and wiring; module behavior
  belongs in the module's test project. Packaging manifests are checked as one parsed module set.
- Flutter shell: `flutter analyze` and `flutter test` from `src/Modules/Google/Flutter/app/shell`.
  Wire-shape changes in C# records must be mirrored in the Dart screens in the same change.
- Persisted grain state: append `[Id(n)]`, never renumber; use concrete arrays, not interface
  lists — the memory grain storage's JSON serializer cannot round-trip C# collection-expression
  `IReadOnlyList` wrappers, and it only bites on reactivation.
- Code style: no boilerplate `/// <summary>`; names carry the meaning; small inline comments only
  for what the code cannot say (and keep them true — stale comments are bugs). Tests are facts
  with sentence-shaped names, `TestContext.Current.CancellationToken`, real grains over mocks
  (`ModuleTest.Create().WithModule<...>()`, `FakeSandbox`, `RecordingCSharpFile`, scripted LLMs).
- Grain hazards: no self-calls on non-reentrant grains (deadlock); one-way self-references
  (`AsReference<T>()`) for signal-triggered work; `ObserverManager` watches need lease renewal.
