# Workspace Hosting and Design System Implementation Plan

> Execute inline with superpowers:executing-plans and test-driven development. User explicitly authorized implementation of both approved designs.

**Goal:** One authoritative workspace hosting typed neuron roots, with shared theme and motion across Home, Files and Assistant.

**Architecture:** App manifests declare default entry references. Workspace persists navigation independently of behaviors composing content. Flutter reads state and renders semantic components through its existing theme.

**Tech Stack:** C#, Orleans, behavior scripts, Dart/Flutter, Flame, Aspire.

**Spec:** docs/superpowers/specs/2026-10-12-workspace-design-system.md

## Global constraints

Preserve provenance, compile gate, neuron state, whole rebuilds, concrete arrays, non-reentrant safety and unrelated user changes. Map symbols through LSP before edits. No new app-readiness coordinator. No deployment.

## Review focus

- Delayed composition cannot undo Close or focus changes.
- Duplicate commands retain operation identity after newer navigation.
- Typed scene/text roots and separate documents survive reconnect.
- Theme changes propagate; stable rebuilds do not replay entrance animations.
- Files capability limitations and uninitialized/failed roots are observable.

### Task 1: Declared app entry

Files: Apps.Contracts/Apps/AppManifest.cs; Apps/Apps/AppDeclaration.cs, AppNeuron.cs, Creator.cs; Apps/Authoring/AgentPrompts.cs; CSharp/Authoring/CSharpScriptSandbox.cs; corresponding module tests and Dart app manifest model.

- [x] Map declaration/constructor/serialization callers with LSP.
- [x] Write failing entry validation and declaration round-trip facts.
- [x] Add optional typed relative entry, validate paths and preserve it through authoring/import/export and compilation.
- [x] Verify Apps module and Dart wire-model tests.

### Task 2: Authoritative typed-root hosting

Files: Flutter.Contracts/Workspace contracts; Flutter/Workspace/WorkspaceNeuron.cs, WorkspaceEndpoints.cs; workspace module facts; Dart core workspace models/client; shell workspace store/controller/app.

- [x] Map command and snapshot callers with LSP.
- [x] Write failing focus/duplicate/close/reactivation facts.
- [x] Consolidate commands and persist focus/Home/Companion with typed references and stable receipts.
- [x] Replace client navigation inference and address fabrication; repair watch recovery.
- [x] Verify module and shell regression suites.

### Task 3: Shared theme and semantic components

Files: Dart ui theme/composition/components, exports and widget tests; Files feature behaviors and supported component contracts where needed.

- [x] Map existing theme, rendering and Files contracts with LSP.
- [x] Write failing stable-identity/reduced-motion/theme-inheritance and breadcrumb interaction tests.
- [x] Extend existing theme with motion/shape/spacing; implement reusable themed composition and navigation components.
- [x] Apply to Files storage/location/selection flow using real supported connectors.
- [x] Verify ui/core tests and analyze.

### Task 4: Integrate Home, Ino, Assistant and Creator

Files: features/os app declarations and open/composition behaviors; shell Home/workspace composition and tests.

- [x] Remove Stage as competing top-level host and Ino's duplicate docking reaction.
- [x] Compose initial UI idempotently; remove composition focus effects; retain document roots.
- [x] Render Home/main/Companion and contextual controls consistently through shared components.
- [x] Compile-check feature bundles and run affected module unit/E2E and Flutter suites.

### Task 5: Runtime verification and review

- [x] Launch Aspire, inspect health and failures.
- [x] Drive Home, Files and Assistant through real neurons; test duplicate Open/Close and completion without navigation. Cover typed roots, multiple documents and reconnect through automated tests. Live generated-app and connected-folder traversal remain unverified (see ledger).
- [x] Hot reload/restart Flutter; inspect runtime errors and telemetry.
- [x] Independent final review; fix important findings and record limitations.

## Execution ledger

