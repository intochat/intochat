# IntoChat.OS Test Infrastructure Architecture

## 1. Executive Summary & Philosophy
IntoChat.OS testing infrastructure is designed around **opaque-box verification**, **progressive testability**, and **fail-closed architectural guarantees**. The test harness treats subsystems as opaque components, validating external contracts, invariants, state transitions, security provenance boundaries, and telemetry signals without relying on internal private state or non-deterministic timing.

## 2. Test Suite Tiers Overview
The test suite is organized into 4 distinct, complementary tiers:

```
┌───────────────────────────────────────────────────────────────┐
│        Tier 4: Real-World Application Scenarios (5+)           │
│   Multi-step E2E workflows, lifecycle transitions, crashes    │
├───────────────────────────────────────────────────────────────┤
│        Tier 3: Cross-Feature Combinations (16+)               │
│   Pairwise feature interactions across hot/cold, auth, memory │
├───────────────────────────────────────────────────────────────┤
│        Tier 2: Boundary & Corner Cases (105+ tests)           │
│   5+ tests per feature: extreme sizes, nulls, corruptions     │
├───────────────────────────────────────────────────────────────┤
│        Tier 1: Feature Coverage (105+ tests)                  │
│   5+ tests per feature across all 21 inventory features (F1-F21) │
└───────────────────────────────────────────────────────────────┘
```

### Tier 1: Feature Coverage (F1–F21)
- **Target**: >=5 tests per feature for all 21 features in `PROJECT.md § Feature Inventory` (105 total).
- **Focus**: Deterministic happy-path behavior, contract compliance, type structures, and core functional guarantees.
- **Features Tested**:
  - `F1`: Ephemeral Signal Annotation (`AppActivity`, `PresentationChanged`, `AppRequested`)
  - `F2`: AppNeuron Progress In-Memory Separation
  - `F3`: Asynchronous Confirmation Resolution (`KernelConfirmations`)
  - `F4`: Streaming Signal Allocation Optimization (`SessionSignalSubscription`)
  - `F5`: Fail-Closed PlainNeuronCallFilter
  - `F6`: Provenance Caller Construction Hygiene (`CapabilityCallers.Of`)
  - `F7`: Sideload Consent Gate Enforcement
  - `F8`: Strict Grant Rule Evaluation (`GrantRules`)
  - `F9`: Eliminate Dual App Definitions (`FilmCraft`, `PhotoCraft`)
  - `F10`: Fix SystemOne Hosting Path
  - `F11`: Stale InternalsVisibleTo Cleanup
  - `F12`: Retire AppState Legacy Collections & Shape
  - `F13`: Retire Legacy Shims & Forwards
  - `F14`: Canonical Execution Context Subsystem (`IExecutionContext`)
  - `F15`: Canonical Decision Maker Subsystem (`IDecisionMaker`)
  - `F16`: Canonical Memory with Graph UI (`IKnowledgeStore`)
  - `F17`: Canonical Task Engine for Self-Improvement (`ITaskEngine`)
  - `F18`: API Tracking Baseline & Contract Manifest Update
  - `F19`: TreatWarningsAsErrors Zero-Warning Enforcement
  - `F20`: Reusable Testing Framework for Agent Verification (`ITestVerificationService`)
  - `F21`: Aspire Telemetry Verification & Flush Barrier (`TelemetryFlushBarrier`)

### Tier 2: Boundary & Corner Cases
- **Target**: >=5 tests per feature for all 21 features (105 total).
- **Focus**: Extreme input values, null/empty/whitespace arguments, unicode/meta-characters, concurrent access, boundary limits, and fail-closed security denials.

### Tier 3: Cross-Feature Combinations (Pairwise Interaction)
- **Target**: 16 pairwise integration tests.
- **Focus**: Verifying seamless coordination between decoupled subsystems:
  - Hot-path ephemeral signals combined with execution contexts (`F1` + `F14`).
  - Strict caller provenance applied to reactive decision making (`F6` + `F15`).
  - Graph memory retrieval feeding deliberate goal synthesis (`F16` + `F15`).
  - Task engine self-improvement loops executing test verification (`F17` + `F20`).
  - Confirmation resolution triggering task status transitions (`F3` + `F17`).
  - Telemetry barrier flushing during high-throughput signal streaming (`F21` + `F4`).

### Tier 4: Real-World Application Scenarios
- **Target**: >=5 comprehensive end-to-end multi-step scenarios.
- **Focus**: Realistic developer and runtime agent workflows:
  - Scenario 1: Autonomous Agent Self-Improvement Loop (Task creation -> Decision synthesis -> Execution -> Verification).
  - Scenario 2: High-Throughput Media Studio Pipeline (FilmCraft timeline edit -> Mcp export -> Ephemeral telemetry).
  - Scenario 3: Secure App Sideloading with Human Consent & Grant Enforcement (Manifest validation -> User consent -> Fail-closed execution).
  - Scenario 4: Graph Memory Synthesis & Knowledge-Augmented Routing (Node ingestion -> Semantic search -> Plan deliberation).
  - Scenario 5: Fault Injection & Resilient Recovery (Simulated timeout -> Fail-closed filter rejection -> Telemetry flush barrier).

---

## 3. Test Runner & Platform Architecture
The test suite utilizes **Microsoft.Testing.Platform** (`xunit.v3.mtp-v2`) in `IntoChat.OS.Kernel.Tests.Integration`:
- **Executable Runner**: Output type is an executable runner (`OutputType=Exe`) integrating directly with `dotnet test` and standalone CLI execution.
- **Targeted Execution**: Tests can be filtered by Category or Feature:
  ```bash
  # Run all E2E tests
  dotnet test src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Integration/IntoChat.OS.Kernel.Tests.Integration.csproj --no-build

  # Run by Tier
  dotnet test src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Integration/IntoChat.OS.Kernel.Tests.Integration.csproj --no-build --filter "Category=Tier1_FeatureCoverage"
  dotnet test src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Integration/IntoChat.OS.Kernel.Tests.Integration.csproj --no-build --filter "Category=Tier2_BoundaryCorner"
  dotnet test src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Integration/IntoChat.OS.Kernel.Tests.Integration.csproj --no-build --filter "Category=Tier3_CrossFeature"
  dotnet test src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.Tests.Integration/IntoChat.OS.Kernel.Tests.Integration.csproj --no-build --filter "Category=Tier4_RealWorldScenarios"
  ```
- **Zero-Warning Enforcement**: `TreatWarningsAsErrors=true` is enforced with zero warnings, satisfying project-wide build integrity (`F19`).
