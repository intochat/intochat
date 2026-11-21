# IntoChat.OS & DigitalBrain Test Infrastructure Architecture

## 1. Philosophy & Platform

The test suite runs on **.NET 11** with **xUnit v3** and **Microsoft.Testing.Platform (MTP v2)** (`xunit.v3.mtp-v2`). Every test project compiles as an executable test module (`OutputType=Exe`) with `TreatWarningsAsErrors=true` enforced repository-wide.

Core principles:
- **Opaque-box verification**: Test behavior through public contracts, views, signals, and HTTP/browser boundaries rather than private internals.
- **Runtime parity**: Every app screen and plain neuron must behave identically on both the in-memory `TestOS` runtime and an in-process Orleans silo.
- **Strict layer boundaries**: Framework testing libraries (`DigitalBrain.Testing`, `DigitalBrain.Testing.Silo`) remain completely decoupled from `IntoChat.OS` product code.

---

## 2. DDD Test Pyramid

Tests are organized by scope and feedback speed across both `framework/tests/` and `tests/`:

```
                    ┌──────────────────────────────┐
                    │   Browser (Playwright UI)    │  IntoChat.OS.Tests.Browser
                    ├──────────────────────────────┤
                    │   E2E (Aspire / Full Stack)  │  *.Tests.E2E
                    ├──────────────────────────────┤
                    │   Integration (Silo / HTTP)  │  *.Tests.Integration
                    ├──────────────────────────────┤
                    │  Unit & Architecture (Fast)  │  *.Tests.Unit / *.Architecture.Tests.Unit
                    └──────────────────────────────┘
```

| Tier | Suffix / Project | Runtime & Scope |
|---|---|---|
| **Unit** | `*.Tests.Unit` | In-memory `TestOS`, pure domain logic, view rendering, analyzers, capabilities. Sub-second feedback, zero network or silo startup. |
| **Architecture** | `DigitalBrain.Architecture.Tests.Unit`, `IntoChat.OS.Architecture.Tests.Unit` | Assembly dependency rules, layer isolation, public API and namespace invariants. |
| **Integration** | `*.Tests.Integration` | In-process Orleans silo (`BothRuntimesFixture`), HTTP runtime endpoints, templates, and cross-module contracts. |
| **E2E** | `DigitalBrain.Framework.Tests.E2E`, `IntoChat.OS.Tests.E2E` | Full-stack multi-process / Aspire-hosted workflows, HTTP session drivers, and end-to-end user journeys. |
| **Browser** | `IntoChat.OS.Tests.Browser` | Real browser automation against the live web/desktop UI surface. |

---

## 3. DigitalBrain vs. IntoChat.OS Testing Boundary

| Layer | Projects | Responsibilities | Constraints |
|---|---|---|---|
| **Framework (Orleans-free)** | `framework/src/DigitalBrain.Testing` | `TestOS`, `INeuronRuntime`, `NeuronRuntimeKind`, `AppSession<TScreen>`, `IAppSessionDriver`, `DirectNeuronSessionDriver`, `ScreenBaseline`, capability conformance suites. | Must **never** reference Orleans or `IntoChat.OS.*`. |
| **Framework (Silo)** | `framework/src/DigitalBrain.Testing.Silo` | `BothRuntimesFixture`, `AppConcurrencyAndIsolationContract<TScreen, TFixture>`, host conformance facts. | References `DigitalBrain.Testing` and `DigitalBrain.Runtime.Orleans`; must **never** reference `IntoChat.OS.*`. |
| **Product Support** | `tests/IntoChat.OS.Tests.Support` | `AspireHttpSessionDriver`, `VisualDebugWindow`, Aspire host fixtures, OS-specific test helpers. | Product-only; wires `IntoChat.OS` hosts into `DigitalBrain.Testing` abstractions. |

---

## 4. Two-Axis TDD App Testing Framework

App screens are tested along two orthogonal axes:

### Axis 1: Runtime (`DigitalBrainRuntime` / `NeuronRuntimeKind`)
- **`InMemory` (`NeuronRuntimeKind.TestOS`)**: Deterministic in-memory `TestOS` with a controlled clock for instant unit and TDD loops.
- **`Orleans` (`NeuronRuntimeKind.Silo`)**: Real in-process Orleans silo (`SiloHost.Local`) with `BrainLog` storage and real-time scheduling.

### Axis 2: Session Driver (`IAppSessionDriver`)
- **`DirectNeuronSessionDriver`** (`DigitalBrain.Testing`): Drives `IScreen` directly over `INeuronRuntime` (`TestOS` or `Silo`) via `AppSession.OpenAsync<TScreen>(runtime)`.
- **`AspireHttpSessionDriver`** (`IntoChat.OS.Tests.Support`): Drives the same `AppSession<TScreen>` API over live HTTP against an Aspire-hosted `IntoChat.OS` instance.

### Visual Debugging (`VisualDebugWindow`)
Set `INTOCHAT_TEST_VISUAL=desktop` to attach `VisualDebugWindow` during interactive test runs and inspect live rendered screen views as `AppSession<TScreen>` steps execute.

### Concurrency & Isolation Contract (`AppConcurrencyAndIsolationContract<TScreen, TFixture>`)
Located in `DigitalBrain.Testing.Silo`, any screen test class can inherit from `AppConcurrencyAndIsolationContract<TScreen, TFixture>` (where `TFixture : BothRuntimesFixture`) to automatically verify across both `TestOS` and `Silo`:
1. **`ColdStart_RendersValidInitialView`**: Opening a fresh `AppSession.OpenAsync<TScreen>(runtime)` produces a non-null `CurrentView` with `Generation >= 1`.
2. **`IsolatedKeys_DoNotShareState`**: Two sessions opened with distinct keys render independently without cross-session state leakage.
3. **`ConcurrentActivation_SameGeneration_AtMostOneSucceeds`**: When the subclass overrides `ConcurrentActivationKey`, 10 callers race concurrently on the same initial generation; at most 1 succeeds and all others fail with `ActivationRefusedException` (`Reason == ActivationRefusedException.Stale`).

---

## 5. Running Tests with Microsoft.Testing.Platform (MTP)

MTP v2 uses `--test-modules`, `--filter-class`, and `--filter-trait` (not legacy VSTest `--filter "Category=..."`).

```bash
# Build first (or omit --no-build to build automatically)
dotnet build IntoChat.slnx -c Release

# Run a specific test module (built executable)
dotnet test --test-modules "artifacts/bin/IntoChat.OS.Tests.Unit/release/IntoChat.OS.Tests.Unit.dll"

# Run multiple test modules via glob
dotnet test --test-modules "artifacts/bin/*.Tests.Unit/release/*.Tests.Unit.dll"

# Filter by class name
dotnet test --test-modules "artifacts/bin/IntoChat.OS.Tests.Integration/release/IntoChat.OS.Tests.Integration.dll" \
  --filter-class "*AppSession*"

# Filter by xUnit trait
dotnet test --test-modules "artifacts/bin/IntoChat.OS.Tests.Integration/release/IntoChat.OS.Tests.Integration.dll" \
  --filter-trait "Category=Smoke"

# Run a single test project directly with MTP flags after `--`
dotnet run --project tests/IntoChat.OS.Tests.Unit/IntoChat.OS.Tests.Unit.csproj -c Release -- --filter-class "*Calculator*"

# Enable desktop visual debug window during a test run
INTOCHAT_TEST_VISUAL=desktop dotnet run --project tests/IntoChat.OS.Tests.E2E/IntoChat.OS.Tests.E2E.csproj -c Release
```