- Baseline: shell workspace_focus_test.dart and workspace_app_open_test.dart passed (3 tests).
- Preflight: existing Directory.Packages.props change is unrelated and preserved.
- Implemented typed manifest entries, workspace-owned Home/Companion/focus, generic Open and idempotent navigation receipts. Removed surface-only opening and Ino docking authority. Remote membership/focus is no longer duplicated in local persistence.
- Implemented shared theme tokens, semantic surface/breadcrumb components, default entrance/scene motion, reduced-motion behavior and suppression of initial/reconnect animation replay.
- Files now composes storages, breadcrumb navigation, filter and contextual selection through C# behaviors and existing neuron components. No new Google or YouTube connector was added.
- Independent implementation/review agents covered entry/Creator, workspace/Files and theme/shell. Follow-up findings fixed scene contract mapping, watch recovery, delayed table hydration focus theft, duplicate document receipts and remote-state persistence.
- Verification: Apps 239 tests passed, with the added import/export fact also passing separately; Flutter module 141; hosting composition 7; UI 158; shell 197; core 4. FeatureCompileFacts passed both checks across 18 feature bundles. Dart analysis is clean. Final production Dart LSP refresh reported no diagnostics in received results.
- Delayed table hydration regression tests cover Close/Home, Home with an open document and another focused document. Removing the no-reveal flag made all three fail; restoring production made all three pass.
- Broader CSharp suite: 188/189 passed; one SubscriptionCallerFacts theory case timed out, then both variants passed in isolation. Existing xUnit1051 analyzer errors required a command-line WarningsNotAsErrors override; unrelated tests and package changes were not modified. This is not recorded as an entirely green broader suite.
- Live Aspire verification: Trust, both OS replicas, both MCP replicas, sandbox and FlutterShell are Running/Healthy. Rebuilt the sandbox image to match current contracts; retained its cache and strict contract/provenance handshake.
- Real MCP commands configured typed Home and Companion, opened Ino/Files/Assistant, changed focus and replayed Open/Close commands. Duplicate Open did not steal focus; an old Close did not close a newly reopened window. After fresh sandbox compilation, Assistant/Files behavior completion and Files filtering left workspace revision 23, Home focus and closed membership unchanged.
- Flutter hot restart/reload succeeded and runtime error inspection returned none. Relevant command telemetry was inspected. No pixel-level visual review was performed.
- Limits: no Windows storage was connected, so live folder traversal was not exercised. Creator compile/publish and typed/multiple-document handling were tested, but no new model-generated app or Assistant model answer was requested live. Existing runtime assignment-generation weaknesses are outside this change.
- No commits or deployment. Unrelated Directory.Packages.props edits preserved.

## Follow-up: approved visual and real-flow validation

- Three independent agents handled shared visual components, real storage and parallel activity. Parent integrated shell chrome and catalog behavior; no overlapping production ownership.
- Catalog and app details now discover UI through the declared Entry, not an operation named open. Four regression cases first failed and then passed. Full shell suite: 201 passed; analysis clean.
- Bottom workspace chrome now consumes shared spacing and radius tokens.
- Connected a genuine local Windows device through the normal endpoint, allowing only the task-created sample root C:/Users/vhorb/AppData/Local/Temp/digitalbrain-files-verification-20261012. Real Files storage/root/folder/file preview, filtering, selection and breadcrumbs passed. Final Files location is Storages with empty filter/selection. Windows suite: 21 passed, 1 skipped because TPM ECDH is unavailable.
- First-use Windows machine identity discovery is still a usability gap. A normal Facts call initialized its genuine identity; no credential fabrication or broader folder authorization was used.
- Real parallel status/close/navigation checks passed and restored original workspace membership (Files and Brain open, Home focused). Assistant status was simulated deterministically; no model call is claimed. Added real-grain concurrent Open/retry/reactivation receipt regression; full Flutter module suite 142/142 passed. Matching completion/close/stale-focus spans were inspected.
- Shared visual capture uses rendered widget fixtures. Live screenshot capture is unavailable because the running Flutter app lacks the driver extension and native computer capture is disabled. Do not interpret fixture screenshots as live end-to-end visual verification.

- Shared visual follow-up fixed inherited chat fonts, primary-button font and a copy-control overlap discovered in rendered pixels. UI suite: 167 passed; analyzer clean. Six desktop/narrow fixtures cover paper, midnight and lumen; parent inspected lumen desktop and paper narrow. These validate shared components, not live shell geometry or animation timing.
- Connected Flutter hot restart succeeded. After restart, the real widget tree showed Ino Home, Files/Brain tabs and the Assistant transcript/composer. Workspace Read remained revision 34 with Files/Brain open and Home focused. Runtime errors: none. Full integrated shell suite remained 201/201 after UI changes.
- LSP navigation mapped changed symbols; Dart publish-diagnostic messages were unavailable in the final bridge and pull diagnostics are unsupported. Clean Dart diagnostics were verified with flutter analyze; C# test LSP diagnostics were empty.

Fixture regeneration from src/DigitalBrain.Modules/Google/Flutter/app/ui:

```powershell
$env:WORKSPACE_VISUAL_FONT = 'C:/Windows/Fonts/segoeui.ttf'
$env:WORKSPACE_VISUAL_ICONS = 'E:/tools/flutter/bin/cache/artifacts/material_fonts/materialicons-regular.otf'
$env:WORKSPACE_VISUAL_OUTPUT = 'E:/intochat/digitalbrain/artifacts/workspace-visual-2026-10-12'
flutter test --no-pub test/workspace_visual_test.dart
```

The environment variables are optional: normal test runs check layout without writing screenshots. Desktop fixtures are 1000x640; narrow fixtures are 390x1100.

- Final Files polish uses the existing connection Account, then IWindows.Facts().MachineName, with an explicit per-device fallback when unavailable. Files 1.0.4 compile-checked across all 18 bundles (2 FeatureCompileFacts passed) and applied through normal app update. Live storage label and breadcrumb both read TWIXPC; restored Storages at listing revision 62. Update trace c414be492e78b41a1150c15d77e3dc62 succeeded. No service restart or new contract.
- The dedicated sample-folder connection remains available for inspection. No personal folders were granted, commits created or deployment performed.

## Follow-up: storage onboarding and live acceptance

