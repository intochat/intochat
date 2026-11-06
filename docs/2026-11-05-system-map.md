# IntoChat End-to-End System Map, Complexity Audit & Target Architecture

**Date:** 2026-11-05  
**Scope:** `E:/intochat/digitalbrain` (`intochat/digitalbrain`), `E:/intochat/modules` (`intochat/modules`), `E:/intochat/os` (`intochat/os`), `E:/intochat/intochat.docs`, `E:/intochat/os.docs`  
**Visual Companion:** [2026-11-05-system-map.html](file:///E:/intochat/intochat.docs/assessment/2026-11-05-system-map.html)

---

## Executive Summary

IntoChat has just completed four major architectural shifts in nine days (Oct 28 – Nov 5, 2026): retiring Pulumi for a native C# ARM installer (`0055b8adb`), splitting a monorepo into three repositories (`digitalbrain`, `modules`, `os`), landing compiled-unit `[Synapse]` execution in collectible `AssemblyLoadContext`s (`DigitalBrain.Runtime.Synapses`, Plans 1–3), and replacing code-generated user promises with declarative `Policy` evaluation (`PoliciesNeuron`, Plan 4).

Because the old systems were kept alive while the new ones were built alongside them, the repositories currently carry **both halves of three unfinished transitions**:
1. **Two container build & release pipelines (`release.yml` vs `deploy.yml`)** that fire in parallel on `release: published`, build all three images twice to two different registries (`ghcr.io` vs `acrintochattest.azurecr.io`) with drifted MSBuild flags, and take **32.5 minutes** median while never deploying the images attached to the GitHub Release.
2. **A three-repository NuGet cascade (`digitalbrain` → `modules` → `os`)** forced through a single GitHub Packages namespace (`nuget.pkg.github.com/intochat`) whose package ownership is pinned to `intochat/os`, requiring a classic PAT (`PACKAGE_TOKEN`), breaking `bump.yml` 14 times in 48 hours (10% success rate in `os`), and blocking local feed development with `<packageSourceMapping>`.
3. **Two coexisting app/programmability stacks** (`App<TState>` + `AppNeuron` + `Creator` + out-of-process `CSharp.Sandbox` + `IntoChat.OS.Client` at **23,829 LOC**, alongside `[Synapse]` + `DigitalBrain.Runtime.Synapses` + `PoliciesNeuron` at **~2,200 LOC**) and **six coexisting module configuration mechanisms**.

This document maps every project, dependency, test count, workflow job duration, deployment step, and configuration path across the system (Section 1), ranks the seven primary sources of complexity with exact file paths, costs, and commit histories (Section 2), proposes one concrete target architecture with rejected alternatives and costs if wrong (Section 3), lays out a six-phase execution plan (Section 4), and lists the five decisions for the owner (Section 5).

---

## 1. System Code Map & Raw Measurements

### 1.1 Cross-Repository Totals (`VERSION = 0.14.2` across all three repos)

| Repository | `.csproj` Projects (Prod / Test / Template) | Published NuGet Packages | `.cs` Files | Total C# Lines (Non-Empty) | Discovered xUnit Tests (Static/Prompt Count) | Executed in Push CI |
|---|---:|---:|---:|---:|---:|---:|
| **`digitalbrain`** (`E:/intochat/digitalbrain`) | **48** (21 prod / 22 test / 5 template) | **19** (`nuget.org` + GitHub Packages) | 783 | **74,342** (63,892) | **1,894** active + 8–10 template = **1,902–1,904** (`1,900`) | **1,894** (100%) |
| **`modules`** (`E:/intochat/modules`) | **54** (28 prod / 26 test) | **28** (GitHub Packages) | 380 | **22,100** (19,471) | **486** discovered (`549` static attribute + conformance sum) | **390** (**80.2%** — 96 integration/browser tests never run!) |
| **`os` (C#)** (`E:/intochat/os`) | **88** (47 prod / 40 test / 1 template) | **40** packable (GitHub Packages) | 1,553 (+41 loose/embedded) | **136,188** (119,994) (+7,625 loose/embedded) | **2,404** discovered (`2,439` static attribute + conformance sum) | **2,403** (99.96% — 1 Docker sandbox E2E test never runs) |
| **`os/ui` (Flutter/Dart)** (`E:/intochat/os/ui`) | **4** Dart packages (`core`, `design_ui`, `ui`, `shell`) | 0 (compiled into `os-ui` & Kernel `wwwroot`) | 331 `.dart` files | **78,168** (73,749) | **485** Flutter `test()` / `testWidgets()` | **485** (100%, via `flutter.yml`) |
| **TOTAL** | **190 `.csproj` + 4 Dart** | **87 packable** | **2,716 `.cs` + 331 `.dart`** | **310,798 lines** | **4,784 C# (`4,888` static) + 485 Dart = 5,269** | **4,687 C# + 485 Dart** |

---

### 1.2 `E:/intochat/digitalbrain` — Complete Project & Test Map (48 `.csproj`, 19 NuGet Packages)

#### Production & Template Projects (23 `.csproj`)
| # | Project Name | Path | Kind | Packable | Package ID | `.cs` Files | Total Lines | Non-Empty Lines | Internal `ProjectReference`s | External `PackageReference`s |
|---:|---|---|---|---|---|---:|---:|---:|---|---|
| 1 | `DigitalBrain` | [DigitalBrain.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain/DigitalBrain.csproj) | library | **Yes** | `DigitalBrain` | 152 | 6,964 | 6,162 | *(none)* | `Microsoft.Extensions.Configuration.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`, `Microsoft.Extensions.Options`, `Microsoft.Orleans.Sdk` |
| 2 | `DigitalBrain.Analyzers` | [DigitalBrain.Analyzers.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Analyzers/DigitalBrain.Analyzers.csproj) | analyzer | **Yes** | `DigitalBrain.Analyzers` | 53 | 4,347 | 3,846 | *(packs `CodeFixes.dll`)* | `Microsoft.CodeAnalysis.CSharp` |
| 3 | `DigitalBrain.Analyzers.CodeFixes` | [DigitalBrain.Analyzers.CodeFixes.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Analyzers.CodeFixes/DigitalBrain.Analyzers.CodeFixes.csproj) | analyzer | No (bundled in #2) | - | 5 | 405 | 350 | `DigitalBrain.Analyzers` | `Microsoft.CodeAnalysis.CSharp.Workspaces` |
| 4 | `DigitalBrain.Apps.Packaging` | [DigitalBrain.Apps.Packaging.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Apps.Packaging/DigitalBrain.Apps.Packaging.csproj) | library | **Yes** | `DigitalBrain.Apps.Packaging` | 3 | 255 | 222 | `DigitalBrain` | *(none)* |
| 5 | `DigitalBrain.Capabilities.Compute` | [DigitalBrain.Capabilities.Compute.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Compute/DigitalBrain.Capabilities.Compute.csproj) | library | **Yes** | `DigitalBrain.Capabilities.Compute` | 19 | 557 | 499 | `DigitalBrain` | *(none)* |
| 6 | `DigitalBrain.Capabilities.Connections` | [DigitalBrain.Capabilities.Connections.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Connections/DigitalBrain.Capabilities.Connections.csproj) | library | **Yes** | `DigitalBrain.Capabilities.Connections` | 24 | 1,033 | 917 | `DigitalBrain`, `Capabilities.Identity`, `Capabilities.Secrets` | *(none)* |
| 7 | `DigitalBrain.Capabilities.Identity` | [DigitalBrain.Capabilities.Identity.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Identity/DigitalBrain.Capabilities.Identity.csproj) | library | **Yes** | `DigitalBrain.Capabilities.Identity` | 14 | 184 | 164 | `DigitalBrain` | *(none)* |
| 8 | `DigitalBrain.Capabilities.Secrets` | [DigitalBrain.Capabilities.Secrets.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Secrets/DigitalBrain.Capabilities.Secrets.csproj) | library | **Yes** | `DigitalBrain.Capabilities.Secrets` | 5 | 63 | 55 | `DigitalBrain` | *(none)* |
| 9 | `DigitalBrain.Capabilities.Storage` | [DigitalBrain.Capabilities.Storage.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Storage/DigitalBrain.Capabilities.Storage.csproj) | library | **Yes** | `DigitalBrain.Capabilities.Storage` | 12 | 212 | 190 | `DigitalBrain` | *(none)* |
| 10 | `DigitalBrain.Graph` | [DigitalBrain.Graph.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Graph/DigitalBrain.Graph.csproj) | library | **Yes** | `DigitalBrain.Graph` | 8 | 287 | 250 | `DigitalBrain` | *(none)* |
| 11 | `DigitalBrain.Identity.Local` | [DigitalBrain.Identity.Local.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Identity.Local/DigitalBrain.Identity.Local.csproj) | library | **Yes** | `DigitalBrain.Identity.Local` | 11 | 970 | 845 | `DigitalBrain`, `Capabilities.Connections`, `Capabilities.Identity`, `Capabilities.Secrets` | *(none)* |
| 12 | `DigitalBrain.Runtime.Apps` | [DigitalBrain.Runtime.Apps.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Apps/DigitalBrain.Runtime.Apps.csproj) | library | **Yes** | `DigitalBrain.Runtime.Apps` | 2 | 280 | 242 | `DigitalBrain` | `Microsoft.Extensions.Logging.Abstractions` |
| 13 | `DigitalBrain.Runtime.Http` | [DigitalBrain.Runtime.Http.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Http/DigitalBrain.Runtime.Http.csproj) | library | **Yes** | `DigitalBrain.Runtime.Http` | 5 | 388 | 341 | `DigitalBrain`, `Capabilities.Compute`, `Runtime.Orleans` | *(ASP.NET Core `FrameworkReference`)* |
| 14 | `DigitalBrain.Runtime.Orleans` | [DigitalBrain.Runtime.Orleans.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Orleans/DigitalBrain.Runtime.Orleans.csproj) | library | **Yes** | `DigitalBrain.Runtime.Orleans` | 109 | 8,906 | 7,764 | `DigitalBrain`, `Capabilities.*` (all 5), `Graph`, `Identity.Local`, `Runtime.Apps`, `UI` | `Microsoft.Orleans.Reminders`, `Microsoft.Orleans.Runtime`, `Microsoft.Orleans.Sdk`, `Microsoft.Orleans.Serialization`, `Microsoft.Orleans.Server`, `Microsoft.Orleans.Streaming` |
| 15 | `DigitalBrain.Runtime.Orleans.Azure` | [DigitalBrain.Runtime.Orleans.Azure.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Orleans.Azure/DigitalBrain.Runtime.Orleans.Azure.csproj) | library | **Yes** | `DigitalBrain.Runtime.Orleans.Azure` | 6 | 720 | 645 | `DigitalBrain`, `Runtime.Orleans` | `Azure.Data.Tables`, `Azure.Identity`, `Azure.Storage.Blobs`, `Azure.Storage.Queues`, `Microsoft.Orleans.Clustering.AzureStorage`, `Microsoft.Orleans.Persistence.AzureStorage`, `Microsoft.Orleans.Reminders.AzureStorage`, `Microsoft.Orleans.Streaming.AzureStorage` |
| 16 | `DigitalBrain.Runtime.Synapses` | [DigitalBrain.Runtime.Synapses.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Synapses/DigitalBrain.Runtime.Synapses.csproj) | library | **Yes** | `DigitalBrain.Runtime.Synapses` | 7 | 1,138 | 977 | `DigitalBrain`, `Analyzers`, `Runtime.Orleans` | `Microsoft.CodeAnalysis.CSharp` |
| 17 | `DigitalBrain.Testing` | [DigitalBrain.Testing.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Testing/DigitalBrain.Testing.csproj) | library | **Yes** | `DigitalBrain.Testing` | 62 | 4,675 | 4,066 | `DigitalBrain`, `Analyzers`, `Capabilities.*` (all 5), `Graph`, `Identity.Local`, `UI` | `Microsoft.CodeAnalysis.CSharp`, `xunit.v3.assert`, `xunit.v3.extensibility.core` |
| 18 | `DigitalBrain.Testing.Silo` | [DigitalBrain.Testing.Silo.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.Testing.Silo/DigitalBrain.Testing.Silo.csproj) | library | **Yes** | `DigitalBrain.Testing.Silo` | 8 | 1,355 | 1,148 | `DigitalBrain`, `Capabilities.*`, `Runtime.Orleans`, `Testing`, `UI` | `xunit.v3.assert`, `xunit.v3.extensibility.core` |
| 19 | `DigitalBrain.UI` | [DigitalBrain.UI.csproj](file:///E:/intochat/digitalbrain/src/DigitalBrain.UI/DigitalBrain.UI.csproj) | library | **Yes** | `DigitalBrain.UI` | 15 | 665 | 613 | `DigitalBrain` | *(none)* |
| 20 | `DigitalBrain.Templates` | [DigitalBrain.Templates.csproj](file:///E:/intochat/digitalbrain/templates/DigitalBrain.Templates/DigitalBrain.Templates.csproj) | template | **Yes** | `DigitalBrain.Templates` | 0 | 0 | 0 | *(none)* | *(none)* |
| 21 | `DigitalBrain.Examples.Counter` | [DigitalBrain.Examples.Counter.csproj](file:///E:/intochat/digitalbrain/examples/Counter/DigitalBrain.Examples.Counter/DigitalBrain.Examples.Counter.csproj) | sample | No | - | 5 | 252 | 217 | `DigitalBrain`, `Analyzers`, `UI` | *(none)* |
| 22–23 | `AppName` / `MODULE_NAME` (template content) | `templates/DigitalBrain.Templates/content/...` | template | No | - | 4 | 150 | 127 | *(template content)* | `DigitalBrain`, `DigitalBrain.Analyzers`, `DigitalBrain.UI` |

#### Test Projects in `digitalbrain` (22 Active + 3 Template = 25 `.csproj`, 1,894 Active Tests)
| # | Test Project | CI Job | `.cs` Files | Total Lines | Discovered Tests | `[Fact]` / `[Theory]` / `[InlineData]` | Conformance / Fixture Used |
|---:|---|---|---:|---:|---:|---|---|
| 1 | `DigitalBrain.Examples.Counter.Tests` | `facts` | 3 | 173 | **21** | 11 / 2 / 0 | `TestOS`, `SiloHost`, `BothRuntimesFixture` |
| 2 | `DigitalBrain.Analyzers.Tests.Unit` | `facts` | 36 | 9,595 | **382** | 375 / 2 / 7 | Roslyn CSharpAnalyzerVerifier |
| 3 | `DigitalBrain.Apps.Packaging.Tests.Unit` | `facts` | 2 | 412 | **18** | 18 / 0 / 0 | - |
| 4 | `DigitalBrain.Capabilities.Compute.Tests.Unit` | `facts` | 7 | 797 | **48** | 48 / 0 / 0 | `TestOS` |
| 5 | `DigitalBrain.Capabilities.Connections.Tests.Unit` | `facts` | 7 | 1,106 | **59** | 59 / 0 / 0 | `TestOS`, `SiloHost` |
| 6 | `DigitalBrain.Capabilities.Identity.Tests.Unit` | `facts` | 2 | 206 | **14** | 14 / 0 / 0 | - |
| 7 | `DigitalBrain.Capabilities.Secrets.Tests.Unit` | `facts` | 2 | 126 | **8** | 8 / 0 / 0 | - |
| 8 | `DigitalBrain.Capabilities.Storage.Tests.Unit` | `facts` | 1 | 218 | **12** | 12 / 0 / 0 | - |
| 9 | `DigitalBrain.Framework.Tests.E2E` | `product` | 6 | 1,683 | **37** | 23 / 14 / 0 | `OrleansHostConformanceFacts : HostConformanceFacts` (`TestOS`, `SiloHost`, Azurite) |
| 10 | `DigitalBrain.Graph.Tests.Unit` | `facts` | 3 | 296 | **15** | 15 / 0 / 0 | `TestOS` |
| 11 | `DigitalBrain.Identity.Local.Tests.Unit` | `facts` | 7 | 802 | **87** | 31 / 0 / 0 | `IdentityConformance`, `SecretsConformance`, `ConnectionsConformance` |
| 12 | `DigitalBrain.Runtime.Apps.Tests.Unit` | `runtime` | 2 | 506 | **15** | 15 / 0 / 0 | - |
| 13 | `DigitalBrain.Runtime.Http.Tests.Integration` | `runtime` | 2 | 350 | **13** | 13 / 0 / 0 | `SiloHost` + Kestrel |
| 14 | `DigitalBrain.Runtime.Orleans.Azure.Tests.Integration` | `runtime` | 9 | 1,246 | **45** | 45 / 0 / 0 | `SiloHost` + Azurite Docker container |
| 15 | `DigitalBrain.Runtime.Orleans.Tests.Integration` | `runtime` | 38 | 8,668 | **260** | 258 / 1 / 2 | `TestOS`, `SiloHost` (`LocalSiloHostFactory`) |
| 16 | `DigitalBrain.Runtime.Orleans.Tests.Unit` | `runtime` | 19 | 2,860 | **213** | 152 / 2 / 5 | `IdentityConformance`, `SecretsConformance`, `ConnectionsConformance` |
| 17 | `DigitalBrain.Runtime.Synapses.Tests.Integration` | `runtime` | 6 | 1,622 | **47** | 47 / 0 / 0 | `SiloHost` + `SynapseCompiler` + collectible `SynapseLoadContext` |
| 18 | `DigitalBrain.Templates.Tests.Integration` | `template` | 2 | 412 | **8** | 8 / 0 / 0 | Process execution (`dotnet new` + `dotnet test`) |
| 19 | `DigitalBrain.Testing.Silo.Tests.Unit` | `runtime` | 4 | 589 | **33** | 26 / 4 / 3 | `TestOS`, `SiloHost`, `BothRuntimesFixture` |
| 20 | `DigitalBrain.Testing.Tests.Unit` | `facts` | 25 | 3,663 | **325** | 127 / 2 / 4 | `StorageConformance` (x4), `IdentityConformance` (x2), `SecretsConformance` (x2), `ConnectionsConformance` (x2) |
| 21 | `DigitalBrain.Tests.Unit` | `facts` | 35 | 4,769 | **212** | 209 / 1 / 3 | `TestOS` |
| 22 | `DigitalBrain.UI.Tests.Unit` | `facts` | 4 | 275 | **22** | 22 / 0 / 0 | - |
| 23–25 | Template test projects (`AppName.Tests.Unit`, `AppName.Tests.Integration`, `MODULE_NAME.Tests.Unit`) | `template` | 5 | 158 | **8–10** | 6 / 3 / 0 | `TestOS`, `SiloHost`, `BothRuntimesFixture` |

---

### 1.3 `E:/intochat/modules` — Complete Project & Test Map (54 `.csproj`, 28 NuGet Packages)

#### Production Projects (28 Packable `.csproj`)
| # | Package / Project Name | Path | `.cs` Files | Total Lines | Non-Empty | Internal `ProjectReference`s | `DigitalBrain.*` Package Dependencies | External NuGet Dependencies |
|---:|---|---|---:|---:|---:|---|---|---|
| 1 | `IntoChat.OS.Modules.ArtCraft` | [ArtCraft.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/ArtCraft/ArtCraft/src/IntoChat.OS.Modules.ArtCraft/IntoChat.OS.Modules.ArtCraft.csproj) | 6 | 140 | 124 | `Mcp.Contracts` | `DigitalBrain` | - |
| 2 | `IntoChat.OS.Modules.ClickHouse.Contracts` | [ClickHouse.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/ClickHouse/Contracts/IntoChat.OS.Modules.ClickHouse.Contracts/IntoChat.OS.Modules.ClickHouse.Contracts.csproj) | 7 | 77 | 69 | - | `DigitalBrain` | - |
| 3 | `IntoChat.OS.Modules.ClickHouse` | [ClickHouse.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/ClickHouse/src/IntoChat.OS.Modules.ClickHouse/IntoChat.OS.Modules.ClickHouse.csproj) | 7 | 503 | 446 | `ClickHouse.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Http` |
| 4 | `IntoChat.OS.Modules.Google.Auth` | [Google.Auth.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Google/Auth/src/IntoChat.OS.Modules.Google.Auth/IntoChat.OS.Modules.Google.Auth.csproj) | 5 | 253 | 228 | - | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Http` |
| 5 | `IntoChat.OS.Modules.Google.Drive` | [Google.Drive.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Google/Drive/src/IntoChat.OS.Modules.Google.Drive/IntoChat.OS.Modules.Google.Drive.csproj) | 11 | 1,039 | 936 | `Google.Auth` | `DigitalBrain`, `Capabilities.Connections`, `Capabilities.Storage` | `Microsoft.Extensions.Http` |
| 6 | `IntoChat.OS.Modules.Google.Gmail` | [Google.Gmail.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Google/Gmail/src/IntoChat.OS.Modules.Google.Gmail/IntoChat.OS.Modules.Google.Gmail.csproj) | 26 | 1,920 | 1,738 | `Google.Auth` | `DigitalBrain`, `Capabilities.Connections`, `Capabilities.Storage` | `Microsoft.Extensions.Http` |
| 7 | `IntoChat.OS.Modules.Tools.Contracts` | [Tools.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/IntoChat.OS/Tools/Contracts/IntoChat.OS.Modules.Tools.Contracts/IntoChat.OS.Modules.Tools.Contracts.csproj) | 14 | 164 | 147 | - | `DigitalBrain` | - |
| 8 | `IntoChat.OS.Modules.Tools` | [Tools.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/IntoChat.OS/Tools/src/IntoChat.OS.Modules.Tools/IntoChat.OS.Modules.Tools.csproj) | 10 | 744 | 665 | `Tools.Contracts` | `DigitalBrain`, `Capabilities.Storage` | `Microsoft.Extensions.Http` |
| 9 | `IntoChat.OS.Modules.LocalDisk` | [LocalDisk.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/LocalDisk/src/IntoChat.OS.Modules.LocalDisk/IntoChat.OS.Modules.LocalDisk.csproj) | 6 | 562 | 501 | - | `DigitalBrain`, `Capabilities.Storage` | - |
| 10 | `IntoChat.OS.Modules.Microsoft.Azure.Contracts` | [Azure.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/Contracts/IntoChat.OS.Modules.Microsoft.Azure.Contracts/IntoChat.OS.Modules.Microsoft.Azure.Contracts.csproj) | 9 | 563 | 505 | - | `DigitalBrain` | - |
| 11 | `IntoChat.OS.Modules.Microsoft.Azure` | [Azure.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/IntoChat.OS.Modules.Microsoft.Azure.csproj) | 11 | 1,148 | 1,033 | `Azure.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Azure.Identity`, `Azure.ResourceManager.*` (8 packages), `Azure.Security.KeyVault.Secrets`, `Microsoft.Extensions.Http` |
| 12 | `IntoChat.OS.Modules.Microsoft.GitHub.Contracts` | [GitHub.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/GitHub/Contracts/IntoChat.OS.Modules.Microsoft.GitHub.Contracts/IntoChat.OS.Modules.Microsoft.GitHub.Contracts.csproj) | 12 | 165 | 149 | - | `DigitalBrain` | - |
| 13 | `IntoChat.OS.Modules.Microsoft.GitHub` | [GitHub.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/GitHub/src/IntoChat.OS.Modules.Microsoft.GitHub/IntoChat.OS.Modules.Microsoft.GitHub.csproj) | 11 | 831 | 751 | `GitHub.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Http` |
| 14 | `IntoChat.OS.Modules.Microsoft.Orleans.Contracts` | [Orleans.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Orleans/Contracts/IntoChat.OS.Modules.Microsoft.Orleans.Contracts/IntoChat.OS.Modules.Microsoft.Orleans.Contracts.csproj) | 4 | 39 | 34 | - | `DigitalBrain` | - |
| 15 | `IntoChat.OS.Modules.Microsoft.Playwright.Contracts` | [Playwright.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Playwright/Contracts/IntoChat.OS.Modules.Microsoft.Playwright.Contracts/IntoChat.OS.Modules.Microsoft.Playwright.Contracts.csproj) | 8 | 132 | 116 | - | `DigitalBrain`, `UI` | - |
| 16 | `IntoChat.OS.Modules.Microsoft.Playwright` | [Playwright.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Playwright/src/IntoChat.OS.Modules.Microsoft.Playwright/IntoChat.OS.Modules.Microsoft.Playwright.csproj) | 9 | 760 | 666 | `Playwright.Contracts`, `Mcp.Contracts` | `DigitalBrain`, `UI` | - |
| 17 | `IntoChat.OS.Modules.Microsoft.Roslyn.Contracts` | [Roslyn.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Roslyn/Contracts/IntoChat.OS.Modules.Microsoft.Roslyn.Contracts/IntoChat.OS.Modules.Microsoft.Roslyn.Contracts.csproj) | 6 | 93 | 82 | - | `DigitalBrain` | - |
| 18 | `IntoChat.OS.Modules.Microsoft.Roslyn` | [Roslyn.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Roslyn/src/IntoChat.OS.Modules.Microsoft.Roslyn/IntoChat.OS.Modules.Microsoft.Roslyn.csproj) | 4 | 316 | 280 | `Roslyn.Contracts` | `DigitalBrain` | `Microsoft.CodeAnalysis.CSharp` |
| 19 | `IntoChat.OS.Modules.Microsoft.Windows` | [Windows.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Windows/src/IntoChat.OS.Modules.Microsoft.Windows/IntoChat.OS.Modules.Microsoft.Windows.csproj) | 9 | 892 | 797 | - | `DigitalBrain`, `Capabilities.Storage` | - |
| 20 | `IntoChat.OS.Modules.Mcp.Contracts` | [Mcp.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Mcp/Contracts/IntoChat.OS.Modules.Mcp.Contracts/IntoChat.OS.Modules.Mcp.Contracts.csproj) | 9 | 127 | 113 | - | `DigitalBrain` | - |
| 21 | `IntoChat.OS.Modules.Mcp` | [Mcp.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Mcp/src/IntoChat.OS.Modules.Mcp/IntoChat.OS.Modules.Mcp.csproj) | 9 | 798 | 708 | `Mcp.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Http`, `ModelContextProtocol` |
| 22 | `IntoChat.OS.Modules.Postgres.Contracts` | [Postgres.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Postgres/Contracts/IntoChat.OS.Modules.Postgres.Contracts/IntoChat.OS.Modules.Postgres.Contracts.csproj) | 18 | 247 | 222 | - | `DigitalBrain` | - |
| 23 | `IntoChat.OS.Modules.Postgres` | [Postgres.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Postgres/src/IntoChat.OS.Modules.Postgres/IntoChat.OS.Modules.Postgres.csproj) | 25 | 1,701 | 1,528 | `Postgres.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Diagnostics.HealthChecks`, `Npgsql` |
| 24 | `IntoChat.OS.Modules.Qdrant` | [Qdrant.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Qdrant/src/IntoChat.OS.Modules.Qdrant/IntoChat.OS.Modules.Qdrant.csproj) | 9 | 428 | 378 | - | `DigitalBrain` | `Qdrant.Client` |
| 25 | `IntoChat.OS.Modules.Salesforce.Contracts` | [Salesforce.Contracts.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Salesforce/Contracts/IntoChat.OS.Modules.Salesforce.Contracts/IntoChat.OS.Modules.Salesforce.Contracts.csproj) | 12 | 164 | 147 | - | `DigitalBrain` | - |
| 26 | `IntoChat.OS.Modules.Salesforce` | [Salesforce.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Salesforce/src/IntoChat.OS.Modules.Salesforce/IntoChat.OS.Modules.Salesforce.csproj) | 12 | 826 | 744 | `Salesforce.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Http` |
| 27 | `IntoChat.OS.Modules.Supabase` | [Supabase.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Supabase/src/IntoChat.OS.Modules.Supabase/IntoChat.OS.Modules.Supabase.csproj) | 9 | 435 | 384 | `Postgres`, `Postgres.Contracts` | `DigitalBrain`, `Capabilities.Connections` | `Microsoft.Extensions.Diagnostics.HealthChecks`, `Npgsql` |
| 28 | `IntoChat.OS.Modules.Time` | [Time.csproj](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Time/src/IntoChat.OS.Modules.Time/IntoChat.OS.Modules.Time.csproj) | 4 | 132 | 114 | - | `DigitalBrain` | - |

#### Test Projects in `modules` (26 `.csproj`, 486 Discovered Tests — **96 Never Executed in CI**)
| # | Test Project | Tier | Discovered Tests | Executed in `modules/ci.yml`? | Fixtures / Conformance |
|---:|---|---|---:|:---:|---|
| 1 | `IntoChat.OS.Modules.ArtCraft.Tests.Unit` | Unit | **13** | Yes | `ArtCraftModuleOnTestOS`, `TestOS`, `SiloHost` |
| 2 | `IntoChat.OS.Modules.ClickHouse.Tests.Unit` | Unit | **17** | Yes | `ClickHouseModuleOnTestOS`, `TestOS` |
| 3 | `IntoChat.OS.Modules.Google.Auth.Tests.Unit` | Unit | **15** | Yes | `TestOS` |
| 4 | `IntoChat.OS.Modules.Google.Drive.Tests.Unit` | Unit | **17** | Yes | `GoogleDriveModuleOnTestOS`, `TestOS` |
| 5 | `IntoChat.OS.Modules.Google.Gmail.Tests.Unit` | Unit | **46** | Yes | `GmailModuleOnTestOS`, `TestOS` |
| 6 | `IntoChat.OS.Modules.Tools.Tests.Unit` | Unit | **35** | Yes | `ToolsModuleOnTestOS`, `TestOS` |
| 7 | **`IntoChat.OS.Modules.LocalDisk.Tests.Integration`** | **Integration** | **29** | **NO (Unreferenced in `ci.yml`!)** | `LocalDiskConformance : StorageConformance` |
| 8 | `IntoChat.OS.Modules.LocalDisk.Tests.Unit` | Unit | **5** | Yes | `LocalDiskModuleOnTestOS`, `TestOS` |
| 9 | `IntoChat.OS.Modules.Microsoft.Azure.Tests.Unit` | Unit | **24** | Yes | `AzureModuleOnTestOS`, `TestOS` |
| 10 | `IntoChat.OS.Modules.Microsoft.GitHub.Tests.Unit` | Unit | **26** | Yes | `GitHubModuleOnTestOS`, `TestOS` |
| 11 | **`IntoChat.OS.Modules.Microsoft.Playwright.Tests.Browser`** | **Browser** | **4** | **NO (Unreferenced in `ci.yml`!)** | Playwright Chromium |
| 12 | `IntoChat.OS.Modules.Microsoft.Playwright.Tests.Unit` | Unit | **21** | Yes | `PlaywrightModuleOnTestOS`, `TestOS` |
| 13 | `IntoChat.OS.Modules.Microsoft.Roslyn.Tests.Unit` | Unit | **11** | Yes | `RoslynModuleOnTestOS`, `TestOS`, `SiloHost` |
| 14 | **`IntoChat.OS.Modules.Microsoft.Windows.Tests.Integration`** | **Integration** | **5** | **NO (Unreferenced in `ci.yml`!)** | - |
| 15 | `IntoChat.OS.Modules.Microsoft.Windows.Tests.Unit` | Unit | **42** | Yes | `WindowsClipboardConformance : StorageConformance`, `WindowsModuleOnTestOS` |
| 16 | **`IntoChat.OS.Modules.Mcp.Tests.Integration`** | **Integration** | **4** | **NO (Unreferenced in `ci.yml`!)** | `TestOS`, `SiloHost`, `BothRuntimesFixture` |
| 17 | `IntoChat.OS.Modules.Mcp.Tests.Unit` | Unit | **17** | Yes | `McpModuleOnTestOS`, `TestOS` |
| 18 | **`IntoChat.OS.Modules.Postgres.Tests.Integration`** | **Integration** | **30** | **NO (Unreferenced in `ci.yml`!)** | `TableStoreFacts`, `PostgresProviderFacts` |
| 19 | `IntoChat.OS.Modules.Postgres.Tests.Unit` | Unit | **46** | Yes | `PostgresModuleOnTestOS`, `TestOS` |
| 20 | `IntoChat.OS.Modules.Qdrant.Tests.Unit` | Unit | **8** | Yes | - |
| 21 | **`IntoChat.OS.Modules.Salesforce.Tests.Integration`** | **Integration** | **3** | **NO (Unreferenced in `ci.yml`!)** | - |
| 22 | `IntoChat.OS.Modules.Salesforce.Tests.Unit` | Unit | **22** | Yes | `SalesforceModuleOnTestOS`, `TestOS` |
| 23 | `IntoChat.OS.Modules.Supabase.Tests.Unit` | Unit | **6** | Yes | - |
| 24 | **`IntoChat.OS.Modules.Time.Tests.Integration`** | **Integration** | **21** | **NO (Unreferenced in `ci.yml`!)** | `TestOS`, `SiloHost`, `BothRuntimesFixture` |
| 25 | `IntoChat.OS.Modules.Time.Tests.Unit` | Unit | **9** | Yes | `TimeModuleOnTestOS`, `TestOS`, `SiloHost`, `BothRuntimesFixture` |
| 26 | `IntoChat.Modules.Architecture.Tests.Unit` | Architecture | **6** | Yes | Static architecture checks |

---

### 1.4 `E:/intochat/os` — Complete Project & Test Map (88 `.csproj` + 4 Dart Packages, 40 Packable NuGet Packages)

#### Production Projects in `os` (47 `.csproj` + 1 Template `.csproj`)
| # | Project Name | Group | Packable | `.cs` Files | Total Lines | Non-Empty | Target Disposition (Section 3.4) |
|---:|---|---|:---:|---:|---:|---:|---|
| 1 | `IntoChat.OS` | Core | **Yes** | 6 | 110 | 94 | **Delete** (transitional duplicates of `DigitalBrain`) |
| 2 | `IntoChat.OS.Contracts.Compute` | Core | **Yes** | 0 | 0 | 0 | **Delete** (empty type-forwarding shim to `DigitalBrain.Capabilities.Compute`) |
| 3 | `IntoChat.OS.Contracts.Configuration` | Core | **Yes** | 3 | 78 | 69 | Fold into `IntoChat.OS.Kernel` |
| 4 | `IntoChat.OS.Framework` | Hosting | **Yes** | 37 | 1,316 | 1,165 | Split: move `CallFilter` to `DigitalBrain.Runtime.Orleans`, `Machine/*` to `OsModule`, delete duplicates |
| 5 | `IntoChat.OS.ServiceDefaults` | Hosting | No | 9 | 892 | 780 | Fold into `IntoChat.OS.Kernel` |
| 6 | `IntoChat.OS.Aspire.Client` | Aspire | No | 1 | 38 | 31 | Delete / fold into `Client.Orleans` |
| 7 | `IntoChat.OS.Aspire.Hosting` | Aspire | **Yes** | 51 | 3,897 | 3,444 | Replace per-module hosting classes with manifest-driven `Need.*` provisioner |
| 8 | `IntoChat.OS.Aspire.Server` | Aspire | No | 3 | 347 | 310 | Fold into `IntoChat.OS.Kernel` |
| 9 | `IntoChat.OS.AppHost` | AppHost | No | 2 | 369 | 322 | **Keep** in Thin OS Host |
| 10 | `IntoChat.OS.Apps` | First-Party Apps | No | 25 | 12,068 | 10,494 | Delete `CreatorApp` (1,118 LOC); convert 10 shell/studio apps to neurons/synapses in `OsModule` |
| 11 | `IntoChat.OS.Cli` | CLI | **Yes** | 10 | 853 | 750 | **Delete** in Plan 5 (`app build/deploy/watch` obsolete) |
| 12 | `IntoChat.OS.Client` | Sandbox Client | **Yes** | 13 | 1,210 | 1,063 | **Delete** in Plan 5 (only used by `CSharp.Sandbox`) |
| 13 | `IntoChat.OS.Client.Orleans` | Orleans Client | No | 4 | 206 | 179 | Fold into `IntoChat.OS.Mcp` / `DigitalBrain.Runtime.Orleans` |
| 14 | `IntoChat.OS.Kernel` | Kernel Host | No | 19 | 1,984 | 1,736 | **Keep** as Thin OS Host |
| 15 | `IntoChat.OS.Kernel.Composition` | Kernel Composition | **Yes** | 37 | 3,267 | 2,867 | Move plain-neuron/confirmations/storage-streaming to `DigitalBrain.Runtime.*`; move `ConnectionStorage` to `OsModule`; delete `LocalRuntimeSessionEndpoints` |
| 16 | `IntoChat.OS.LocalSetup` | Local Preflight | No | 9 | 964 | 837 | **Keep** in Thin OS Host |
| 17 | `IntoChat.OS.Mcp` | MCP Edge | No | 4 | 198 | 174 | **Keep** (or fold into `IntoChat.OS.Kernel`) |
| 18 | `IntoChat.OS.Templates` | Templates | **Yes** | 2 | 85 | 72 | **Delete** (duplicate of `DigitalBrain.Templates`) |
| 19–31 | `IntoChat.Cloud.*` (13 projects: `Auth` [3], `Blob` [1], `Circle` [3], `Hosting` [1], `Lobby` [1], `Localization` [1], `Mail` [3], `Organisations` [2]) | Cloud Modules | **Yes** (13) | 136 | 9,679 | 8,493 | Move to `modules` (or `src/Modules/Cloud/*`; zero `IntoChat.OS` refs today!) |
| 32–34 | `IntoChat.OS.Modules.AI` (`Contracts`, `src`, `Testing`) | Module | **Yes** (3) | 212 | 11,880 | 10,502 | Move to `modules` |
| 35–36 | `IntoChat.OS.Modules.Flutter` (`Contracts`, `src`) | Module | **Yes** (2) | 164 | 7,090 | 6,252 | Move to `modules` |
| 37–38 | `IntoChat.OS.Modules.Apps` (`Contracts`, `src`) | Module | **Yes** (2) | 185 (+18) | 18,754 (+5,530) | 16,474 (+2,188) | **Delete 12,042 LOC** (Plan 5 `AppNeuron`/`Creator`/`Shell`); keep **4,632 LOC** (`Policies`, `Files`, `BrainScene`, `UserSettings`) in `OsModule` |
| 39 | `IntoChat.OS.Modules.Circles` | Module | **Yes** | 19 | 1,568 | 1,379 | Move to `modules` (absorb `features/os/Circle*`) |
| 40–42 | `IntoChat.OS.Modules.Flame` (`Contracts`, `Testing`, `src`) | Module | **Yes** (3) | 15 | 482 | 428 | Move to `modules` |
| 43–45 | `IntoChat.OS.Modules.Identity` (`Contracts`, `src`, `Testing`) | Module | **Yes** (3) | 145 | 9,683 | 8,547 | Core of `OsModule` (move `NeuronSurface` + `NeuronsService` gRPC to `DigitalBrain.Runtime.Http`) |
| 46–47 | `IntoChat.OS.Modules.Microsoft.Aspire` (`Contracts`, `src`) | Module | **Yes** (2) | 27 | 2,340 | 2,057 | Move to `modules` |
| 48–50 | `IntoChat.OS.Modules.Microsoft.CSharp` (`Contracts`, `src`, `Sandbox`) | Module | **Yes** (2) + Sandbox | 73 | 6,842 | 6,025 | **Delete 100%** in Plan 5 (replaced by `DigitalBrain.Runtime.Synapses`) |

*(See Section 1.2 of Subagent 2's table in lines 301–341 above for the per-project breakdown of all 40 test projects in `os` totaling **2,404** discovered tests / **2,439** static attribute + conformance rows, plus **485** Flutter tests across `os/ui/core` [41], `os/ui/ui` [272], and `os/ui/shell` [172].)*

---

### 1.5 Workflows & Measured Job Durations Across All Repositories (Last 10 Runs)

Across the three code repositories (`digitalbrain`, `modules`, `os`) plus `intochat.docs`, there are **16 GitHub Actions workflow files** totaling **1,853 lines of YAML**:

#### Summary of Measured Workflow Durations (from `gh run list` & `gh run view --json jobs`)
| Repo | Workflow | Runs (`n`) | S / F / C | Run Duration All (`min / med / mean / max`) | Run Duration Success-Only (`min / med / mean / max`) | Critical-Path Job Breakdown (Median Success) |
|---|---|---:|:---:|---|---|---|
| `digitalbrain` | [ci.yml](file:///E:/intochat/digitalbrain/.github/workflows/ci.yml) (327L) | 10 | 4 / 6 / 0 | `51s / 142s / 173.9s / 316s` | `174s / 246.5s / 245.8s / 316s` | `format`:76s, `build`:81.5s, `no-orleans`:33s, `facts`:123s, `runtime`:227s, `product`:85s, `template`:103s → `ci`:3s → `publish`:63.5s (`template-feed` 100% unreachable) |
| `digitalbrain` | [release.yml](file:///E:/intochat/digitalbrain/.github/workflows/release.yml) (90L) | 7 | 3 / 4 / 0 | `27s / 75s / 479.4s / 3034s` | `75s / 82s / 1063.7s / 3034s` | `release` job: **`79s`** (`min=71s, max=85s`) |
| `digitalbrain` | [gitleaks.yml](file:///E:/intochat/digitalbrain/.github/workflows/gitleaks.yml) (35L) | 10 | 10 / 0 / 0 | `8s / 9s / 9.2s / 10s` | `8s / 9s / 9.2s / 10s` | `gitleaks`: **`6s`** |
| `modules` | [ci.yml](file:///E:/intochat/modules/.github/workflows/ci.yml) (156L) | 10 | 4 / 5 / 1 | `40s / 76s / 336.5s / 2252s` | `219s / 284.5s / 760s / 2252s` | `format`:41s, `build`:115.5s → `ci`:3s → `publish`:89.5s (**skips all 6 `.Tests.Integration` + 1 `.Tests.Browser` projects!**) |
| `modules` | [release.yml](file:///E:/intochat/modules/.github/workflows/release.yml) (68L) | 9 | 3 / 6 / 0 | `14s / 24s / 640.9s / 3032s` | `92s / 2471s / 1865s / 3032s` | `release` job: **`89s`** (`min=87s, max=93s`) |
| `modules` | [bump.yml](file:///E:/intochat/modules/.github/workflows/bump.yml) (66L) | 7 | 2 / 5 / 0 | `20s / 21s / 22.1s / 27s` | `20s / 20.5s / 20.5s / 21s` | `bump` job: **`17s`** (5 failures: 3x `401 Unauthorized` on `DigitalBrain.Analyzers`, 1x `NU1903 ILCompiler`, 1x missing `dependencies` label) |
| `modules` | [gitleaks.yml](file:///E:/intochat/modules/.github/workflows/gitleaks.yml) (34L) | 10 | 10 / 0 / 0 | `8s / 10s / 29.9s / 210s` | `8s / 10s / 29.9s / 210s` | `gitleaks`: **`6.5s`** |
| `os` | [ci.yml](file:///E:/intochat/os/.github/workflows/ci.yml) (238L) | 10 | 8 / 2 / 0 | `66s / 477.5s / 545s / 1766s` | **`447s / 485.5s (8.1m) / 637.2s / 1766s`** | `format`:237s, `build`:295s, `facts`:285s, `runtime`:213s, `product`:204s, `blob-storage`:73s → `ci`:3s → `publish`:157s (`report`:5s) |
| `os` | [release.yml](file:///E:/intochat/os/.github/workflows/release.yml) (129L) | 10 | 3 / 4 / 3 | `331s / 629.5s / 704.9s / 1160s` | **`1019s / 1102s (18.4m) / 1093.7s / 1160s`** | Re-runs `ci` (`build`:304s, `facts`:278.5s) + `e2e-latest` (`e2e`:400.5s, `browser`:183s) → `release` (**`706s`** = 11.8m building & pushing 3 images to GHCR) |
| `os` | [deploy.yml](file:///E:/intochat/os/.github/workflows/deploy.yml) (184L) | 10 | 7 / 3 / 0 | `333s / 1878s / 1670.7s / 3156s` | **`1659s / 1950s (32.5m) / 2073.7s / 3156s`** | `test` (`Deploy test`): **`1911.5s` (31.9m)** (`min=1396s, max=3152s`) → `prod` (`Deploy production`): **`91s`** (failed at Azure OIDC login in run `42046070757`) |
| `os` | [e2e.yml](file:///E:/intochat/os/.github/workflows/e2e.yml) (162L) | 10 | 2 / 8 / 0 | `180s / 324.5s / 866.6s / 3378s` | `352s / 366.5s / 366.5s / 381s` | `e2e`: **`362.5s`** (runs 0 tests due to `--filter-trait "needs=artcraft" --ignore-exit-code 8`!), `browser`: **`483s`** (runs 0 tests!) |
| `os` | [flutter.yml](file:///E:/intochat/os/.github/workflows/flutter.yml) (100L) | 10 | 6 / 4 / 0 | `219s / 248.5s / 347.2s / 1294s` | `225s / 255.5s / 249.2s / 264s` | `core-and-ui`:221s, `shell-tests`:162.5s, `analyze-and-build`:252s, `nginx-proxy`:10s |
| `os` | [bump.yml](file:///E:/intochat/os/.github/workflows/bump.yml) (66L) | 10 | **1 / 9 / 0** | `25s / 28s / 30.8s / 54s` | `26s / 26s / 26s / 26s` | `bump`: **`23s`** (9 failures: 3x `401 Unauthorized` on `DigitalBrain.Analyzers`, 6x `createPullRequest` forbidden; 1 success was a no-op) |
| `os` | [artcraft-images.yml](file:///E:/intochat/os/.github/workflows/artcraft-images.yml) (74L) | 8 | 8 / 0 / 0 | `130s / 195s / 260.9s / 767s` | `130s / 195s / 260.9s / 767s` | `photocraft-mcp`:156s, `filmcraft-mcp`:137s, `photocraft-web`:136.5s, `filmcraft-web`:129s |
| `os` | [gitleaks.yml](file:///E:/intochat/os/.github/workflows/gitleaks.yml) (35L) | 10 | 10 / 0 / 0 | `27s / 40s / 153.3s / 1198s` | `27s / 40s / 153.3s / 1198s` | `gitleaks`: **`35s`** |
| `intochat.docs` | `deploy.yml` (89L) | 10 | 9 / 1 / 0 | `3s / 64s / 75.5s / 133s` | `48s / 64s / 83.6s / 133s` | `build`:48.5s → `deploy`:64s |

---

### 1.6 The Current Release-to-Container-App Deployment Path

When `gh release create v0.14.2` is run on `intochat/os`, **two workflows (`release.yml` and `deploy.yml`) trigger simultaneously on `release: types: [published]`**:

1. **Path A — [release.yml](file:///E:/intochat/os/.github/workflows/release.yml) (median `1,102s` = 18.4 min, dead-end for container images):**
   - Calls `./.github/workflows/ci.yml` (`workflow_call`, ~305s) and `./.github/workflows/e2e.yml` (`workflow_call`, ~400s — which runs **0 actual tests**; see Complexity #5).
   - Packs 40 NuGet `.nupkg` files from `Intochat.OS.slnx`, pushes them to GitHub Packages and `nuget.org`, and attaches `.nupkg`/`.snupkg` to the GitHub Release.
   - Logs into **GHCR (`ghcr.io`)** and builds/pushes 3 images with bare version tags (`:0.14.2` and `:latest`):
     - `ghcr.io/intochat/os:0.14.2` via `dotnet publish IntoChat.OS.Kernel.csproj -c Release -r linux-x64 /t:PublishContainer -p:PublishProfile=Container` (uses [Container.pubxml](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Properties/PublishProfiles/Container.pubxml)).
     - `ghcr.io/intochat/os-ui:0.14.2` via `docker build -f ui/shell/Dockerfile ui`.
     - `ghcr.io/intochat/os-sandbox:0.14.2` via `docker build -f .../Sandbox/Dockerfile .` + validates with `scripts/check-sandbox-image.sh`.
   - **Dead end:** Nothing in `deploy.yml`, `deploy.sh`, or Azure ever pulls from `ghcr.io/intochat/{os,os-ui,os-sandbox}`.

2. **Path B — [deploy.yml](file:///E:/intochat/os/.github/workflows/deploy.yml) → [deploy.sh](file:///E:/intochat/os/scripts/deploy/deploy.sh) (median `1,950s` = 32.5 min):**
   - **Step 1 (`Deploy test` job, median `1,911.5s` = 31.9 min):**
     - Polls `deployment_gate.py ci.yml "$GITHUB_SHA" push` up to 10 minutes waiting for the `push` CI run on `master` to finish.
     - Checks out `intochat/os` and private `intochat/os-deployment` (`.deployment-values`) via `DEPLOYMENT_VALUES_TOKEN`.
     - Frees runner disk space, installs .NET 11 + Flutter `3.41.4` + Bicep, writes temp `$RUNNER_TEMP/nuget.config` (`PACKAGE_TOKEN`), logs into GHCR (unused!) and Azure OIDC (`azure/login@v2`).
     - Runs `bash scripts/deploy/deploy.sh --env test --verify-repeatability`:
       1. Sets `image_tag=$(git rev-parse --short HEAD)` (e.g. `0a100d9`) and `server="acrintochattest.azurecr.io"`.
       2. Runs `az acr login -n acrintochattest` and **rebuilds all 3 images from source a second time**:
          - `acrintochattest.azurecr.io/os:<short_sha>` via `dotnet publish "$kernel" -c Release -r linux-x64 -t:PublishContainer` (**omitting `-p:PublishProfile=Container`**!).
          - `acrintochattest.azurecr.io/os-ui:<short_sha>` via `docker build -f ui/shell/Dockerfile`.
          - `acrintochattest.azurecr.io/os-sandbox:<short_sha>` via `docker build -f .../Sandbox/Dockerfile` (**omitting `check-sandbox-image.sh`**!).
       3. Runs `dotnet run --project IntoChat.OS.Kernel.csproj -c Release -- install --env test --config .deployment-values/test.json --image-tag <short_sha>`.
       4. Inside the kernel, [AzureInstaller.cs](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/AzureInstaller.cs) + [InstallPlan.cs](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/InstallPlan.cs) + [ArmAzure.cs](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs) idempotently provision via Azure ARM SDK: Resource Group (`rg-intochat-test`), User-Assigned Managed Identity, Storage Account (`os-state` + `brainlease` blobs, `brainlog` Azure Files share), Key Vault + RBAC + secrets, ACR, optional PostgreSQL Flexible Server, Log Analytics + App Insights, ACA Managed Environment, `CSharpDeployment` ACA Dynamic Session Pool (`ca-intochat-sandbox-test`), and the OS Container App (`ca-intochat-os-test`, `ca-intochat-ui-test`).
       5. Because `--verify-repeatability` is set, `deploy.sh` immediately runs `install` a **second time** against Azure and asserts the ACA revision and logs do not change.
       6. Queries `az containerapp show` and `az monitor log-analytics query` via `deploy_seeding_check.py` to assert all expected `install --print-env` variables exist on the live container and `"Integration seeding completed with 0 failures"` is logged.
       7. Runs `smoke.sh` against `/health/live`, `/health/ready`, `/`, and `/auth/providers`.
     - Records the `@sha256:` digests from `acrintochattest` into `tested-images.json` via `release_manifest.py record` and uploads workflow artifact `tested-images-${GITHUB_SHA}`.
   - **Step 2 (`Deploy production` job):**
     - Downloads `tested-images-${DEPLOY_SHA}`, attaches `tested-images.json` to the GitHub Release, runs `release_manifest.py promote` (`az acr import` by `@sha256:` digest from `acrintochattest` into `acrintochatprod` as `:<v0.14.2>`), and runs `deploy.sh --env prod --image-tag "$RELEASE_TAG"` (which skips image building because `image_exists()` finds `:<v0.14.2>` in `acrintochatprod`).

---

### 1.7 Every Place a Module Is Configured Today

A module's configuration is split across **six distinct mechanisms and ten code locations**:

| # | Mechanism | Key Prefix / Carrier | Where Declared | Where Bound / Consumed |
|---:|---|---|---|---|
| 1 | **Module Enablement & Topology** | `IntoChatOS:Topology` (`local`, `local+cloud`, `cloud`)<br>`IntoChatOS:Modules:{id}:Enabled` | `Module.LoadsUnder(topology)` + `Module.RequiresContainer` + hardcoded `IntoChatOSNames.LocalDisabledModules` ([IntoChatOSNames.cs:52](file:///E:/intochat/os/src/IntoChat.OS/Contracts.Configuration/IntoChatOSNames.cs#L52)) | [OperatingSystemModules.cs](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/OperatingSystemModules.cs), [RunProfile.cs](file:///E:/intochat/os/src/IntoChat.OS.AppHost/RunProfile.cs#L20-L32), [IntoChatOSServerBuilder.cs](file:///E:/intochat/os/src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Server/IntoChatOSServerBuilder.cs) |
| 2 | **Typed Module Options (`IModuleOptions`)** | `IntoChatOS:Modules:{id}:Options:{Prop}` | `ModuleManifest.Settings: [Setting.Of<TOptions>(...)]` + `[Setting(Required = true)]` on `TOptions` | Bound via `OsModule.BindOptions` ([OsModule.cs:98](file:///E:/intochat/os/src/IntoChat.OS/Hosting/IntoChat.OS.Framework/Modules/OsModule.cs#L98)); flattened into env vars in Aspire via `HostingModuleOptions.Resolve` ([HostingModuleOptions.cs:15](file:///E:/intochat/os/src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Composition/HostingModuleOptions.cs#L15)) and in Azure via `ManifestEnvironment` ([ManifestEnvironment.cs:25](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/ManifestEnvironment.cs#L25)) |
| 3 | **Declared Module Secrets (`Secret.Named`)** | `IntoChatOS:Modules:{id}:Secrets:{name}` | `ModuleManifest.Secrets: [Secret.Named("...")]` | Projected to Aspire secret parameters in [ModuleSecretProjection.cs:62](file:///E:/intochat/os/src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Composition/ModuleSecretProjection.cs#L62); mapped to Key Vault `{id}-{name}` in [ManifestEnvironment.cs:63](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/ManifestEnvironment.cs#L63); read at runtime via `secrets@1` `VaultPort.Named` ([VaultPort.cs:27](file:///E:/intochat/os/src/IntoChat.OS.Modules/IntoChat.OS/Identity/src/IntoChat.OS.Modules.Identity/Ports/VaultPort.cs#L27)) |
| 4 | **Deployment Integrations (`IntegrationSchema`)** | `IntoChatOS:Integrations:{id}:{Field}` | Static `Integration` / `Integrations` property on `Module` class (`RequiresSecret`, `OffersSecret`, `RequiresSetting`, `OffersSetting`) | Projected in Aspire by [ModuleSecretProjection.cs:23](file:///E:/intochat/os/src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Composition/ModuleSecretProjection.cs#L23); mapped to Key Vault `{id}-{field}` by [ManifestEnvironment.cs:43](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/ManifestEnvironment.cs#L43); seeded at silo startup by `IntegrationSeeder` ([IntegrationSeeder.cs:25](file:///E:/intochat/os/src/IntoChat.OS.Modules/IntoChat.OS/Identity/src/IntoChat.OS.Modules.Identity/Authority/Integrations/IntegrationSeeder.cs#L25)) into `IntegrationNeuron`; read via `IModuleIntegrations<T>.Get(id)` |
| 5 | **User / Brain Connections (`ConnectorDefinition`)** | Sealed per-brain grain state (`{brain}/connections/{id}`) | Static `Connector` / `Connectors` property on `Module` class + `[Neuron]` subclassing `ConnectionNeuron<TState>` | Opened via `UseCredentialAsync` ([ConnectionNeuron.cs:270](file:///E:/intochat/digitalbrain/src/DigitalBrain.Capabilities.Connections/Connections/ConnectionNeuron.cs#L270)); OAuth flows use `RequiredIntegrations` from Mechanism #4 |
| 6 | **Ad-Hoc Escape Hatches (Keyed Services, `ConnectionStrings:*`, Custom Aspire Hosting & Custom Installer Hooks)** | `ConnectionStrings:qdrant`, keyed `NpgsqlDataSource`, keyed `TimeProvider`, keyed `BlobServiceClient`, `ModuleHosting<T>`, `ModuleDeployment<T>`, `InstallConfig.PostgresAdminPassword` | Scattered across `QdrantModule.cs:34`, `PostgresHosting.cs:13`, `GmailModule.cs:68`, `CSharpModule.cs:80`, `IntoChat.OS.Aspire.Hosting/Modules/*` (16 files), `InstallPlan.cs:127`, `CSharpDeployment.cs`, `.deployment-values/<env>.json` | Bypass `ModuleManifest` entirely; require bespoke code in Aspire, the Kernel installer, and tests for every infrastructure-backed module |

---

## 2. Main Sources of Complexity (Ranked)

### Rank 1: Three Overlapping Programmability Layers (`ReactionWriter`, Duplicate `Shell/*` Scripts, and `App<TState>`) Mixed Together with Sandboxed Code Execution (`CSharp.Sandbox` + `IntoChat.OS.Client`)
- **What it is:** The codebase currently mixes together two things that are genuinely distinct and must be separated cleanly:
  1. **True Redundancies & Dead Duplication (~4,500 LOC — should be deleted):**
     - `ReactionWriter` + `ReactionExamples` ([ReactionWriter.cs](file:///E:/intochat/os/src/IntoChat.OS.Modules/IntoChat.OS/Apps/src/IntoChat.OS.Modules.Apps/Apps/FastPath/ReactionWriter.cs), 892 LOC), which invokes an LLM to generate C# `App<TState>` classes for simple user rules that are now 100% replaced by declarative `Policy` records (`PoliciesNeuron` + `PolicyWriter`, landed in Plans 1–4).
     - `IntoChat.OS.Modules.Apps/Shell/*` (18 files, 2,188 LOC), which are uncompiled string/script duplicates of `Desktop`, `Settings`, and `Workspace` that already exist as compiled C# classes in `IntoChat.OS.Apps`.
     - Legacy packaging/CLI shims (`DigitalBrain.Runtime.Apps`, `DigitalBrain.Apps.Packaging`, `IntoChat.OS.Cli`, ~1,318 LOC).
  2. **Out-of-Process Sandboxed Code Execution & Permission-Gated Remote Client (`CSharp.Sandbox` + `IntoChat.OS.Client` + `RuntimeEdge` + `CreatorApp` — MUST KEEP & ELEVATE):**
     - Earlier draft plans ("Plan 5: Cutover & Delete") grouped `CSharp.Sandbox`, `IntoChat.OS.Client`, `RuntimeEdge`, and `Creator` into the same deletion bucket as `ReactionWriter` on the assumption that in-process `[Synapse]` (`DigitalBrain.Runtime.Synapses`) + `PoliciesNeuron` replaced everything.
     - **Why that assumption was wrong:** In-process `[Synapse]` runs inside the Orleans silo (`SynapseLoadContext`), so Roslyn analyzers `DB0041`–`DB0044` **strictly forbid** `System.IO`, `System.Net`, `Process`, `Environment`, `Reflection`, `Task.Run`, threads, and arbitrary packages inside a `[Synapse]`.
     - By contrast, `CSharp.Sandbox` (`os-sandbox` / Azure ACA Dynamic Session Pool) + [IntoChatOSConnection.cs](file:///E:/intochat/os/src/IntoChat.OS.Client/IntoChat.OS.Client/IntoChatOSConnection.cs) + [SessionConnection.cs](file:///E:/intochat/os/src/IntoChat.OS.Client/IntoChat.OS.Client/Runtime/SessionConnection.cs) + [RuntimeEdge.cs](file:///E:/intochat/os/src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp/Edge/RuntimeEdge.cs) solve a capability neither `[Synapse]` nor `Policy` can provide: **unrestricted C# code execution in an isolated container that connects back to the brain as an authenticated, permission-gated client without ever exposing vault secrets**.
     - Every call from sandboxed or remote code (`connection.Neuron<IGmail>("gmail")`, `connection.Get<ITable>(...)`) goes over `/brains/{brainId}/runtime/v1/{invoke,report,stream}` with an HMAC `SessionToken`, is stamped with `RuntimeCallerContext.Stamp(caller)`, passes through `[Access]` and `CallFilter` (`MissingGrant`, `MissingAllowance`, `LimitReached`, `OutsideWorkspace`), and executes inside the silo where `VaultNeuron` / `ConnectionNeuron<TState>` hold the raw OAuth/API credentials.
- **Where it lives:**
  - `digitalbrain`: `src/DigitalBrain/Apps/*` (18 files, 1,112 LOC), `src/DigitalBrain.Runtime.Apps/*` (3 files, 303 LOC), `src/DigitalBrain.Apps.Packaging/*` (4 files, 265 LOC), `src/DigitalBrain.Analyzers/Apps/*` (10 files, 691 LOC), `src/DigitalBrain.Testing/Apps/*` (6 files, 608 LOC).
  - `os`: `src/IntoChat.OS.Modules/Microsoft/CSharp/*` (75 files, 6,143 LOC), `src/IntoChat.OS.Client/*` + `LocalRuntimeSessionEndpoints.cs` (14 files, 1,238 LOC), `src/IntoChat.OS.Modules/IntoChat.OS/Apps/*` (140 files, 12,042 LOC), `src/IntoChat.OS.Cli/*` (10 files, 750 LOC), `src/IntoChat.OS.Apps/Creator/*` (3 files, 1,118 LOC).
- **What it costs today:**
  - Because `IntoChat.OS.Client` and `RuntimeEdge` currently live inside `os` (`IntoChat.OS.Modules.Microsoft.CSharp/Edge/RuntimeEdge.cs`) instead of `digitalbrain`, the framework itself cannot test remote client / sandbox sessions in its own conformance suite, and the boundary between in-process `[Synapse]` vs. out-of-process `CSharp.Sandbox` vs. declarative `Policy` is blurred.
  - Meanwhile, `ReactionWriter` (892 LOC) and the duplicate `Apps/Shell/*` scripts (2,188 LOC) add 3,080 LOC of dead weight and duplicate tests.
- **Does the original reason still hold? Yes for `CSharp.Sandbox`, `IntoChat.OS.Client` (`RuntimeEdge`), and `CreatorApp`; No for `ReactionWriter` and duplicate `Shell/*` scripts.**
  - **The 3-tier programmability model is clean and complementary when separated by trust & cost:**
    1. **`Policy` (`PoliciesNeuron` / `PolicyWriter`):** Zero-compilation declarative rules (*when signal → if condition → call action*).
    2. **`[Synapse]` (`DigitalBrain.Runtime.Synapses`):** Fast, in-process compiled C# state & reaction units running inside `SynapseLoadContext` under strict `DB0041`–`DB0044` analyzer sandboxing (no I/O, no network, no threads).
    3. **`CSharp.Sandbox` (`os-sandbox`) + `DigitalBrain.Client` (`IntoChat.OS.Client` + `RuntimeEdge`) + `CreatorApp`:** Isolated container / process execution for unrestricted C# code, scripts, and external agents connecting back to the brain over `/brains/{brainId}/runtime/v1/*` with full `CallFilter` permissions, `ICapabilityBroker` (`LIVE` vs `SANDBOX / TEST CREDENTIALS` in `Creator`), and zero-leakage vault access.

---

### Rank 2: The Pipeline Set, 7x Redundant Solution Compiles, and the Double Container Image Build
- **What it is:**
  1. **Too many workflows (16 total; 8 in `os` alone):** `os` has `ci.yml`, `e2e.yml`, `release.yml`, `deploy.yml`, `flutter.yml`, `bump.yml`, `artcraft-images.yml`, and `gitleaks.yml`.
  2. **7x redundant full-solution restores and builds per push in `os/ci.yml`:** In [os/.github/workflows/ci.yml](file:///E:/intochat/os/.github/workflows/ci.yml), `format` (237s), `build` (295s), `facts` (285s), `runtime` (213s), `product` (204s), `blob-storage` (73s), and `publish` (157s) each independently restore and compile the 88-project `Intochat.OS.slnx` from scratch on separate runners.
  3. **Double CI + Double Container Image Build on Release:** Publishing a release triggers both `release.yml` (median **18.4m**) and `deploy.yml` (median **32.5m**) simultaneously. `release.yml` re-runs all of `ci.yml` and `e2e.yml` from scratch (even though `master` just ran `ci.yml`) and builds/pushes `os`, `os-ui`, and `os-sandbox` to **GHCR (`ghcr.io/intochat/*`)**. At the exact same time, `deploy.yml` calls `scripts/deploy/deploy.sh --env test --verify-repeatability`, which **rebuilds all 3 images from source a second time** into **ACR (`acrintochattest.azurecr.io/*:<short_sha>`)** with different flags (`deploy.sh` omits `-p:PublishProfile=Container` on `os` and omits `check-sandbox-image.sh` on `os-sandbox`), runs `IntoChat.OS.Kernel install` **twice** in `test` (`--verify-repeatability`), and promotes the ACR images to `acrintochatprod`. **The GHCR images built by `release.yml` never reach test or production.**
- **Where it lives:**
  - `os/.github/workflows/{ci.yml, e2e.yml, release.yml, deploy.yml, flutter.yml, bump.yml, artcraft-images.yml, gitleaks.yml}` (988 lines)
  - `os/scripts/deploy/{deploy.sh, release_manifest.py, deployment_gate.py, deploy_seeding_check.py, smoke.sh}` (549 lines)
  - `digitalbrain/.github/workflows/{ci.yml, release.yml, gitleaks.yml}` (452 lines)
  - `modules/.github/workflows/{ci.yml, release.yml, bump.yml, gitleaks.yml}` (324 lines)
- **What it costs:**
  - **32.5 minutes** median (`1,950s`, up to `3,156s` = 52.6m) for a release-to-deploy pass, plus **18.4 minutes** (`1,102s`) burned in parallel by `release.yml`.
  - **~38 runner-minutes of CPU time** on every push to `os` `master` (`ci.yml` + `flutter.yml` + `gitleaks.yml`), and **~85 runner-minutes** on every release.
  - **Multiple production/release incidents in the last 48 hours:**
    - `CONTAINER2010` tag escaping failure after 12 minutes in `release.yml` (run `41762249117`, commit `0c98816e8`, fixed in `19c9aa0f2`).
    - Dirty working tree abort in `deploy.sh` because `dotnet nuget update source` mutated `nuget.config` (commit `0c98816e8`).
    - Azure OIDC token expiration (`AADSTS700024`) mid-deploy because building images inside `deploy.sh` took >19 minutes while GitHub's OIDC JWT expires in 5 minutes — requiring 5 consecutive fix commits (`19c9aa0f2`, `600cd2ef5`, `c78e4aa6d`, `50bd62eca`, `13acfaf1f`) and a custom `warm_azure_tokens()` bash function that manually rewrites `~/.azure/service_principal_entries.json`!
    - `deploy.yml` timing out waiting for `ci.yml` on `push` (runs `41819876490`, `41821201033`, fixed in `f4f6549a6` and `0659565e1`).
    - `Deploy production` failing on `v0.14.2` (run `42046070757`) after waiting 23.3 minutes for `Deploy test` (`No subscriptions found for ***`).
- **Why it exists:** `release.yml` was written to publish NuGet packages and GHCR images for public/release consumption (`0c98816e8`), while `deploy.yml` and `deploy.sh` were moved into `os` from the deployment consolidation (`14a523499`, Oct 28) and updated to build into ACR and promote digests (`065956521`, `0a100d93b`, Nov 5) without unifying `release.yml` and `deploy.yml`.
- **Does that reason still hold? No.** Violates [release-and-deployment-rule.md](file:///E:/intochat/intochat.docs/assessment/release-and-deployment-rule.md) (*"Build every deployable artifact once in the test job... Promote the exact digests that passed test"*). One release must be one workflow run that builds the images once and deploys those exact digests to `test` and `prod`.

---

### Rank 3: The Three-Repository Package Cascade, GitHub Packages Ownership, and NuGet Source Mapping
- **What it is:** Every framework change requires a 3-step release/bump cascade (`digitalbrain` → `modules` → `os`). Both `modules/nuget.config` and `os/nuget.config` use `<packageSourceMapping>` to pin `DigitalBrain*` and `IntoChat*` **exclusively** to `github` (`https://nuget.pkg.github.com/intochat/index.json`). Because those package names were first published from `intochat/os` before the tri-repo split, GitHub Packages scopes their ownership to `intochat/os`:
  - `GITHUB_TOKEN` in `digitalbrain` and `modules` gets `403 Forbidden` when pushing packages owned by `os`, and `GITHUB_TOKEN` in `modules` and `os` gets `403 Forbidden` when restoring cross-repo packages.
  - Every repo has to carry a classic PAT (`PACKAGE_TOKEN`).
  - Even though `DigitalBrain.*` is published to `nuget.org`, `modules` and `os` are forbidden by `<packageSourceMapping>` from restoring `DigitalBrain.*` from `nuget.org`.
  - Both `modules/Directory.Build.targets` and `os/Directory.Build.targets` unconditionally inject `<PackageReference Include="DigitalBrain.Analyzers" />` into every MSBuild project, including `dotnet run scripts/bump-pins.cs`, forcing a simple XML regex script to authenticate with GitHub Packages and restore Roslyn analyzers before it can edit `Directory.Packages.props`.
  - Locally, `Directory.Build.local.props` (`<RestoreAdditionalProjectSources>E:/feeds/local</RestoreAdditionalProjectSources>`) is rejected by NuGet for `DigitalBrain*` and `IntoChat*` unless the developer also overrides `<packageSourceMapping>` in a scratch `nuget.config`.
- **Where it lives:**
  - [modules/nuget.config](file:///E:/intochat/modules/nuget.config), [os/nuget.config](file:///E:/intochat/os/nuget.config), [os/ui/shell/nuget.config](file:///E:/intochat/os/ui/shell/nuget.config)
  - [modules/Directory.Build.targets](file:///E:/intochat/modules/Directory.Build.targets), [os/Directory.Build.targets](file:///E:/intochat/os/Directory.Build.targets)
  - [modules/Directory.Packages.props](file:///E:/intochat/modules/Directory.Packages.props), [os/Directory.Packages.props](file:///E:/intochat/os/Directory.Packages.props)
  - [modules/scripts/bump-pins.cs](file:///E:/intochat/modules/scripts/bump-pins.cs), [os/scripts/bump-pins.cs](file:///E:/intochat/os/scripts/bump-pins.cs)
  - `digitalbrain/.github/workflows/{ci.yml, release.yml}`, `modules/.github/workflows/{ci.yml, release.yml, bump.yml}`, `os/.github/workflows/{ci.yml, release.yml, bump.yml}`
- **What it costs:**
  - **14 of 17 `bump.yml` runs failed across `modules` and `os`** (2/7 succeeded in `modules`; **1/10 succeeded in `os`**, and that 1 was a no-op where no pins changed!). In `os`, `bump.yml` is **still broken right now** (`GraphQL: GitHub Actions is not permitted to create or approve pull requests (createPullRequest)` in runs `41985263748` through `42037918001`).
  - **50%–60% CI/Release failure rate during cascades:** `modules` `ci.yml` failed 5 of the last 10 runs; `modules` `release.yml` failed 6 of 9 runs; `digitalbrain` `release.yml` failed 4 of 7 runs.
  - **At least 12 fix commits in 48 hours** fighting feed auth, token fallback chains (`PACKAGE_TOKEN || DEPLOYMENT_VALUES_TOKEN || GITHUB_TOKEN` in `a0441d93e`, reverted 11 minutes later in `999e8958f`), package-index race conditions (`42cf24921` adding a 50s `curl` loop + release asset re-pusher, removed in `8812e4b04`), `bump-pins.cs` `401 Unauthorized` (`2962de427`, `f82c9b4`), `NU1903 ILCompiler` AOT vulnerability (`6e2a8762f`, `d9dfd19`), and missing PR labels (`b05e21f`).
  - **Dependabot PRs fail immediately in `os` and `modules`** (e.g., `os` run `42038133960`) because Dependabot runs do not receive `PACKAGE_TOKEN` and therefore cannot restore `DigitalBrain*` or `IntoChat*` from `github`.
- **Why it exists:** Introduced on Oct 29 – Nov 3, 2026 (`0c85bc2`, `14072e0`, `ce31131e6`, `3a8fc1b`; documented in [repo-boundaries-and-release-flow.md](file:///E:/intochat/intochat.docs/assessment/repo-boundaries-and-release-flow.md) and [2026-11-03-framework-boundaries-and-release-flow.md](file:///E:/intochat/intochat.docs/assessment/plans/2026-11-03-framework-boundaries-and-release-flow.md)) to enforce architectural boundaries between the open-source framework (`digitalbrain`), capability modules (`modules`), and the OS (`os`).
- **Does that reason still hold? Only for `digitalbrain` vs `os`, not for splitting `modules` into a separate repository or forcing `DigitalBrain*` through GitHub Packages.**
  - `digitalbrain` is a genuine reusable framework published to `nuget.org` (19 packages). If `DigitalBrain.*` is consumed from `nuget.org` (or local feed), `PACKAGE_TOKEN` is never needed for `DigitalBrain.*`.
  - By contrast, `modules` (`IntoChat.OS.Modules.*`) has **only one consumer (`intochat/os`)**, shares the exact same version lockstep (`0.14.2`), already leaves 16 of the 33 modules inside `os` anyway (`AI`, `Flutter`, `Flame`, `Circles`, `Identity`, `Apps`, `Aspire`, `CSharp`, plus 8 `Cloud.*` modules), and its architectural boundary (`modules` may only reference `DigitalBrain.*`, never `IntoChat.OS.*`) is already enforced in 6 seconds by MSBuild `<ProjectReference>` rules and `IntoChat.Modules.Architecture.Tests.Unit`. Keeping `modules` in a separate git repo adds an entire NuGet publishing/bumping hop and splits the module catalog in half for zero architectural gain.

---

### Rank 4: Six-Way Module Configuration Split & Hidden Startup/Runtime Failure Modes
- **What it is:** Although `ModuleManifest` (`DigitalBrain/Modules/ModuleManifest.cs`) was introduced on Oct 25 (`3375cbbcc`) to be the single declaration of what a module needs, modules actually configure settings, secrets, connections, and backing containers across **six overlapping mechanisms** (Section 1.7):
  1. `ModuleManifest.Settings` (`Setting.Of<TOptions>`)
  2. `ModuleManifest.Secrets` (`Secret.Named`)
  3. `ModuleManifest.Needs` (`Need.State`, `Need.HttpClient`, `Need.Webhook`, `Need.Container`, etc.)
  4. Static `IntegrationSchema` properties (`RequiresSecret`, `OffersSecret`, `RequiresSetting`) seeded via `IntegrationSeeder` into `IntegrationNeuron` (`IntoChatOS:Integrations:{id}:{Field}`)
  5. Static `ConnectorDefinition` properties (`ConnectionNeuron<TState>`)
  6. Ad-hoc `IConfiguration.GetConnectionString("qdrant")`, `[FromKeyedServices]` singletons (`PostgresHosting.DataSourceKey`), custom `ModuleHosting<T>` classes in `IntoChat.OS.Aspire.Hosting/Modules/*` (16 files, 1,450 LOC), hardcoded `InstallPlan.PostgresSpecs` (`InstallConfig.PostgresAdminPassword`), and `.deployment-values/<env>.json`.
- **Where it lives:**
  - `digitalbrain/src/DigitalBrain/Modules/*` & `digitalbrain/src/DigitalBrain.Capabilities.Connections/Connectors/*`
  - `os/src/IntoChat.OS/Hosting/IntoChat.OS.Framework/Modules/OsModule.cs`
  - `os/src/IntoChat.OS.Modules/IntoChat.OS/Identity/src/.../Authority/Integrations/{IntegrationSeeder.cs, IntegrationNeuron.cs, ModuleIntegrations.cs}`
  - `os/src/IntoChat.OS.Aspire/IntoChat.OS.Aspire.Hosting/Composition/{IntoChatOSBuilder.cs, HostingModuleOptions.cs, ModuleSecretProjection.cs}` & `Modules/*` (16 files)
  - `os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/{InstallConfig.cs, InstallPlan.cs, ManifestEnvironment.cs}`
- **What it costs:**
  - **Every infrastructure module (`Postgres`, `Qdrant`, `ClickHouse`, `Supabase`, `ArtCraft`, `AI`, `CSharp`) is wired 4 different ways** (in the module itself, in `IntoChat.OS.Aspire.Hosting`, in `InstallPlan.cs` / `ManifestEnvironment.cs`, and in tests).
  - **Two different secret mechanisms for the same concept:** Deployment-level secrets can be declared either via `ModuleManifest.Secrets` (`IntoChatOS:Modules:{m}:Secrets:{s}` → `VaultPort.Named`) OR via `IntegrationSchema.RequiresSecret` (`IntoChatOS:Integrations:{id}:{f}` → `IntegrationSeeder` → `IntegrationNeuron` → `IModuleIntegrations<T>`).
  - **Key-name mismatch in `InstallPlan.cs` vs `ManifestEnvironment.cs` for Postgres:** When `PostgresAdminPassword` is set in `<env>.json`, `InstallPlan.cs:61-65` provisions Azure PostgreSQL Flexible Server and writes the connection string to Key Vault secret **`db-connection`**, whereas `ManifestEnvironment.cs:43-61` maps `PostgresModule.Integration` (`IntoChatOS__Integrations__postgres__ConnectionString`) to Key Vault secret **`postgres-connectionstring`**!
  - **Unconfigured `PostgresModule` crashes the entire Orleans silo on startup:** In `PostgresModule`, `PostgresSourceRegistry` registers as an `IIntegrationStartup` (`PostgresHosting.cs:26`) whose `InitializeAsync` unconditionally calls `Get(TablePlacement.Shared)` (`PostgresSourceRegistry.cs:25-26`). If `postgres` is enabled (which is the default on `Topology=cloud`) and `IntoChatOS:Integrations:postgres:ConnectionString` is not configured, `Get` sees `IntegrationStatus.Unconfigured` and throws `PostgresUnavailableException`, which `IntegrationSeeder.Execute` (`IntegrationSeeder.cs:56-60`) does not catch — crashing the Orleans silo during startup!
  - **Inconsistent SQLSTATE error classification in `PostgresProvider` vs `TableStore`:** When PostgreSQL dies mid-call with a server-side `PostgresException` (e.g., `57P01` admin shutdown or `08006` connection failure), `TableStore.Refusal` (`TableStore.cs:152-156`) correctly classifies `08*`, `28*`, `3D000`, `57P0*` as `PostgresUnavailableException` (HTTP 503), whereas `PostgresProvider.ReadAsync` (`PostgresProvider.cs:108-112`) catches `PostgresException` before `NpgsqlException` and turns *every* `PostgresException` into `PostgresQueryException` (HTTP 409).
- **Why it exists:** Evolved through six configuration redesigns between Sept 17 and Nov 3, 2026 (`c6cba6470` → `28ce92c73` → `3a724f8a4` → `3592114c5` → `3375cbbcc` → `58540e8`), each adding a cleaner layer (`ModuleManifest`, `Secret.Named`, `Need.Container`) without retiring `IntegrationSchema`, `IIntegrationStartup`, or the bespoke `IntoChat.OS.Aspire.Hosting/Modules/*` classes.
- **Does that reason still hold? No.** `IntegrationSchema` and `ModuleManifest.Secrets`/`ModuleManifest.Settings`/`ModuleManifest.Needs` duplicate the exact same job. Consolidating onto `ModuleManifest` alone eliminates `IntegrationSchema`, `IntegrationSeeder`, `IntegrationNeuron`, `IIntegrationStartup`, and the 16 bespoke `ModuleHosting<T>` files.

---

### Rank 5: OS Composition Weight — Framework Primitives & General Modules Sitting in `os`
- **What it is:** The owner's instinct is that `os` should be a thin host plus an `OsModule` carrying settings, connectors, and the shell's needs. Today, `E:/intochat/os` contains **47 production `.csproj` projects (75,303 non-empty C# LOC)** and publishes **40 NuGet packages** that nobody outside `os` consumes.
- **Where it lives & what is inside those 75,303 LOC:**
  1. **21,291 LOC (28.3%)** — Legacy `App<TState>` / `Creator` / `CSharp.Sandbox` / `Cli` / `Client` stack (Rank 1, Plan 5 deletion).
  2. **28,955 LOC (38.5%)** — General-purpose & Cloud modules living in `os` instead of the modules catalog: `AI` (10,452 LOC), `Flutter` (6,252 LOC), `Microsoft.Aspire` (2,057 LOC), `Circles` (1,379 LOC), `Flame` (390 LOC), and the 8 `IntoChat.Cloud.*` modules (8,425 LOC — which **already have zero references to `IntoChat.OS` or `IntoChat.OS.Framework`**).
  3. **2,299 LOC (3.1%)** — Transitional framework primitives in `IntoChat.OS` (94 LOC), `IntoChat.OS.Framework` (1,165 LOC), `IntoChat.OS.Kernel.Composition` (`BrainNeuron`, `PlainNeuronCallFilter`, `InstanceDirectoryGrain`, `ConfirmationsNeuron`, `StorageContentEndpoints`), and `Identity/Authority/Neurons` (`NeuronSurface`, `NeuronsService` gRPC), including **329 LOC of verbatim duplicates** of types already in `DigitalBrain.Runtime.Orleans` (`NeutralCallMapping`, `BrainAffinePlacement`, `SignalJournal`).
  4. **20,800 LOC (27.6%)** — The actual `OsModule` (`Identity` [7,915 LOC], `Policies` [259 LOC], `Files`/`VfsNeuron` [2,361 LOC], `BrainScene` [1,196 LOC], `UserSettings`/`Profile`/`InstalledModules` [249 LOC], and the 10 first-party shell/studio surfaces [8,900 LOC]).
  5. **~1,950 LOC (2.6%)** — The actual Thin OS Host (`IntoChat.OS.Kernel`, `IntoChat.OS.Mcp`, `IntoChat.OS.AppHost`, `IntoChat.OS.LocalSetup`).
- **What it costs:**
  - Building `Intochat.OS.slnx` compiles 88 `.csproj` projects (~295s in CI) and `release.yml` / `ci.yml` packs and pushes **40 NuGet packages** (~157s in CI) even though `os` is the top of the dependency tree and has zero downstream NuGet consumers!
- **Why it exists:** During the Oct 29 – Nov 3 tri-repo split (`5c68a87`, `4fc14acfa`), the 17 modules that had no reference to `IntoChat.OS.Framework` or `Identity` moved to `E:/intochat/modules`, while `AI`, `Flutter`, `Flame`, `Circles`, `Aspire`, `Identity`, `Apps`, and `Cloud.*` were left behind in `os` until `Identity` and `Apps` were decoupled.
- **Does that reason still hold? No.** Moving the remaining framework primitives (~1,970 LOC) into `DigitalBrain.Runtime.Orleans` / `DigitalBrain.Runtime.Http` and deleting the 329 LOC of duplicates decouples `AI`, `Flutter`, `Flame`, `Circles`, and `Aspire` from `IntoChat.OS.Framework`, allowing `os` to stop packing NuGet packages altogether and shrink its host layer to 3 projects (~1,500 LOC).

---

### Rank 6: Test Pyramid Blind Spots, Phantom `e2e.yml`, Multi-Tier Duplication, and Flakiness Under Load
- **What it is:** Across **4,784 discovered C# tests** (`1,894` in `digitalbrain`, `486` in `modules`, `2,404` in `os`) and **485 Flutter tests**, the test pyramid has four structural defects:
  1. **`os/.github/workflows/e2e.yml` is a 100% phantom workflow that runs 0 tests in both jobs:**
     - Job `e2e` ([e2e.yml:112](file:///E:/intochat/os/.github/workflows/e2e.yml#L112)) pulls 4 ArtCraft Docker images and runs `IntoChat.OS.Kernel.Tests.E2E.Aspire` with `--filter-trait "needs=artcraft" --ignore-exit-code 8`. Because **zero** tests in `os` have `[Trait("needs", "artcraft")]`, Microsoft.Testing.Platform exits with code `8` ("zero tests ran"), which `--ignore-exit-code 8` masks as green!
     - Job `browser` ([e2e.yml:136](file:///E:/intochat/os/.github/workflows/e2e.yml#L136)) runs `find src -name '*.Tests.Browser.csproj'`, finds nothing (because `Playwright.Tests.Browser` moved to `modules`), prints `"Browser test modules live in intochat/modules; skipping."`, and exits `0`.
     - Yet `release.yml` waits **6.7 minutes (`400.5s`)** on every release for `e2e-latest` to run **0 tests**.
  2. **96 tests in `modules` (all 6 `.Tests.Integration` projects + `Playwright.Tests.Browser`, 19.8% of the repo's tests) never run in CI:**
     - [modules/.github/workflows/ci.yml:69-90](file:///E:/intochat/modules/.github/workflows/ci.yml#L69-L90) hardcodes a `for project in ...` list of only the 19 `.Tests.Unit` projects (390 tests). `LocalDisk.Tests.Integration` (29), `Postgres.Tests.Integration` (30), `Time.Tests.Integration` (21), `Windows.Tests.Integration` (5), `Mcp.Tests.Integration` (4), `Salesforce.Tests.Integration` (3), and `Playwright.Tests.Browser` (4) are never executed.
  3. **Zero real database/container or fault-injection tests for data modules (`Postgres`, `ClickHouse`, `Qdrant`, `Supabase`), and no Aspire conformance tier in `digitalbrain`:**
     - Neither `digitalbrain`, `modules`, nor `os` uses `Testcontainers`. Only Azurite is started in CI (`digitalbrain` and `os`). `Postgres.Tests.Integration` only runs SQL-formatting/refusal unit checks or skips unless `INTOCHATOS_POSTGRES_TEST_CONNECTION` is manually set. And in `IntoChat.OS.Kernel.Tests.E2E.Aspire`, `OsAppHostFixture` explicitly disables **9 modules**: `csharp`, `artcraft`, `playwright`, `postgres`, `qdrant`, `clickhouse`, `supabase`, `azure`, and `roslyn`!
     - Furthermore, `OsHostConformanceFacts` in `IntoChat.OS.Kernel.Tests.E2E.Aspire` skips 2 of the 14 `HostConformanceFacts` (`Gap_a_stale_cursor...` and `KillBetweenWriteAndPublish...`) with `Assert.Skip("Pre-existing DigitalBrain master failure ... deferred to 0.13 cleanup")`.
  4. **Six concrete causes of slowness and flakiness under load in `os`:**
     - **TOCTOU ephemeral port race in `LocalSiloHostFactory.FreePort()` ([LocalSiloHostFactory.cs:88-93](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Orleans/Testing/LocalSiloHostFactory.cs#L88-L93)):** Binds port `0` and immediately closes the `TcpListener` before Orleans starts. (In commit `ff67bfeb4` on Oct 21, `PortReservation.cs` fixed this with cross-process lock files, but `f014a83a6` on Oct 29 deleted `IntoChat.OS.Testing` and reverted to the racy `FreePort()`!).
     - **~30+ full in-process Orleans silos booted per test run** (many per `[Fact]` instead of `IClassFixture`, plus `BootFacts.AFailedStartLeavesNoProcessBehind` booting a second full Aspire `OsAppHostFixture`).
     - **Linked `.Tests.Unit` files re-compiled into `.Tests.Integration` with `Kinds = [TestOS, Silo]`** (`TimeNeuronFacts`, `ControlNeuronFacts`, `AiNeuronFacts`), re-running `TestOS` cases that `.Tests.Unit` already ran, and verifying the 11 shipped apps' BDD/view fixtures **3 separate times** (`IntoChat.OS.Apps.Tests.Unit`, `IntoChat.OS.Kernel.Tests.E2E`, and `IntoChat.OS.Kernel.Tests.E2E.Aspire`).
     - **ThreadPool starvation under parallel module execution:** `EdgeGrantsCacheAdversarialFacts` spawned 80 blocking `Barrier.SignalAndWait` workers, breaking `--max-parallel-test-modules 8` in commit `9bb955839` (reverted 23 minutes later in `6e2a8762f`).
     - **`[ModuleInitializer]` ordering race on `ModuleConfigurationOptions.Current`** (`OsModule.InitializeConfigurationRoot()` setting `Root = "IntoChatOS"`, fixed via workaround in `c2e0a9aca`).
     - **`RefreshCodeGraph` MSBuild target in `Directory.Build.targets`:** When `CI != 'true'` and `-p:CodeGraphRefresh=false` is omitted, every `.csproj` in the 88-project solution spawns `npx -y @colbymchenry/codegraph@1.6.0 sync`, causing massive SQLite/stamp-file contention on Windows.

---

### Rank 7: Five Deployment Redesigns Leaving a 3-Way Split (`deploy.sh` + `AzureInstaller`/`ArmAzure` + `IntoChat.OS.Aspire.Hosting`)
- **What it is:** The git history shows **five deployment redesigns in four months**:
  1. **Era 1 (2026-06-23, `bed5cedcc`):** Vendored 33,000-LOC `DeploymentKit` Pulumi library + `deploy/DigitalBrain.Deploy` (17 Azure resources).
  2. **Era 2 (2026-06-23, `cf283057d`):** Replaced `DeploymentKit` with a minimal 237-line `Pulumi.AzureNative` program (`Elon Algorithm Pass 1`).
  3. **Era 3 (2026-09-27, `97f2b5707`):** Introduced `DigitalBrain.Deployment` reading Aspire's `hosted-manifest.json` (`AspireManifest.cs`) + `DeploymentKit` + per-module `[ModuleDeployment]` assemblies (`Memory/Qdrant`, `ClickHouse`, `Compute/Postgres`, `AI/Ollama`, `CSharp` dynamic session pool).
  4. **Era 4 (2026-10-18, `9a6a674ec`, `9accaa496`):** Extracted packable `DigitalBrain.Deployment.Runtime` (`RuntimeDeployment.DeployRuntime`) using `Pulumi.AzureNative` directly without `DeploymentKit`, depending only on `DigitalBrain.Contracts.Configuration`.
  5. **Era 5 (2026-10-18, retired in `0055b8adb`; `c13961faf`, `353caf674`, `fd3ec7eaa`, `14a523499`):** Retired Pulumi, `DigitalBrain.Deployment`, and Aspire manifest deployments (-1,665 LOC); replaced them with `AzureInstaller` + `InstallPlan` + `ManifestEnvironment` in `IntoChat.OS.Kernel/Install/*` (733 LOC) and `ArmAzure` in `IntoChat.OS.Modules.Microsoft.Azure` (604 LOC), wrapped by `scripts/deploy/deploy.sh` + 4 Python/bash helper scripts (549 LOC) and `.deployment-values` (`intochat/os-deployment`).
- **What it costs today:**
  - While retiring Pulumi (`0055b8adb`) rightly removed external state backends and 33k LOC of `DeploymentKit`, the current stack splits deployment logic across **three places**:
    1. `scripts/deploy/{deploy.sh, release_manifest.py, deployment_gate.py, deploy_seeding_check.py, smoke.sh}` (549 LOC of bash + Python) handling ACR login, image building, OIDC token cache surgery (`warm_azure_tokens`), secret JSON merging, Log Analytics KQL polling, and `az acr import`.
    2. `IntoChat.OS.Kernel/Install/*` (733 LOC) + `IntoChat.OS.Modules.Microsoft.Azure` (`ArmAzure.cs`, 1,006 LOC) handling idempotent Azure ARM PUT calls and Key Vault secret writing.
    3. `IntoChat.OS.Aspire.Hosting` (51 files, 3,444 LOC) handling local/test topology orchestration with a completely separate set of per-module hosting classes (`PostgresModuleHosting`, `QdrantModuleHosting`, etc.).
  - Worse, `deploy.yml` runs `IntoChat.OS.Kernel install` **twice on every test deploy** (`--verify-repeatability`), adding ~5–7 minutes of serial Azure ARM polling and Container App revision checks to every run.
- **Does that reason still hold? Partially:** Native C# `AzureInstaller` + `ArmAzure` is simpler and more deterministic than Pulumi (`97f2b5707` / `9a6a674ec`) or `azd` Bicep generation, **provided** (a) `deploy.sh` stops building images and stops hacking Azure CLI token caches, (b) `--verify-repeatability` is tested in fast `InMemoryCloud` unit tests (`InstallPlan` idempotency is already proven in `IntoChat.OS.Kernel.Tests.Unit`!) rather than running ARM deployments twice against live Azure on every release, and (c) `InstallPlan` and `IntoChat.OS.Aspire.Hosting` both read the same declarative `ModuleManifest.Needs` instead of maintaining parallel per-module classes.

---

## 3. The Proposal (One Target Architecture)

### 3.1 Area 1: Pipelines — Two Workflows (`ci.yml` + `release.yml`), Build Once, Release-to-Prod in ~7.5 Minutes

#### Ruling
Collapse the 15 workflows across `digitalbrain`, `modules`, and `os` into **two workflows per repository (`ci.yml` and `release.yml`)** — or **four workflow files total** once `modules` is folded into `os` (Area 2). Eliminate `deploy.yml`, `e2e.yml`, `flutter.yml`, `bump.yml`, and `gitleaks.yml` as standalone workflows:

1. **In `os` (which includes `modules` and `ui`):**
   - **`ci.yml` (runs on `push` to `master` and `pull_request`, target wall-clock: ~4m 15s):**
     - **Job 1 — `build` (~1m 45s):** Runs `gitleaks` (6s), restores with `actions/cache` on `~/.nuget/packages` keyed by `**/Directory.Packages.props`, runs `dotnet build Intochat.OS.slnx -c Release --no-restore -p:CodeGraphRefresh=false` (with analyzers and `dotnet format --verify-no-changes --no-restore`), and uploads the `artifacts/bin/Release` directory via `actions/upload-artifact@v4`.
     - **Job 2 — `test-unit` (`needs: [build]`, ~1m 30s):** Downloads `artifacts/bin/Release` and runs all `.Tests.Unit` projects with `dotnet test --no-build --max-parallel-test-modules 4` (after fixing the 2 ThreadPool/port-race defects in Area 5).
     - **Job 3 — `test-integration` (`needs: [build]`, ~2m 00s):** Downloads `artifacts/bin/Release`, starts Azurite + Postgres 16 + Qdrant GitHub Actions `services:` containers (zero Docker startup wait overhead during tests!), and runs all `.Tests.Integration` projects (including the 6 `modules` integration suites currently skipped in CI) with `--no-build`.
     - **Job 4 — `e2e` (`needs: [build]`, ~2m 15s):** Downloads `artifacts/bin/Release` and runs `IntoChat.OS.Kernel.Tests.E2E` + `IntoChat.OS.Kernel.Tests.E2E.Aspire` (`--no-build`, without the broken `--filter-trait "needs=artcraft"` flag).
     - **Job 5 — `flutter` (parallel with `build`, path-filtered or cached pub/sdk via `subosito/flutter-action@v2` with `cache: true`, ~2m 15s):** Runs `flutter analyze`, `flutter test` (`core`, `ui`, `shell`), `flutter build web --release`, and uploads `ui/shell/build/web` artifact.
     - **Job 6 — `ci` gate (`needs: [build, test-unit, test-integration, e2e, flutter]`, 3s):** Single branch-protection status check.
   - **`release.yml` (the single CD workflow, runs on `release: types: [published]` and `workflow_dispatch`, target wall-clock: ~7m 30s total from release to prod):**
     - **No CI/E2E re-run:** Instead of re-running `ci.yml` and `e2e.yml` via `workflow_call` (saving **6.7 minutes** immediately), `package` verifies in **2 seconds** via `gh api repos/.../commits/$GITHUB_SHA/check-runs` that the `ci` gate check-run passed on `$GITHUB_SHA` (the exact check `digitalbrain/release.yml` already uses!).
     - **Job 1 — `package` (~2m 30s):**
       - Restores from cached NuGet packages, builds `os` (`dotnet publish IntoChat.OS.Kernel.csproj -c Release -r linux-x64 /t:PublishContainer -p:PublishProfile=Container`), `os-ui` (`docker build` with `type=gha` BuildKit layer caching), and `os-sandbox` (`docker build` with `type=gha` BuildKit layer caching + `check-sandbox-image.sh`, running in parallel with `os` and `os-ui`).
       - Pushes images **once** to `acrintochattest.azurecr.io/{os,os-ui,os-sandbox}:<vX.Y.Z>` (and optionally mirrors the exact manifest to `ghcr.io/intochat/{os,os-ui,os-sandbox}:<vX.Y.Z>` via `crane copy` in 3 seconds without rebuilding).
       - Resolves immutable `@sha256:` digests, writes `release-manifest.json`, uploads it as workflow artifact + GitHub Release asset, and uploads the pre-built `IntoChat.OS.Kernel` linux-x64 binary artifact.
     - **Job 2 — `deploy-test` (`needs: [package]`, `environment: test`, ~2m 45s):**
       - Downloads the pre-built `IntoChat.OS.Kernel` binary and `release-manifest.json`, reads repo-tracked `os/deploy/test.json` + `OS_DEPLOY_SECRETS_JSON`, logs into Azure via `azure/login@v2` (since the job takes <3 minutes and builds zero images, the 5-minute OIDC token **never expires** and `warm_azure_tokens()` is deleted!), runs `./IntoChat.OS.Kernel install --env test --config merged.json` **once** (passing the `@sha256:` digests from `release-manifest.json`), runs `deploy_seeding_check.py` + `smoke.sh`.
     - **Job 3 — `deploy-prod` (`needs: [deploy-test]`, `environment: production`, ~2m 15s):**
       - Gated solely by **GitHub Environment protection rules** on `environment: production` (required reviewers / wait timer / branch policy configured in GitHub Settings — zero polling loops in YAML).
       - Imports the exact `@sha256:` digests from `acrintochattest` into `acrintochatprod` via `az acr import`, runs `./IntoChat.OS.Kernel install --env prod --config merged.json` with those `@sha256:` digests, and runs `smoke.sh`.
2. **In `digitalbrain`:**
   - **`ci.yml` (~2m 30s):** `build` (with `gitleaks`, `format`, `no-orleans`, uploads build artifact) → parallel `facts`, `runtime` (with Azurite service container), `conformance-aspire` (Area 5), `template` (`--no-build`) → `ci` gate. (Deletes the unreachable `template-feed` job and stops publishing preview packages on every push unless explicitly requested).
   - **`release.yml` (~1m 15s):** Checks `ci` check-run on `$GITHUB_SHA`, packs the `DigitalBrain.*` packages, pushes to `nuget.org` (`NUGET_API_KEY`), attaches `.nupkg`/`.snupkg` to the GitHub Release.

#### Expected Release-to-Prod Timeline (Before vs. After)
| Phase | Today (`release.yml` + `deploy.yml` in parallel) | Target (`release.yml` single run) | Time Saved |
|---|---:|---:|---:|
| Verify CI / Re-run CI + E2E | `400s` in `release.yml` + `0–300s` gate poll in `deploy.yml` | **`2s`** (`gh api` check-run assertion on `$GITHUB_SHA`) | **-6m 38s** |
| Build & Push Container Images | Built **twice**: `706s` in `release.yml` (GHCR) + `~900s` in `deploy.sh` (ACR) | **`150s`** (`2m 30s` — built **once** in parallel with GHA BuildKit layer cache) | **-12m 30s** |
| Provision & Deploy `test` + Smoke | `~1,010s` (`16m 50s` — runs `install` **twice** for `--verify-repeatability`) | **`165s`** (`2m 45s` — runs pre-built `Kernel install` **once** by `@sha256:` digest) | **-14m 05s** |
| Promote Digest & Deploy `prod` | `~135s` (`2m 15s` — currently failing on OIDC subscription) | **`135s`** (`2m 15s` — `az acr import` by `@sha256:` + `Kernel install` + `smoke.sh`) | — |
| **Total Wall-Clock Time** | **32m 30s (`1,950s` median)** + 18m 22s duplicate run | **7m 32s (`452s`)** | **-25m 00s (-77%)** |

#### Alternatives Rejected & Cost If Wrong
- **Rejected Alternative A:** Keep `release.yml` and `deploy.yml` separate and have `deploy.yml` trigger on `workflow_run: workflows: [release]`. *Why rejected:* Splits one release across two workflow runs in the GitHub Actions UI, complicates manual `workflow_dispatch` rollbacks, and forces artifact handoffs across workflow boundaries.
- **Rejected Alternative B:** Keep `--verify-repeatability` (running `Kernel install` twice on every live Azure `test` deployment). *Why rejected:* Costs ~6 minutes of live Azure ARM polling on every release to test deterministic C# plan construction (`InstallPlan.ForFoundation`), which is already tested in <1 second by `InMemoryCloud` in `IntoChat.OS.Kernel.Tests.Unit`. *Cost if wrong:* If an Azure ARM PUT call is non-idempotent only against real Azure APIs, a single-pass `test` deploy catches it on the *next* release rather than the first release; if desired, `--verify-repeatability` can remain an opt-in `workflow_dispatch` checkbox.

---

### 3.2 Area 2: Packages, Smart Module Templates for Contributors & Ino, and Co-Located Documentation

#### Ruling
**Use a two-repository topology (`digitalbrain` + `os`), folding `intochat/modules` back into `intochat/os` under `src/Modules/` (enforced by MSBuild & architecture tests), publish `DigitalBrain.*` to `nuget.org` only, upgrade `DigitalBrain.Templates` (`dotnet new intochat-module`) so both human contributors and Ino can scaffold modules in one command, and co-locate documentation inside `digitalbrain/docs/` and `os/docs/` with automated manifest-to-doc verification.**

Here is how the pieces fit together:
1. **Why `digitalbrain` stays a separate repository:** `digitalbrain` is the open-source framework published to **`nuget.org`** (which is world-readable and requires **zero credentials** on `dotnet restore`).
2. **Why `modules` merges into `os` (`src/Modules/*`):**
   - `intochat/modules` has **only one consumer (`intochat/os`)**, is versioned in 1:1 lockstep (`0.14.2`), and half of the modules (`AI`, `Flutter`, `Flame`, `Circles`, `Aspire`, `Identity`, `CSharp`, `Cloud.*`) already live in `os` anyway.
   - Inside `os/src/Modules/`, a single 15-line `os/src/Modules/Directory.Build.props` + the existing 6-second [ModuleBoundaryFacts.cs](file:///E:/intochat/modules/src/IntoChat.Modules.Architecture.Tests.Unit/ModuleBoundaryFacts.cs) enforces the exact same rule as a separate repository: **projects under `src/Modules/` may only reference `DigitalBrain.*` packages and other `*.Contracts` projects — never `IntoChat.OS.Kernel`**.
3. **Smart Module Template & Dev-Workflow Generator for Contributors and Ino (`DigitalBrain.Templates`):**
   - Today, [DigitalBrain.Templates](file:///E:/intochat/digitalbrain/templates/DigitalBrain.Templates/content/intochat-module/.template.config/template.json) (`dotnet new intochat-module`) scaffolds a standalone repository with its own `Directory.Build.props`, `Directory.Packages.props`, `global.json`, and `nuget.config`, and only creates a `.Tests.Unit` project.
   - **Upgrade `dotnet new intochat-module` with two modes (`--in-tree` default when inside `os/src/Modules/`, plus `--standalone` for external third-party repos):**
     - Scaffolds `src/Modules/<Category>/<Name>/`:
       - `src/IntoChat.OS.Modules.<Name>.Contracts/` (`I<Name>Neuron.cs`, signals, DTOs)
       - `src/IntoChat.OS.Modules.<Name>/` (`<Name>Module.cs` with complete `ModuleManifest`: `Provides`, `Requires`, `Needs`, `Settings`, `Secrets`, `Connectors`, `Actions`)
       - `tests/IntoChat.OS.Modules.<Name>.Tests.Unit/` (`<Name>ModuleFacts.cs` inheriting `ModuleOnTestOSFacts<<Name>Module>`)
       - `tests/IntoChat.OS.Modules.<Name>.Tests.Integration/` (`<Name>SiloFacts.cs` running against `SiloHost.Local()`)
       - `README.md` (structured module documentation page)
     - **Zero-touch registration:** Because [OperatingSystemModules.cs](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/OperatingSystemModules.cs#L15-L34) discovers every `[DigitalBrain.Module]` via `DependencyContext.Default`, adding the module project to `os/src/Modules/` automatically wires it into the OS Kernel, Aspire (`ManifestHostingProjector`), and Azure (`ManifestEnvironment`) with zero manual registration code.
     - **Golden Reference Module in Tree (`src/Modules/Template/`):** Keep a small, canonical `TemplateModule` (or `EchoModule`) in `os/src/Modules/Template/` that exercises `Settings`, `Secrets`, `Connectors`, `[Synapse]`, and `ModuleOnTestOSFacts` as a living reference for contributors.
     - **Ino / Creator Dev-Workflow Integration (`IModuleScaffolder` neuron in `RoslynModule`):** Expose `dotnet new intochat-module` as a local developer tool neuron so that in local dev workflow you can tell Ino: *"Create a new Linear module with an OAuth connector and webhook"* — and Ino scaffolds the module from `DigitalBrain.Templates`, fills in the contracts and `ModuleManifest`, and runs `dotnet test` on the new module's `ModuleOnTestOSFacts` and `Architecture.Tests.Unit` immediately.
4. **Maintaining Documentation Across Repositories (`digitalbrain/docs/` + `os/docs/` + CI Drift Guard):**
   - Today documentation is scattered across 4 locations: `digitalbrain`, `os`, `E:/intochat/os.docs` (26 VitePress files), and `E:/intochat/intochat.docs` (69 Astro/Starlight files deployed to `docs.intochat.com` on Cloudflare Pages via [intochat.docs/.github/workflows/deploy.yml](file:///E:/intochat/intochat.docs/.github/workflows/deploy.yml)).
   - **Co-locate docs with the code they describe (so PRs update code + docs in the same commit):**
     1. **`digitalbrain/docs/`:** Owns framework documentation — Neurons, Synapses (`[Synapse]`, `StateNeuron<T>`, `[Slot]`, `[Every]`), Capabilities, Policies (`PoliciesNeuron`), Remote Client (`DigitalBrain.Client`), Testing Tiers (`TestOS`, `SiloHost`, Aspire Conformance), Analyzer reference (`DB0001`–`DB0044`), and the `intochat-module` template guide.
     2. **`os/docs/`:** Absorbs `os.docs` (`architecture.md`, `concepts.md`, `modules.md`, `creator-self-improving-loop.md`, `sandbox-and-live-behaviors.md`, `contributing.md`), the Astro/Starlight site from `intochat.docs` (with a path-filtered `docs` job in `os/.github/workflows/ci.yml` that deploys to Cloudflare Pages on `master`), and `docs/assessment/`. Separate `os.docs` and `intochat.docs` repos can then be retired.
     3. **Automated Doc-Drift Verification (`ModuleDocumentationFacts` in `Architecture.Tests.Unit`):** Every module in `os/src/Modules/<Name>/README.md` has a generated/verified Markdown table of its `ModuleManifest` (`Settings`, `Secrets`, `Connectors`, `Needs`, `Actions`). A 2-second test in `Architecture.Tests.Unit` reflects over `OperatingSystemModules.Modules` and fails CI if a module is missing its `README.md` or if its documented settings/secrets/connectors drift from `ModuleManifest`.
5. **Killing `PACKAGE_TOKEN`, GitHub Packages, `<packageSourceMapping>` friction, and `bump.yml` failures:**
   - Stop pushing `DigitalBrain.*`, `IntoChat.OS.Modules.*`, and `IntoChat.OS.*` to `https://nuget.pkg.github.com/intochat/index.json`.
   - `os` stops packing 40 NuGet packages altogether (saving `157s` per CI run).
   - `os/nuget.config` becomes identical to `digitalbrain/nuget.config`: a single `<add key="nuget.org" value="https://api.nuget.org/v3/index.json" />` source (plus an optional repo-tracked `.local-feed` folder).
   - **Result:** `PACKAGE_TOKEN` is deleted from all repositories (and from `os-sandbox/Dockerfile`!). Dependabot PRs work out of the box. Cross-repo `403 Forbidden` package-ownership errors disappear forever.
6. **Fixing `bump-pins.cs` and `Directory.Build.targets`:**
   - Guard the analyzer injection in `os/Directory.Build.targets` so file-based scripts (`dotnet run scripts/*.cs`) and non-packable utility scripts never pull `DigitalBrain.Analyzers`:
     ```xml
     <ItemGroup Condition="'$(UsingMicrosoftNetSdk)' == 'true' and '$(IsTestProject)' != 'true' and '$(MSBuildProjectExtension)' == '.csproj'">
       <PackageReference Include="DigitalBrain.Analyzers" PrivateAssets="all" IncludeAssets="runtime;build;native;contentfiles;analyzers;buildtransitive" />
     </ItemGroup>
     ```
7. **Zero-Config Local Development (No Scratch `nuget.config` or Untracked Files):**
   - Check in a repo-tracked `Directory.Build.props` condition in `os`: when sibling `../digitalbrain/src/DigitalBrain/DigitalBrain.csproj` exists and `USE_LOCAL_DIGITALBRAIN=1` (or `<UseLocalDigitalBrain>true</UseLocalDigitalBrain>`), MSBuild substitutes `<ProjectReference>` to `../digitalbrain/src/...` automatically — or `digitalbrain/scripts/pack-local.cs` outputs directly to `../os/.local-feed` (tracked with `.gitkeep` and mapped in `os/nuget.config`). Zero untracked files, zero scratch `nuget.config`.

#### Alternatives Rejected & Cost If Wrong
- **Rejected Alternative A: Keep all 3 repositories (`digitalbrain`, `modules`, `os`).** *Why rejected:* Preserves the two-hop release cascade (`digitalbrain` → `modules` → `os`), requires publishing and restoring 28 `IntoChat.OS.Modules.*` NuGet packages on every module change, and forces cross-repo coordination for a solo developer.
- **Rejected Alternative B: Merge all 3 into a single monorepo (`digitalbrain` + `modules` + `os`).** *Why rejected:* Weakens the hard open-source/public NuGet boundary of `digitalbrain` and makes it too easy for framework changes to rely on OS E2E tests instead of `digitalbrain`'s own conformance suite.

---

### 3.3 Area 3: Module Configuration — One Declarative `ModuleManifest` Shape Across Local, Tests, Aspire, and Azure

#### Ruling
**Make `ModuleManifest` the single source of truth for everything a module needs (`Settings`, `Secrets`, `Connectors`, and `Needs`), and retire `IntegrationSchema`, `IntegrationSeeder`, `IntegrationNeuron`, `IIntegrationStartup`, `IModuleIntegrations<T>`, and the 16 bespoke `ModuleHosting<T>` classes.**

#### 1. The Unified Declarative Shape in `ModuleManifest`
Every module declares at most four configuration facets inside `public static ModuleManifest Manifest`:
```csharp
public sealed record ModuleManifest(
    IReadOnlyList<CapabilityId> Provides,
    IReadOnlyList<CapabilityId> Requires,
    IReadOnlyList<Need> Needs,
    IReadOnlyList<Setting> Settings,
    IReadOnlyList<Secret> Secrets,
    IReadOnlyList<ConnectorDefinition> Connectors,
    IReadOnlyList<PrivilegedGrant> Privileged = default!,
    IReadOnlyList<ActionDescriptor> Actions = default!);
```
- **`Settings` (`Setting.Of<TOptions>()`):** Non-secret tuning parameters (`Isolation`, timeouts, endpoints), always bound from `<Root>:Modules:<id>:Options:<Prop>` to `IOptions<TOptions>` with eager `ValidateOnStart()`.
- **`Secrets` (`Secret.Named(name, Required: bool)`):** Deployment-owned credentials (API keys, OAuth client secrets, database connection strings, signing keys), always bound from `<Root>:Modules:<id>:Secrets:<Name>` and resolved through `secrets@1` (`IVaultPort` / `IModuleSecrets<TModule>`). **This replaces `IntegrationSchema` completely.**
- **`Connectors` (`ConnectorDefinition`):** User/brain-owned connection definitions (`ConnectionNeuron<TState>`), referencing any required deployment OAuth secrets from `Secrets` (`RequiredSecrets = ["ClientId", "ClientSecret"]`).
- **`Needs` (`Need.*`):** Declarative backing resources required by the module:
  - Existing: `Need.State(name)`, `Need.Streams`, `Need.Reminders`, `Need.Secrets`, `Need.Http`, `Need.HttpClient(name, ...)`, `Need.Webhook(path, neuron)`
  - **New typed infrastructure needs (replacing custom `ModuleHosting<T>` and `InstallPlan` special cases):**
    - `Need.Postgres(string secretName = "ConnectionString", string? adminSecretName = null, string defaultDatabase = "intochat")`
    - `Need.VectorStore(string name = "qdrant", string secretName = "ConnectionString", bool optionalInMemoryFallback = true)`
    - `Need.Container(string name, string image, int port, string endpointSetting)`

#### 2. How Each Environment Supplies the Manifest Uniformly
All four environments feed the exact same configuration grammar (`IntoChatOS:Modules:<id>:Options:<Prop>` and `IntoChatOS:Modules:<id>:Secrets:<Name>`):

| Environment | How `Settings` & `Secrets` Are Supplied | How `Needs` (`Need.Postgres`, `Need.Container`, etc.) Are Provisioned |
|---|---|---|
| **1. Unit / Integration Tests (`TestOS` & `SiloHost.Local`)** | Passed via `testHost.WithModule<PostgresModule>(options => ..., secrets: new() { ["ConnectionString"] = connStr })` which populates in-memory `IConfiguration`. | `TestOS` uses in-memory fakes; `.Tests.Integration` uses a shared xUnit assembly fixture (`PostgresContainerFixture`) that starts a `postgres:16-alpine` container (or connects to the CI `services:` Postgres on `localhost:5432`) and passes its connection string into `Secrets["ConnectionString"]`. |
| **2. Local Runs (`intochat-os run`)** | Read from `~/.intochat/config.json` (`Modules:<id>:Options` / `Secrets`) or environment variables (`IntoChatOS__Modules__<id>__...`). | If `Need.Postgres` is declared and `Secrets:ConnectionString` is not set locally, `AspireCloud` (`--local`) starts a local Docker container (`postgres:16`) or disables the module cleanly with a clear log message instead of crashing the silo. |
| **3. Aspire (`IntoChat.OS.AppHost`)** | A single generic `ManifestHostingProjector` iterates `OperatingSystemModules.Modules`, binds `Manifest.Settings`, and creates secret Aspire parameters for `Manifest.Secrets`. | `ManifestHostingProjector` switches on `Manifest.Needs`: for `Need.Postgres`, it calls `builder.AddPostgres("{id}-pg").AddDatabase(need.DefaultDatabase)` and wires its `ConnectionStringExpression` directly into `IntoChatOS__Modules__{id}__Secrets__{need.SecretName}`. **All 16 bespoke `IntoChat.OS.Aspire.Hosting/Modules/*` files (1,450 LOC) are deleted.** |
| **4. Azure (`IntoChat.OS.Kernel install` + `os/deploy/<env>.json`)** | `os/deploy/<env>.json` + `OS_DEPLOY_SECRETS_JSON` have one uniform shape (`"modules": { "<id>": { "options": {...}, "secrets": {...} } }`). `ManifestEnvironment` maps every `Manifest.Secrets` entry to Key Vault secret `{id}-{secret-kebab}` and ACA env var `IntoChatOS__Modules__{id}__Secrets__{Name}`. | `InstallPlan` switches on `Manifest.Needs`: if `postgres` is enabled and `Need.Postgres` is present without an external `ConnectionString` secret, it provisions `{prefix}-{env}-pg` Flexible Server and writes the connection string to Key Vault secret **`postgres-connectionstring`** (fixing the `db-connection` vs `postgres-connectionstring` mismatch!). |

#### 3. Concrete Example: `PostgresModule` Under the Target Design & Database Failure Handling
```csharp
[DigitalBrain.Module("postgres")]
public sealed class PostgresModule : DigitalBrain.Module
{
    public static ModuleManifest Manifest { get; } = new(
        Provides: [],
        Requires: [Capability.Connections.V1, Capability.Secrets.V1],
        Needs: [Need.Postgres(secretName: "ConnectionString", adminSecretName: "AdminConnection", defaultDatabase: "postgres")],
        Settings: [Setting.Of<PostgresModuleOptions>("IntoChatOS:Modules:postgres:Options")],
        Secrets: [Secret.Named("ConnectionString", Required: true), Secret.Named("AdminConnection", Required: false)],
        Connectors: [new("postgres", "Postgres", ["connection-string"], [])
        {
            Neurons = [typeof(IPostgresServer), typeof(ITable)],
            Signals = [typeof(TableDefined), typeof(RowUpserted), typeof(RowDeleted)]
        }]);
}
```
- **What happens when the database is unconfigured or unavailable at startup (Fixed):**
  1. `PostgresSourceRegistry` is **not** an `IIntegrationStartup` task that runs unguarded inside `IntegrationSeeder` and crashes the silo. Instead, `NpgsqlDataSource` is constructed **lazily on first use** (`Lazy<ValueTask<NpgsqlDataSource>>`) using `IModuleSecrets<PostgresModule>.Get("ConnectionString")`.
  2. If the `postgres` module is enabled in configuration without a `ConnectionString` secret, `ManifestEnvironment` / `ModuleSettingsValidation` fails fast at **host build time** with a clear validation message (`"Module 'postgres' is enabled and requires secret 'IntoChatOS:Modules:postgres:Secrets:ConnectionString'"`), rather than throwing an unhandled `PostgresUnavailableException` halfway through Orleans grain startup.
  3. If the `ConnectionString` is configured but the PostgreSQL server is unreachable at startup, the silo boots cleanly, `PostgresHealthCheck` reports `Degraded` (so the whole OS silo is not killed by ACA liveness/readiness probes just because a module's backing database is restarting!), and calls to `PostgresModule` fail fast with `PostgresUnavailableException` (HTTP 503).
- **What happens when the database dies mid-call (Fixed):**
  1. Unify error classification in a single `PostgresExceptionClassifier.Classify(Exception ex)` helper shared by both `PostgresProvider` and `TableStore`:
     - Any `PostgresException` with SQLSTATE `08*` (connection failure), `57P0*` (admin shutdown / crash recovery), `53300` (too many connections), `28*` (auth failure), or `3D000` (database does not exist), as well as any non-`PostgresException` `NpgsqlException` (socket reset, EOF, connect timeout), throws **`PostgresUnavailableException`** (which derives from `ServiceUnavailableException` → HTTP `503` + retryable signal).
     - Only user/schema SQL errors (syntax `42*`, constraint `23*`, data `22*`) and query timeouts throw **`PostgresQueryException`** (HTTP `409`).
  2. When `PostgresUnavailableException` occurs on a pooled connection, call `NpgsqlDataSource.Clear()` (or let Npgsql's `KeepAlive` + connection-reset handling evict broken sockets) so that as soon as PostgreSQL recovers, the very next grain call succeeds without restarting the silo.

#### Alternatives Rejected & Cost If Wrong
- **Rejected Alternative:** Keep `IntegrationSchema` + `IntegrationNeuron` for deployment credentials so admins can edit deployment-level OAuth client secrets at runtime in the UI without redeploying. *Why rejected:* In practice, `IntegrationSeeder` overwrites `IntegrationNeuron` from `IConfiguration` (`IntoChatOS:Integrations:*`) on every silo startup anyway, and user-editable connections are already handled by `ConnectionNeuron<TState>`. Having two parallel deployment-secret systems (`IntegrationSchema` vs `ModuleManifest.Secrets`) costs 600+ lines of authority seeding plumbing and caused the `PostgresSourceRegistry` startup crash and Key Vault secret name mismatch. *Cost if wrong:* Rotating a deployment-wide OAuth client secret requires updating Key Vault / environment config rather than calling a grain method.

---

### 3.4 Area 4: OS Composition — A 3-Project Thin Host (~1,500 LOC), an `OsModule`, Promoting `DigitalBrain.Client`, and Targeted Deletions

#### Ruling
**Confirm the owner's hypothesis while preserving and elevating sandboxed code execution:**
1. The OS becomes a thin host (`IntoChat.OS.Kernel`, `IntoChat.OS.AppHost`, and `os/ui`) composing `DigitalBrain.Runtime.Orleans` + `DigitalBrain.Runtime.Http` + `DigitalBrain.Runtime.Synapses` with a product list of modules, one of which is `IntoChat.OS.Modules.Os` (`OsModule`).
2. **Promote `IntoChat.OS.Client` + `RuntimeEdge` into `digitalbrain` (`DigitalBrain.Client` + `DigitalBrain.Runtime.Http`), and KEEP `CSharpModule` + `CSharp.Sandbox` (`os-sandbox`) + `CreatorApp`:**
   - Why: `[Synapse]` runs in-process inside the silo (`SynapseLoadContext`) under strict Roslyn analyzer restrictions (`DB0041`–`DB0044`: no file I/O, no network, no threads, no arbitrary libraries). `PoliciesNeuron` evaluates declarative rules only. Neither replaces **out-of-process sandboxed C# execution (`os-sandbox`)** connecting back to the brain via [IntoChatOSConnection.cs](file:///E:/intochat/os/src/IntoChat.OS.Client/IntoChat.OS.Client/IntoChatOSConnection.cs) and [RuntimeEdge.cs](file:///E:/intochat/os/src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp/Edge/RuntimeEdge.cs), where every capability call is authenticated by an HMAC `SessionToken`, checked against `CallFilter` permissions (`MissingGrant`, `MissingAllowance`, `LimitReached`, `OutsideWorkspace`), and executed inside the silo so vault secrets (`VaultNeuron` / `ConnectionNeuron<TState>`) never enter the sandbox container!
   - Promoting `IntoChat.OS.Client` (`DigitalBrain.Client`) and `RuntimeEdge` (`/brains/{brainId}/runtime/v1/{invoke,report,stream}`) to `digitalbrain` makes remote/sandbox client connections a first-class framework primitive tested directly in `digitalbrain`'s Aspire conformance suite.
3. **Delete only the true redundancies (~4,500 LOC):** `ReactionWriter` (892 LOC, superseded by `PolicyWriter`), `IntoChat.OS.Modules.Apps/Shell/*` (2,188 LOC of uncompiled duplicate scripts for `Desktop`, `Settings`, `Workspace`), and legacy `DigitalBrain.Runtime.Apps` / `Apps.Packaging` / `Cli` shims (~1,318 LOC).

#### Concrete Movement & Deletion Map (`75,303 LOC` → `~2,100 LOC` in the OS Host)

| Action | Exact Projects / Subsystems | Lines (Non-Empty) | Destination |
|---|---|---:|---|
| **1. DELETE (Redundant FastPath & Duplicate Scripts Only)** | • `ReactionWriter` & `ReactionExamples` in `IntoChat.OS.Modules.Apps/Apps/FastPath/*` (892 LOC — superseded by `PolicyWriter` / `PoliciesNeuron`)<br>• Uncompiled duplicate script strings in `IntoChat.OS.Modules.Apps/Shell/*` (`Desktop`, `Settings`, `Workspace` scripts — 2,188 LOC)<br>• `IntoChat.OS.Cli` (`app build/deploy/watch`, 750 LOC) + `DigitalBrain.Runtime.Apps` & `DigitalBrain.Apps.Packaging` (568 LOC)<br>• `IntoChat.OS` core duplicates (`ILogic`, `INeuronState`, `IRuntimeBuilder`, `NeuronAliases`, `NeuronKeys`, `NeuronProxies`, 94 LOC), `IntoChat.OS.Contracts.Compute` (0 LOC), `IntoChat.OS.Templates` (72 LOC), `IntoChat.OS.Framework` duplicates (`NeutralCallMapping`, `BrainAffinePlacement`, `SignalJournal`, 235 LOC) | **-4,231 LOC in `os`**<br>**-568 LOC in `digitalbrain`** | **Deleted permanently** |
| **2. MOVE to `digitalbrain` (Elevate Client, RuntimeEdge & Primitives)** | • **Permission-gated remote/sandbox client & edge:** `IntoChat.OS.Client` (`IntoChatOSConnection`, `SessionConnection`, `NeuronProxy`) + `RuntimeEdge` (`/brains/{id}/runtime/v1/{invoke,report,stream}`, `SessionTokens`) (~1,580 LOC) → `DigitalBrain.Client` & `DigitalBrain.Runtime.Http`<br>• Plain-neuron access filter & directory: `BrainNeuron`, `PlainNeuronCallFilter`, `CallFilter`/`ICallFilterStage`, `PlainInstanceRegistration`, `InstanceDirectoryGrain`, `TypeTableGrain`, `KernelInvocationSink`, `RuntimeCallerMirror` (~770 LOC) → `DigitalBrain.Runtime.Orleans`<br>• Confirmations: `ConfirmationsNeuron`, `KernelConfirmations` (~370 LOC) → `DigitalBrain.Runtime.Orleans`<br>• Storage HTTP byte streaming: `StorageContentEndpoints`, `StorageContentGateway`, `UploadPlanBook`, `UploadTicket` (~569 LOC) → `DigitalBrain.Runtime.Http`<br>• Generic Neuron HTTP/gRPC/MCP surface: `LoadedContracts`, `NeuronSurface`, `NeuronsService`, `neurons.proto` (~412 LOC) → `DigitalBrain.Runtime.Http` | **3,701 LOC** | `E:/intochat/digitalbrain/src/DigitalBrain.{Client,Runtime.Orleans,Runtime.Http}` |
| **3. MOVE to `src/Modules/` (Standard Modules, Including `CSharp` + `Sandbox`)** | • `IntoChat.OS.Modules.Microsoft.CSharp` + `Contracts` + `Sandbox` (`os-sandbox` container + ACA Dynamic Session Pool — 5,800 LOC)<br>• `IntoChat.OS.Modules.AI` (10,452 LOC)<br>• `IntoChat.OS.Modules.Flutter` (6,252 LOC)<br>• `IntoChat.OS.Modules.Flame` (390 LOC)<br>• `IntoChat.OS.Modules.Circles` (1,379 LOC + 449 LOC `Circle`/`CircleRoom` features)<br>• `IntoChat.OS.Modules.Microsoft.Aspire` (2,057 LOC)<br>• All 8 `IntoChat.Cloud.*` modules (`Auth`, `Blob`, `Circle`, `Hosting`, `Lobby`, `Localization`, `Mail`, `Organisations` — 8,425 LOC) | **35,204 LOC** | `os/src/Modules/*` (pure `DigitalBrain.Module`s with zero reference to `IntoChat.OS.Kernel`) |
| **4. CONSOLIDATE into `OsModule` (`IntoChat.OS.Modules.Os`)** | • `Identity` (`identity@1`, `secrets@1`, accounts, brains, grants, contacts, vault, OAuth callback, `/connectors`, `/brains/{id}/connections` — 7,915 LOC) + `Machine/*` key custody (`File`, `Keychain`, `Tpm` — 408 LOC) + `ConnectionStorage` & `KernelCapabilityConnections` (416 LOC)<br>• Declarative `Policies/*` (`PolicyWriter`, `PoliciesEndpoints` — 259 LOC)<br>• Workspace `Files/*` (`FilesWorkspaceNeuron`, `VfsNeuron` — 2,361 LOC)<br>• 3D `Brain/*` scene graph (`BrainScene`, `BrainSceneGraph` — 1,196 LOC)<br>• System neurons (`ProfileNeuron`, `UserSettingsNeuron`, `InstalledModulesNeuron` — 249 LOC)<br>• First-party shell/studio surfaces (`Ino`, `CreatorApp`, `Settings`, `Desktop`, `Workspace`, `Gallery`, `Markdraw`, `Data`, `PhotoCraft`, `FilmCraft`, `Post`, `ArtCraftEngineNeuron`) + `Creator` / `BddNeuron` / `ICapabilityBroker` (`LIVE` vs `SANDBOX / TEST CREDENTIALS`) | **~29,500 LOC** | `os/src/Modules/Os/IntoChat.OS.Modules.Os` (a normal `[Module("os")]` package!) |
| **5. KEEP in Thin OS Host** | • **`IntoChat.OS.Kernel`** (absorbs `ServiceDefaults`, `Aspire.Server`, `Contracts.Configuration`, and optionally `IntoChat.OS.Mcp`): `Program.cs`, `CloudHost.cs`, `OperatingSystemModules.cs`, `Install/*` (`AzureInstaller`), health/OTel defaults (~1,450 LOC)<br>• **`IntoChat.OS.AppHost`** (absorbs `ManifestHostingProjector` + `LocalSetup` preflight, ~650 LOC)<br>• **`os/ui`** (Flutter shell client, 38,448 Dart LOC) | **~2,100 C# LOC** | `os/src/IntoChat.OS.Kernel` & `os/src/IntoChat.OS.AppHost` |

#### What the Flutter Shell (`os/ui`) Needs After This Move
- `os/ui` needs **zero architectural rewrite**:
  1. Its multiplexed gRPC-Web channel (`Neurons/Call`, `Neurons/Watch`, `Neurons/ReadState` in `neurons.proto`) is now served directly by `DigitalBrain.Runtime.Http` (`NeuronsService`).
  2. Its auth/identity/connections endpoints (`/auth/*`, `/lobby/*`, `/identity/*`, `/connectors`, `/brains/{id}/connections`) and storage endpoints (`/brains/{id}/storage/...`) are served by `Cloud.Auth`, `Cloud.Lobby`, `OsModule`, and `DigitalBrain.Runtime.Http`.
  3. Its value-tree workspace, `Creator` 3D brain scene / live-vs-sandbox viewport, and control endpoints (`/brains/{id}`, `/brains/{id}/live`, `/brains/{id}/brain/events`, `/brains/{id}/ui/tables/*`, `/brains/{id}/ui/scenes/*`) are served by `FlutterModule`, `FlameModule`, `CSharpModule`, and `OsModule`.

---

### 3.5 Area 5: Testing Framework — What Each Tier Proves, Aspire + Sandbox Conformance in `digitalbrain`, and Fixing Flakiness

#### Ruling
**Yes — `digitalbrain` should own a small, fast Aspire + Testcontainers conformance tier (`DigitalBrain.Framework.Tests.E2E`) that proves BOTH (a) an in-process `[Synapse]` running in `SynapseLoadContext` AND (b) an out-of-process sandbox process connecting via `DigitalBrain.Client` (`IntoChatOSConnection` → `RuntimeEdge`) work on an Aspire-hosted Orleans silo backed by real storage (Azurite), with permission-gated `CallFilter` enforcement and zero secret leakage.** Meanwhile, `modules` owns container + fault-injection tests for each backing service (e.g., killing/pausing Postgres mid-call), and `os` E2E proves only whole-product assembly, auth, and shell journeys.

#### 1. The Target Test Pyramid Across Repositories

| Tier & Owner | Runner / Backing Infrastructure | What It Proves (and What Is Forbidden Here) | Target Count & Duration |
|---|---|---|---|
| **Tier 1: `digitalbrain` Unit & Analyzer Tests** (`*.Tests.Unit`) | `TestOS` (in-memory fake runtime) + Roslyn verifier | Neuron/Synapse programming model, analyzer rules (`DB0001`–`DB0044`), `PolicyEngine`, capability contracts, `TestOS` conformance kits, `dotnet new intochat-module` template verification. **No Orleans silos.** | ~1,300 tests / ~45s |
| **Tier 2: `digitalbrain` Silo, Synapse & Client Integration** (`*.Tests.Integration`) | Shared `LocalSiloHostFactory` (`IClassFixture`) + `SynapseLoadContext` + `RuntimeEdge` | Grain activation, `BrainLog` journaling, checkpoints, `SynapseCompiler` + `SynapseLoader` unloadability, `DigitalBrain.Client` (`SessionConnection` → `RuntimeEdge`) permission checks (`MissingGrant`, `MissingAllowance`, `LimitReached`, vault custody), `ScheduleGrain`, `PoliciesNeuron`, `PlainNeuronCallFilter`, `ConfirmationsNeuron`, `StorageContentEndpoints`, `NeuronSurface` gRPC. | ~600 tests / ~60s |
| **Tier 3: `digitalbrain` Aspire + Real-Storage + Sandbox Conformance (`DigitalBrain.Framework.Tests.E2E`)** | `DistributedApplicationTestingBuilder` + Azurite (`Orleans.Azure`) + sample `Module` + `[Synapse]` + out-of-process sandbox worker (`DigitalBrain.Client`) | **The framework's self-contained proof:** Boots a real Aspire AppHost running an Orleans silo with `CloudProfile` (Azurite Blob/Table/Queue persistence), installs an in-process `[Synapse]`, launches an out-of-process sandbox worker that connects over HTTP+SSE via `DigitalBrain.Client` (`SessionToken`), verifies allowed calls execute with vault secrets staying inside the silo while ungranted calls are blocked by `CallFilter`, restarts the silo against the same Azurite storage, and proves state + journal recovery (`HostConformanceFacts` — including fixing the 2 skipped gap/kill tests!). | ~50 tests / ~45s |
| **Tier 4: Module Unit & Doc-Drift Tests** (`src/Modules/**/*.Tests.Unit` + `Architecture.Tests.Unit`) | `TestOS` only (`Kinds = [TestOS]`) | Module manifest validation (`ModuleOnTestOSFacts`), `ModuleBoundaryFacts`, `ModuleDocumentationFacts` (verifying `README.md` matches `ModuleManifest`), option validation, request/response serialization, and pure neuron logic on `TestOS`. **Never starts an Orleans silo.** | ~850 tests / ~35s |
| **Tier 5: Module Container & Fault-Injection Integration Tests** (`src/Modules/**/*.Tests.Integration`) | Shared `SiloHost` (`Kinds = [Silo]` **only** — never re-runs `TestOS`!) + real service containers (`postgres:16`, `qdrant`, `clickhouse`, `azurite`, `os-sandbox`) | Real protocol execution + **fault injection**: e.g., `PostgresFaultInjectionFacts` defines a table, upserts rows, **pauses/restarts the Postgres container (or terminates backend connections via `pg_terminate_backend`) mid-query**, asserts `PostgresUnavailableException` (503) is thrown and `TableNeuron` state is uncorrupted, unpauses Postgres, and asserts the next `Upsert`/`Read` succeeds without restarting the silo! | ~400 tests / ~80s |
| **Tier 6: `os` Product E2E (`IntoChat.OS.Kernel.Tests.E2E.Aspire`)** | `DistributedApplicationTestingBuilder<IntoChat_OS_AppHost>` (1 shared `OsAppHostFixture` per run) | **Only what lower tiers cannot prove:** Full OS module composition boot, `/health/ready`, auth/session handshake, gRPC-Web `Neurons/Call` & `Neurons/Watch` stream, shell view open journeys, `Creator` live-vs-sandbox broker switch, and a live policy-gated capability call end-to-end. (Deletes `OsHostConformanceFacts` duplicate of `digitalbrain` and deletes the 2x redundant BDD/view checks). | ~30 tests / ~45s |

#### 2. Six Concrete Fixes to Eliminate OS Test Suite Flakiness and Duplication
1. **Restore Cross-Process Socket Reservation in `LocalSiloHostFactory`:** Replace the racy `FreePort()` (`TcpListener(0)` that closes before `builder.Build()`) in [LocalSiloHostFactory.cs:88-93](file:///E:/intochat/digitalbrain/src/DigitalBrain.Runtime.Orleans/Testing/LocalSiloHostFactory.cs#L88-L93) with a process-wide + lockfile-backed `PortReservation` ring buffer.
2. **Stop Re-Running `TestOS` Inside `.Tests.Integration`:** In `Time.Tests.Integration`, `Flutter.Tests.Integration`, `AI.Tests.Integration`, and `DigitalBrain.Testing.Silo`, change `.Tests.Integration` to run `NeuronRuntimeKind.Silo` **only** (since `.Tests.Unit` already ran `NeuronRuntimeKind.TestOS`), and delete the 3 copy-pasted `BothRuntimesFixture` clones in `AiFixture.cs`, `FlutterFixture.cs`, and `FlameFixture.cs` in favor of `DigitalBrain.Testing.Silo`.
3. **Run Shipped Surface View Verification Once (Not 3x):** Keep fast view/scenario verification in `.Tests.Unit` (`TestOS`), keep 1 smoke journey per surface over HTTP/gRPC in `Kernel.Tests.E2E.Aspire`, and delete `RestoredAppsE2EFacts.AllShippedAppsPassExecutableBddScenariosAndPersistReportsInBddNeuronOnSilo` from `Kernel.Tests.E2E`.
4. **Share One Warm `CSharpFixture` / Roslyn Metadata Reference Cache Across `CSharp.Tests.Integration`:** Instead of re-creating `MetadataReference` sets and 30-second `AppBaseline` loops per test class, cache the standard assembly `MetadataReference` set once per process and delete the duplicate `ReactionWriter` + `Apps/Shell/*` script compilation tests.
5. **Disable `RefreshCodeGraph` During MSBuild Builds by Default:** Change `<CodeGraphRefresh Condition="'$(CodeGraphRefresh)' == ''">false</CodeGraphRefresh>` in `Directory.Build.props` so local Windows `dotnet build` and `dotnet test` on the full solution never spawn 88 concurrent `npx @colbymchenry/codegraph` processes.
6. **Delete `ModuleConfigurationOptions.Current` Mutable Static (`[ModuleInitializer]` Race):** Pass the configuration root prefix (`"IntoChatOS"` or `"DigitalBrain"`) through `IConfiguration` / `DigitalBrainHostOptions` with a fallback check on both prefixes during bind, eliminating the `[ModuleInitializer]` ordering bug (`c2e0a9aca`).

---

### 3.6 Area 6: Deployment & Zero-Script Code-First Architecture — Delete All 24 Scripts (`1,984 LOC`) in Favor of `AzureInstaller`, Aspire Commands, and xUnit Facts

#### Ruling
**Eliminate every `.sh`, `.py`, `.ps1`, and standalone `scripts/*.cs` file across all repositories (`24 script files / 1,984 LOC` + `6 committed .pyc bytecode files` + `5 obsolete loop/*.py scripts` → `0 scripts`).**
1. **Cloud Deployment (`IntoChat.OS.Kernel install`):** Keep the native C# [`AzureInstaller`](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/AzureInstaller.cs) + [`InstallPlan`](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/InstallPlan.cs) + [`ArmAzure`](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs) introduced in `0055b8adb`. Expand `AzureInstaller` + `InstallPlan` + `ArmAzure` by **~120 lines of C#** so `IntoChat.OS.Kernel install` handles `os-ui` Container App reconciliation, ACR `@sha256:` image digest import (`--promote-from`), JWT principal OID resolution from `TokenCredential`, and HTTP `/health/ready` + `/` smoke checks directly in compiled, unit-tested C# — **deleting `scripts/deploy/{deploy.sh, deploy_seeding_check.py, release_manifest.py, deployment_gate.py, smoke.sh}` and their Python unit tests (`838 LOC` + `6 .pyc` files) completely**.
2. **Local Dev & Packaging (`IntoChat.OS.AppHost` Aspire Commands):** Use [`IntoChat.OS.AppHost`](file:///E:/intochat/os/src/IntoChat.OS.AppHost/AppHost.cs) (`Aspire.AppHost.Sdk/13.6.1`, which already implements `reset` in C# and supports Aspire `WithCommand` / `aspire do` pipeline commands) for local dev commands (`reset`, `package-msix`, and local `deploy`), and promote `scripts/msix/launcher/*.cs` into a compiled `.csproj` (`src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.csproj`).
3. **Repository, Docs, Template & Container Verification (xUnit + MSBuild + Roslyn):** Move all repository-layout, README drift, template verification, and Nginx proxy checks into standard xUnit test suites (`IntoChat.OS.Architecture.Tests.Unit`, `DigitalBrain.Templates.Tests.Unit`, `IntoChat.OS.Kernel.Tests.Integration`) and Roslyn `BannedSymbols.txt`.

#### Why `scripts/deploy/*` (838 LOC of Bash + Python) Was Duplicating C# Code Already in `ArmAzure.cs` & `InstallPlan.cs`
Line-by-line inspection of `scripts/deploy/*` against [`InstallPlan.cs`](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/InstallPlan.cs) and [`ArmAzure.cs`](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs) shows that `deploy.sh` was re-implementing in fragile bash/jq/Python what the C# installer already does:
1. **`az group create` / `az acr create` ([deploy.sh:78-79](file:///E:/intochat/os/scripts/deploy/deploy.sh#L78-L79)):** `InstallPlan.Foundation` ([InstallPlan.cs:88-98](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/InstallPlan.cs#L88-L98)) and [`ArmAzure.Ensure` (`ArmAzure.cs:57-63`)](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs#L57-L63) **already** ensure the Azure Resource Group and ACR Registry idempotently via the Azure ARM SDK. `deploy.sh` only ran them in bash because it was building images before running `AzureInstaller`.
2. **`render_config()` + inline Python JWT base64 decoding + 10-line `jq` merge ([deploy.sh:126-143](file:///E:/intochat/os/scripts/deploy/deploy.sh#L126-L143)):** `deploy.sh` shells out to `az account get-access-token`, pipes the token into `python3 -c` to base64-decode the JWT `oid` claim (`installerPrincipalId`), and runs `jq` to merge `OS_DEPLOY_SECRETS_JSON`. In C#, [`ArmAzure.cs`](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs#L43) already holds `TokenCredential` (`DefaultAzureCredential`)! Calling `credential.GetTokenAsync` and reading the `oid` claim + merging `OS_DEPLOY_SECRETS_JSON` + `release-manifest.json` inside `InstallConfig.Load()` takes **15 lines of strongly-typed C#** with zero `jq` and zero Python.
3. **`update_ui()` + manual `az containerapp revision deactivate` loop ([deploy.sh:146-159](file:///E:/intochat/os/scripts/deploy/deploy.sh#L146-L159)):** `deploy.sh` updated `os-ui` in bash only because [`InstallPlan.cs:119-149`](file:///E:/intochat/os/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel/Install/InstallPlan.cs#L119-L149) defined a `ContainerAppSpec` for `OsApp` (`ca-intochat-os-<env>`) and omitted `UiApp` (`ca-intochat-ui-<env>`)! And [`ArmAzure.EnsureApp` (`ArmAzure.cs:393-396,462-468`)](file:///E:/intochat/modules/src/IntoChat.OS.Modules/Microsoft/Azure/src/IntoChat.OS.Modules.Microsoft.Azure/ArmAzure.cs#L393-L396) **already** sets `ActiveRevisionsMode = ContainerAppActiveRevisionsMode.Single` (so Azure automatically deactivates old revisions) and diffs `MatchesDesired` before updating. Adding `UiApp` (`ContainerAppSpec` with `KERNEL_ORIGIN = $"https://{osFqdn}"`) to `InstallPlan.cs` deletes `update_ui()` and manual revision loops completely.
4. **`deploy_seeding_check.py` + `test_deploy_seeding_check.py` (392 LOC + 6 `.pyc` files):** Polled `az monitor log-analytics query` with a KQL regex (`Integration seeding completed with ([0-9]+) failures`) only because `IntegrationSeeder` ran asynchronously in the background. Retiring `IntegrationSeeder` in favor of `ModuleManifest.Secrets` (Decision 4) and surfacing module readiness on `/health/ready` makes `deploy_seeding_check.py` 100% obsolete.
5. **`release_manifest.py` (71 LOC with tests):** Shells out to `az acr repository show` and `az acr import`. `IntoChat.OS.Modules.Microsoft.Azure` **already references `Azure.ResourceManager.ContainerRegistry` (`1.4.0`)** — adding `ImportImageByDigestAsync` to `ArmAzure.cs` (`--promote-from acrintochattest`) replaces `release_manifest.py` with 25 lines of C#.
6. **`deployment_gate.py` (117 LOC with tests) & `smoke.sh` (47 LOC):** `deployment_gate.py` polled `gh run list` because `deploy.yml` was separate from `release.yml` (Decision 2 deletes it). `smoke.sh` was a copy-paste of lines 194–208 of `deploy.sh` (`curl /health` + `az containerapp revision list`); a 20-line C# `HttpClient` smoke check at the end of `AzureInstaller.Run` replaces both.

#### Evaluation of Code-First Options for Replacing Scripts
| Option | How It Works | Verdict |
|---|---|---|
| **Option 1: Hybrid Code-First (`IntoChat.OS.Kernel install` + `IntoChat.OS.AppHost` Aspire Commands + xUnit Facts)** | • **Cloud CD (`release.yml`):** Invokes the pre-built `./IntoChat.OS.Kernel install` binary directly (`--env`, `--config`, `--manifest`, `--promote-from`). Zero bash/Python, zero Aspire DCP restore overhead on CD runners, 100% unit-tested via `InMemoryCloud`.<br>• **Local Dev & Desktop Packaging (`IntoChat.OS.AppHost`):** Uses `AppHost.cs` (`Aspire.AppHost.Sdk/13.6.1`) commands (`dotnet run --project src/IntoChat.OS.AppHost -- reset / package-msix` or `aspire do`).<br>• **CI / Repo Checks:** Standard xUnit facts (`Architecture.Tests.Unit`, `Templates.Tests.Unit`) + MSBuild + Roslyn `BannedSymbols.txt`. | **RECOMMENDED** — Fastest in CI/CD (<1s startup for pre-built `Kernel` binary), uses Aspire commands where Aspire is already loaded, and enforces repo/doc invariants on every `dotnet test`. |
| **Option 2: All-In on `aspire do` (`IntoChat.OS.AppHost`) for Cloud CD Too** | Wire `aspire do deploy-test` and `aspire do deploy-prod` in `IntoChat.OS.AppHost` (`Aspire.AppHost.Sdk/13.6.1`), which delegates to `AzureInstaller.Run`. | **Good as a local alias (`aspire do deploy`), rejected as the primary CI/CD entrypoint:** Running `aspire do` on a clean GitHub Actions CD runner requires restoring/building `IntoChat.OS.AppHost` and Aspire DCP packages (~45s overhead), whereas `package` already publishes the self-contained `IntoChat.OS.Kernel` binary. |
| **Option 3: Dedicated `IntoChat.DevCli` C# Tool Project** | Create a new `src/IntoChat.DevCli` console project to hold all build, repo-sync, doc-check, and deploy subcommands. | **Rejected:** Creates another CLI project to maintain when `IntoChat.OS.Kernel` (`install`), `IntoChat.OS.AppHost` (`reset`, `package-msix`), and xUnit (`Architecture.Tests.Unit`) already own those exact domains. |

#### Complete Zero-Script Replacement Map (All 24 Scripts / 1,984 LOC → 0 Scripts)
| Script Today | LOC | Explicit Code-First Replacement (Zero Scripts) |
|---|---:|---|
| `os/scripts/deploy/deploy.sh` | 211 | **Delete.** Replaced by `./IntoChat.OS.Kernel install --env <env> --config deploy/<env>.json --manifest release-manifest.json` (`AzureInstaller` + `InstallPlan.UiApp` + `ArmAzure`). |
| `os/scripts/deploy/deploy_seeding_check.py` + `test_*.py` + 6 `.pyc` files | 392 | **Delete.** Retiring `IntegrationSeeder` for `ModuleManifest.Secrets` + `/health/ready` C# check in `AzureInstaller` makes Log Analytics KQL scraping obsolete. |
| `os/scripts/deploy/release_manifest.py` + `test_*.py` | 71 | **Delete.** Replaced by `ArmAzure.ImportImageByDigestAsync` (`Azure.ResourceManager.ContainerRegistry`) via `./IntoChat.OS.Kernel install --promote-from acrintochattest`. |
| `os/scripts/deploy/deployment_gate.py` + `test_*.py` | 117 | **Delete.** Merging `release.yml` and `deploy.yml` into one workflow (Decision 2) + 2s `ci` check-run gate eliminates polling `gh run list`. |
| `os/scripts/deploy/smoke.sh` | 47 | **Delete.** Replaced by `AzureInstaller.VerifySmokeAsync(HttpClient)` (`/health/ready`, `/`, and single active ACA revision assertion via `ArmAzure`). |
| `os/scripts/sync-solution.cs` | 97 | **Delete.** [`RepositoryLayoutFacts.cs`](file:///E:/intochat/os/src/Testing/IntoChat.OS.Architecture.Tests.Unit/RepositoryLayoutFacts.cs) in `IntoChat.OS.Architecture.Tests.Unit` *already* encodes the exact same `.slnx` folder rules in compiled unit tests! Add a 15-line autofix helper when `FIX_LAYOUT=1` (or `AppHost` command). |
| `os/scripts/module-readmes.cs` | 190 | **Delete.** Move into `ModuleDocumentationFacts.cs` in `IntoChat.OS.Architecture.Tests.Unit` (verifies `ModuleManifest` vs `README.md` on every `dotnet test`, auto-updates when `UPDATE_DOCS=1`). |
| `os/scripts/bump-pins.cs` & `modules/scripts/bump-pins.cs` | 190 | **Delete** (along with `BumpPinsFacts.cs`). Folding `modules` into `os` kills the first; native `.github/dependabot.yml` (grouped for `DigitalBrain*` on `nuget.org`) replaces the second. |
| `digitalbrain/scripts/pack-local.cs` | 100 | **Delete.** Standard `dotnet pack DigitalBrain.slnx -c Release -o ../os/.local-feed` packs all 19 packable projects in one MSBuild graph invocation instead of spawning 19 sequential child processes. |
| `os/scripts/check-sandbox-image.sh` | 33 | **Delete.** [`CSharp.Sandbox/Dockerfile:16`](file:///E:/intochat/os/src/IntoChat.OS.Modules/Microsoft/CSharp/src/IntoChat.OS.Modules.Microsoft.CSharp.Sandbox/Dockerfile#L16) already runs `test -f /app/DigitalBrain.dll && test -f /app/DigitalBrain.UI.dll` at `docker build` time, and `CSharp.Tests.Integration` verifies container startup and `/health` in C#. |
| `digitalbrain/scripts/ci/template-smoke.sh` | 120 | **Delete.** Replace with C# xUnit `TemplateSmokeFacts.cs` in `digitalbrain/tests/DigitalBrain.Templates.Tests.Unit` using `Microsoft.TemplateEngine.Authoring.TemplateVerifier` (already in `Directory.Packages.props`). |
| `digitalbrain/scripts/ci/fail-on-skipped.sh` | 47 | **Delete.** Fix the 2 skipped `HostConformanceFacts` and add `M:Xunit.Assert.Skip(System.String)` to Roslyn `BannedSymbols.txt` in E2E projects (fails at **compile time** instead of parsing `.trx` XML with `sed`). |
| `os/ui/shell/test/nginx_proxy_test.py` | 164 | **Delete.** Replace with C# xUnit `NginxProxyFacts.cs` in `IntoChat.OS.Kernel.Tests.Integration` (starts a lightweight Kestrel upstream + `os-ui` container and asserts SSE/WebSocket/auth-header forwarding). |
| `os/scripts/msix/build-msix.ps1` + `launcher/*.cs` | 503 | **Delete `.ps1` script & promote launcher to `.csproj`.** Move `scripts/msix/launcher/{IntoChat.OS.Desktop.cs, DesktopKeyBackup.cs}` into a real project `src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.csproj` (compiled and analyzer-checked in `Intochat.OS.slnx`!), and add `MsixPackager` command to `IntoChat.OS.AppHost` (`aspire do package-msix`). |
| `os/ui/core/tool/generate_proto.sh` & `os/docs/superpowers/loop/*.py` | 8 + 5 files | **Delete.** Replace `generate_proto.sh` with `dart run tool/generate_proto.dart` (or MSBuild `<Protobuf>`), and delete the 5 obsolete `ReactionWriter`/`FastPath` benchmark `.py` files in `docs/superpowers/loop/`. |

#### How Rollback Works Under the Zero-Script One-Release-One-Run Rule
- Every release attaches its verified `release-manifest.json` (containing the immutable `@sha256:` digests for `os`, `os-ui`, and `os-sandbox` and commit SHA) as an asset on the GitHub Release, and those digests remain in `acrintochatprod`.
- To roll back production to any prior release (e.g., `v0.14.1`), trigger `release.yml` via `workflow_dispatch` with input `rollback_tag: v0.14.1`. When `rollback_tag` is set, `release.yml` **skips `package` and `deploy-test`**, downloads `release-manifest.json` from the `v0.14.1` GitHub Release, and runs `./IntoChat.OS.Kernel install --env prod --config deploy/prod.json --manifest release-manifest.json`. **Total rollback time: ~90 seconds**, with zero scripts, zero rebuilds, and 100% digest identity.

---

## 4. A Phased Plan

Each phase is shippable independently, leaves all tests and deployments green, and has concrete, measurable outcomes. Phases 1, 2, and 3 are scoped and named so they can be planned directly with the `writing-plans` skill next.

| Phase | Name (for `writing-plans`) | Scope & Key Actions | Measurable Outcome |
|---:|---|---|---|
| **Phase 1** | **`phase-1-pipeline-consolidation-single-build-release-and-zero-deploy-scripts`** | 1. Fix `os/.github/workflows/e2e.yml` phantom filter (`--filter-trait "needs=artcraft"`) and move `IntoChat.OS.Kernel.Tests.E2E` + `E2E.Aspire` into `os/.github/workflows/ci.yml` as an `e2e` job.<br>2. Refactor `os/.github/workflows/ci.yml` to build once (`build` job uploads `artifacts/bin/Release`), run `test-unit`, `test-integration`, `e2e` with `--no-build`, and fold `flutter.yml` and `gitleaks.yml` into `ci.yml`.<br>3. Expand `AzureInstaller` + `InstallPlan` + `ArmAzure` (~120 LOC C#) to reconcile `os-ui` (`UiApp`), read `release-manifest.json` and `OS_DEPLOY_SECRETS_JSON`, resolve JWT `oid` from `TokenCredential`, import ACR digests (`--promote-from`), and run C# `/health/ready` + `/` smoke checks. **Delete `scripts/deploy/*` (all 8 bash/Python files + 6 `.pyc` files) and `scripts/check-sandbox-image.sh`.**<br>4. Merge `os/.github/workflows/release.yml` and `deploy.yml` into a single `release.yml` (`package` → `deploy-test` → `deploy-prod`) that checks the `ci` check-run on `$GITHUB_SHA` (2s), builds `os`, `os-ui`, and `os-sandbox` **once** in parallel with `PublishProfile=Container` and `type=gha` Docker cache, and runs `./IntoChat.OS.Kernel install` directly.<br>5. Move non-secret `test.json` and `prod.json` into `os/deploy/<env>.json` (retiring `intochat/os-deployment` and `DEPLOYMENT_VALUES_TOKEN`), fix the `production` GitHub Environment Azure OIDC role assignment (`42046070757`), and add `rollback_tag` input to `release.yml`. | • **Workflows removed:** 5 (`deploy.yml`, `e2e.yml`, `flutter.yml`, and 3 `gitleaks.yml` files)<br>• **Scripts removed:** **9 files (`871 LOC`) + 6 `.pyc` files** (`scripts/deploy/*` + `check-sandbox-image.sh`)<br>• **Release-to-prod time:** drops from **32.5m (`1,950s`)** to **~7.5m (`~450s`)** (**-25 minutes saved per release**)<br>• **Push CI time in `os`:** drops from **8.1m (`485.5s`)** to **~4.2m (`~250s`)**<br>• **Container builds per release:** drops from **6 images (2x3)** to **3 images (1x3)**, with 100% digest parity<br>• **Repositories retired:** `intochat/os-deployment` + `DEPLOYMENT_VALUES_TOKEN` eliminated |
| **Phase 2** | **`phase-2-fold-modules-into-os-smart-templates-docs-and-zero-repo-scripts`** | 1. Move the 28 module projects and 26 test projects from `E:/intochat/modules/src` into `E:/intochat/os/src/Modules/`, preserving `IntoChat.Modules.Architecture.Tests.Unit` and adding `os/src/Modules/Directory.Build.props` to forbid any reference to `IntoChat.OS.*` host projects.<br>2. Archive `intochat/modules`, configure `.github/dependabot.yml` for `DigitalBrain*` on `nuget.org`, stop packing 40 NuGet packages in `os`, and delete `PACKAGE_TOKEN` from all workflows and `os-sandbox/Dockerfile`.<br>3. Upgrade `DigitalBrain.Templates` (`dotnet new intochat-module`) with `--in-tree` (default inside `os/src/Modules/`) vs `--standalone`, add a canonical `TemplateModule`, and expose `IModuleScaffolder` in `RoslynModule` so Ino can scaffold & test new modules in dev workflow.<br>4. Co-locate documentation in `digitalbrain/docs/` and `os/docs/` (absorbing `os.docs` and `intochat.docs`) and move `scripts/module-readmes.cs` into `ModuleDocumentationFacts` in `Architecture.Tests.Unit`.<br>5. **Eliminate the remaining 15 scripts across `os` and `digitalbrain`:** delete `os/scripts/bump-pins.cs`, `modules/scripts/bump-pins.cs`, `os/scripts/sync-solution.cs` (already covered by `RepositoryLayoutFacts.cs`), `digitalbrain/scripts/pack-local.cs` (replaced by `dotnet pack DigitalBrain.slnx`), `digitalbrain/scripts/ci/template-smoke.sh` (replaced by `TemplateSmokeFacts.cs`), `digitalbrain/scripts/ci/fail-on-skipped.sh` (replaced by Roslyn `BannedSymbols.txt`), `os/ui/shell/test/nginx_proxy_test.py` (replaced by `NginxProxyFacts.cs`), `os/scripts/msix/build-msix.ps1` (promoting `launcher/*.cs` to `src/IntoChat.OS.Desktop` + `AppHost` `package-msix` command), and `os/docs/superpowers/loop/*.py`. | • **Repositories retired:** **3** (`intochat/modules`, `os.docs`, `intochat.docs` → leaving just **`digitalbrain` + `os`**)<br>• **Scripts removed:** **All remaining 15 scripts (`1,113 LOC`) + 5 `loop/*.py` files → 0 `.sh`, `.py`, `.ps1`, or `scripts/*.cs` files left in either repo!**<br>• **Workflows removed:** **6** (`modules/{ci,release,bump,gitleaks}.yml` + `os/bump.yml` + `intochat.docs/deploy.yml`)<br>• **NuGet packages packed/pushed on OS CI:** drops from **68 (`28 + 40`) to 0** (**-2m 37s saved per push**)<br>• **Tokens & scratch files removed:** `PACKAGE_TOKEN`, `Directory.Build.local.props`, and scratch `nuget.config` eliminated<br>• **Tests recovered in CI:** **+96** integration/browser tests from `modules` + `ModuleDocumentationFacts` + `TemplateSmokeFacts` |
| **Phase 3** | **`phase-3-elevate-client-runtime-edge-and-prune-dead-app-layers`** | 1. Promote `IntoChat.OS.Client` (`IntoChatOSConnection`, `SessionConnection`, `NeuronProxy`) and `RuntimeEdge` (`/brains/{id}/runtime/v1/{invoke,report,stream}`, `SessionTokens`) into `digitalbrain` (`DigitalBrain.Client` + `DigitalBrain.Runtime.Http`), keeping `CSharpModule` + `CSharp.Sandbox` (`os-sandbox`) and `CreatorApp` wired to `DigitalBrain.Client`.<br>2. Wire `DigitalBrain.Runtime.Synapses` (`OrleansSynapseRegistrar` + `SynapseRestorer`) into `IntoChat.OS.Kernel.Composition` alongside `CSharp.Sandbox`.<br>3. Delete `ReactionWriter` & `ReactionExamples` (892 LOC, superseded by `PolicyWriter`), delete the uncompiled duplicate script copies in `IntoChat.OS.Modules.Apps/Shell/*` (2,188 LOC), and delete legacy `IntoChat.OS.Cli` + `DigitalBrain.Runtime.Apps` + `DigitalBrain.Apps.Packaging` (~1,318 LOC). | • **Lines of dead/duplicate code removed:** **~4,800 LOC** (`ReactionWriter`, duplicate `Shell/*` scripts, `Cli`, `Runtime.Apps`, `Apps.Packaging`, and core duplicates)<br>• **Framework capability gained:** Any sandbox container (`os-sandbox`), CLI, or remote agent can connect to any `DigitalBrain` host via `DigitalBrain.Client` (`RuntimeEdge`) with full `CallFilter` permissions and zero-leakage vault access |
| **Phase 4** | **`phase-4-uniform-module-manifest-and-postgres-resilience`** | 1. Add `Connectors` and typed infrastructure `Needs` (`Need.Postgres`, `Need.VectorStore`, `Need.Container`) to `ModuleManifest` in `digitalbrain`.<br>2. Migrate all modules from `IntegrationSchema` / `IModuleIntegrations<T>` to `ModuleManifest.Secrets` + `IModuleSecrets<T>`, and delete `IntegrationSchema`, `IntegrationSeeder`, `IntegrationNeuron`, and `IIntegrationStartup`.<br>3. Replace the 16 bespoke `IntoChat.OS.Aspire.Hosting/Modules/*` classes with a single `ManifestHostingProjector`, and align `InstallPlan.PostgresSpecs` with `ManifestEnvironment` (`postgres-connectionstring`).<br>4. Refactor `PostgresSourceRegistry` to lazy initialization, unify `PostgresExceptionClassifier` across `PostgresProvider` and `TableStore`, and add `PostgresFaultInjectionFacts` (testing container pause/kill mid-call). | • **Files removed:** **~24 files / ~2,100 LOC** (`IntegrationSeeder`, `IntegrationNeuron`, `IntegrationSchema`, and 16 `ModuleHosting<T>` files)<br>• **Startup crash fixed:** Enabling `postgres` without a connection string fails fast at config validation instead of crashing the Orleans silo during `IntegrationSeeder`; transient DB downtime marks health `Degraded` and returns `503` cleanly |
| **Phase 5** | **`phase-5-thin-os-host-and-framework-boundaries`** | 1. Move `BrainNeuron`, `PlainNeuronCallFilter`, `CallFilter`, `InstanceDirectoryGrain`, and `ConfirmationsNeuron` (~1,140 LOC) to `DigitalBrain.Runtime.Orleans`, and move `StorageContentEndpoints` + `NeuronSurface` / `NeuronsService` gRPC (~980 LOC) to `DigitalBrain.Runtime.Http`.<br>2. Delete `IntoChat.OS`, `IntoChat.OS.Contracts.Compute`, `IntoChat.OS.Templates`, and the duplicate runtime types in `IntoChat.OS.Framework`.<br>3. Move `AI`, `Flutter`, `Flame`, `Circles`, `Aspire`, `CSharp`, and `Cloud.*` into `os/src/Modules/`, and consolidate `Identity`, `Policies`, `Files`, `BrainScene`, `Creator`, and first-party shell surfaces into `IntoChat.OS.Modules.Os` (`OsModule`), leaving only `IntoChat.OS.Kernel`, `IntoChat.OS.AppHost`, and `IntoChat.OS.Desktop` at the host layer. | • **OS Host C# footprint:** shrinks from **47 prod projects / 75,303 LOC** to **3 host projects (~2,450 LOC)** + modular `src/Modules/*` catalog |
| **Phase 6** | **`phase-6-framework-aspire-conformance-and-deflake`** | 1. Replace `LocalSiloHostFactory.FreePort()` with lockfile/reserved port binding (`PortReservation`).<br>2. Change `.Tests.Integration` projects to run `NeuronRuntimeKind.Silo` only (stopping duplicate `TestOS` runs) and delete duplicate `BothRuntimesFixture` classes.<br>3. Fix the 2 skipped `HostConformanceFacts` (`Gap_a_stale_cursor...` and `KillBetweenWriteAndPublish...`) in `digitalbrain`, and expand `DigitalBrain.Framework.Tests.E2E` into an Aspire + Azurite + `[Synapse]` + out-of-process `DigitalBrain.Client` sandbox conformance suite. | • **Framework confidence:** `digitalbrain` proves real Aspire + Azure storage + in-process `[Synapse]` + out-of-process `DigitalBrain.Client` sandbox execution + crash/gap recovery on every framework PR<br>• **Duplicate test executions removed:** **~420 redundant test runs eliminated**; zero port-collision flakes under `--max-parallel-test-modules 4` |

---

## 5. Decisions for the Owner (Updated with Owner Rulings)

All six architectural decisions have been reviewed and resolved with the owner:

1. **Repository Topology (`3 repos` → `2 repos: digitalbrain + os`) — APPROVED (with Smart Contributor & Ino Module Template):**
   - **Ruling:** Fold `intochat/modules` back into `intochat/os` under `src/Modules/` (enforcing the zero-OS-dependency boundary via `src/Modules/Directory.Build.props` and `IntoChat.Modules.Architecture.Tests.Unit`), and keep `intochat/digitalbrain` as the separate open-source framework repository publishing to `nuget.org`.
   - **Refinement:** Upgrade `DigitalBrain.Templates` (`dotnet new intochat-module`) to support `--in-tree` (default inside `os/src/Modules/`) and `--standalone`, keep a canonical `TemplateModule` in `os/src/Modules/Template/`, and expose `IModuleScaffolder` in `RoslynModule` so Ino and contributors can scaffold and test a new module in one step.
2. **Release Workflow (`Single build to ACR + Manifest Promotion`) & Co-Located Documentation — APPROVED:**
   - **Ruling:** Merge `release.yml` and `deploy.yml` into a single `release.yml` workflow (`package` → `deploy-test` → `deploy-prod`) that builds `os`, `os-ui`, and `os-sandbox` **once**, pushes to `acrintochattest`, and promotes by `@sha256:` digest to `acrintochatprod`.
   - **Documentation Strategy:** Co-locate documentation inside the two code repositories (`digitalbrain/docs/` for framework docs and `os/docs/` for OS/product/module docs + Cloudflare Pages publishing, retiring the separate `os.docs` and `intochat.docs` repos), and enforce zero doc drift via `ModuleDocumentationFacts` in `Architecture.Tests.Unit` (which verifies every module's `README.md` against its `ModuleManifest`).
3. **Sandboxed Code Execution (`CSharp.Sandbox` + `IntoChat.OS.Client` + `Creator`) vs. Legacy Redundancies — RESOLVED (Keep & Elevate Sandbox + Client; Delete Only Redundancies):**
   - **Ruling:** **Keep** `CSharpModule`, `CSharp.Sandbox` (`os-sandbox` container + ACA Dynamic Session Pool), and `CreatorApp`, and **elevate** `IntoChat.OS.Client` (`IntoChatOSConnection` / `SessionConnection`) + `RuntimeEdge` (`/brains/{id}/runtime/v1/{invoke,report,stream}`) into `digitalbrain` (`DigitalBrain.Client` + `DigitalBrain.Runtime.Http`).
   - **Why:** In-process `[Synapse]` is restricted by `DB0041`–`DB0044` (no I/O, no network, no threads, no arbitrary packages) to protect the Orleans silo, and `Policy` is declarative only. Out-of-process `CSharp.Sandbox` + `DigitalBrain.Client` + `RuntimeEdge` is the only mechanism that lets full, unrestricted C# code or external processes execute in isolation while calling back into the brain with `CallFilter` permissions (`MissingGrant`, `MissingAllowance`, `LimitReached`, `OutsideWorkspace`) and zero-leakage access to `VaultNeuron` / `ConnectionNeuron<TState>`.
   - **What is deleted instead (~4,800 LOC):** Delete `ReactionWriter` (892 LOC, superseded by `PolicyWriter`), the uncompiled duplicate script copies in `IntoChat.OS.Modules.Apps/Shell/*` (2,188 LOC), and legacy `IntoChat.OS.Cli` / `DigitalBrain.Runtime.Apps` / `Apps.Packaging` shims (~1,318 LOC).
4. **Retiring `IntegrationSchema` + `IntegrationSeeder` in Favor of `ModuleManifest.Secrets` — APPROVED:**
   - **Ruling:** Standardize all deployment-level module secrets on `ModuleManifest.Secrets` (`IntoChatOS:Modules:<id>:Secrets:<Name>` backed by Key Vault `{id}-{name}`) and retire `IntegrationSchema`, `IntegrationSeeder`, and `IntegrationNeuron` (Phase 4).
5. **Retiring `intochat/os-deployment` in Favor of Repo-Tracked `os/deploy/<env>.json` + GitHub Environment Secrets — APPROVED:**
   - **Ruling:** Move non-secret environment configuration (`test.json`, `prod.json`) into `os/deploy/<env>.json`, keep all secrets in GitHub Environment secrets (`OS_DEPLOY_SECRETS_JSON`) / Azure Key Vault, and retire the separate `intochat/os-deployment` repository and `DEPLOYMENT_VALUES_TOKEN` (Phase 1).
6. **Zero-Script Code-First Architecture (`24 scripts / 1,984 LOC` → `0 scripts`) — APPROVED:**
   - **Ruling:** Delete the entire `scripts/` directory across `os`, `modules`, and `digitalbrain` (all 24 `.sh`, `.py`, `.ps1`, and `scripts/*.cs` files + 6 committed `.pyc` binaries + 5 obsolete `loop/*.py` scripts), and add a guard in `RepositoryLayoutFacts.cs` forbidding `.sh`, `.py`, `.ps1`, and `scripts/*.cs` files. Put cloud deployment/promotion/smoke in `IntoChat.OS.Kernel install` (`AzureInstaller` + `InstallPlan` + `ArmAzure`), local dev & MSIX packaging commands in `IntoChat.OS.AppHost` (`aspire do` / `AppHost` commands, with `IntoChat.OS.Desktop` as a compiled `.csproj`), and repository/doc/template/proxy checks in standard xUnit test suites (`Architecture.Tests.Unit`, `Templates.Tests.Unit`, `Kernel.Tests.Integration`) and Roslyn `BannedSymbols.txt` (Phases 1 & 2).