- The earlier screenshot limitation is superseded: Flutter Inspector's screenshot service captures the running render tree without a driver extension or native computer automation. Captures are under `artifacts/workspace-live-2026-10-12`. `02-stale-home-after-focus.png` is failure evidence, not a successful Files capture.
- Live inspection found an empty decorated toolbar when the companion was expanded. Decoration now belongs to the resolved nonempty dock. Regression coverage also exercises Tab navigation at 390x844; that narrow-screen check is a widget test, not a resized live desktop.
- Live Files exposed a single-root breadcrumb crash (`clamp(1, 0)`). The shared component computes the hidden range only for collapsed paths. The new single-root regression failed before the fix; single-root, empty and existing collapsed navigation tests pass. Hot reload and `04-files-fixed.png` confirm the live root view, with no reported Flutter runtime errors at that check. UI suite: 169 passed; analysis clean.
- Windows setup uses authenticated brain-scoped HTTP endpoints over existing connection and IWindows contracts. It discovers the actual workspace host, connects through normal connection authorization, and grants only explicit full folder paths. The view names the host rather than claiming it is the browser's computer. The full OS exposed ambiguous IConnection resolution missed by the Windows-only host; routing now uses the framework's connector-aware GetConnection helper, with a regression that first returned 500.
- Live Windows discovery returned TWIXPC. Normal connect, root read, idempotent allow and relative-path rejection passed; MCP Roots returned only the pre-existing task sample folder. Trace `bbccb5f3cc944f9ebfc4137e610d3ac9` records a successful frontend request. No personal folders were authorized.
- A real Creator invocation generated `workspace-notes-check`. Its first surface compiled but used invalid semantic child kinds. A real Creator repair produced version 0.2.1, with independent Alpha/Beta IText neurons and a surface containing two `text` references. Publish passed the compile gate and wrote the local cluster marketplace; trace `cdeda83612781fe6fbfc45e0eb65bb43` has no errors. Its `ask` operation reports to telemetry only, not to a user-visible result neuron. The generated source contains no fixed colors, fonts or animation choices.
- CSharp cleanup passes cancellation tokens to reminder calls, eliminating xUnit1051 without an override. The intermittent no-part-log test now waits for the accepted source's existing subscription handshake after reassignment/reactivation, instead of treating a runtime observer as source readiness. Provenance/refused-source assertions remain. Full suite: 189 passed, zero skipped; the historical intermittent failure was diagnosed from ordering, not freshly reproduced.
- Review found that settings section replacement dropped the brain-aware callback wrapper. Every workspace settings section now retains that wrapper; callbacks are stable across unrelated rebuilds and change with client/brain identity. Regressions cover unfinished path preservation and brain changes after General-to-Connections navigation. Final shell suite: 210 passed; Windows suite: 25 passed, one TPM ECDH skip; core suite: 4 passed. Shell analysis and Dart MCP analysis are clean; Windows C# LSP diagnostics are empty.
- The stale live workspace was a hosting defect: native gRPC was sent through an HTTP/1 cleartext front. The controller had never received a watch revision; the HTTP/2 preface received HTTP_1_1_REQUIRED. Desktop now uses the front's HTTPS endpoint, and that front reaches the primary OS over HTTPS/HTTP/2 with the existing trusted development certificate. Project endpoints use Aspire's URL injection; executable hosts use the HTTPS port variable. No polling fallback or certificate-validation bypass was retained. Hosting tests: 8 passed; AppHost build: zero warnings/errors. Live proxy logs confirm native gRPC Watch over HTTP/2 with 200 streaming responses on both hops.
- Final live acceptance: Files-to-Home focus revision 36 appeared without reload (`07-home-streamed.png`); generated surface showed both documents (`08-generated-surface.png`); independent Alpha/Beta windows opened; Alpha changed while visible with workspace revision remaining 40 (`10-alpha-streamed.png`). Beta remained version 1, Hello Beta. Paper theme restyled the document and Assistant together (`11-paper-theme.png`). The real Windows setup dialog showed TWIXPC and the sole allowed sample root (`12-storage-setup.png`). Local dialog/theme actions were invoked through Flutter's VM service; domain and workspace mutations used digitalbrain-mcp.
- Hot restart retained Alpha focus, both document windows and changed Alpha text (`13-reconnected-alpha.png`). Restored Hello Alpha, closed the three task-created windows, restored midnight theme and Home, leaving original Files/Brain membership open at workspace revision 43 (`14-restored-home.png`). The verification app remains published in the local cluster. Successful text trace `1881c0d072a786524e1b3db667c50a9f` and close trace `78f3bd7d8e9c1d7addb6d03848b0b285` were inspected. Final Trust, both OS/MCP instances, SystemOne, sandbox and FlutterShell are Running/Healthy; Flutter reports no runtime errors.
- Limits: live captures verify rendered states, theme propagation, signal delivery and reconnect; animation frame timing was not benchmarked. No new Assistant model answer was requested in this follow-up. The narrow viewport and Tab checks are automated widget tests. No commits or remote deployment were made in this follow-up.
