# OS-App Lifecycle Ownership Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Resolve PR #135's five findings structurally: typed OS-app manifest, adoption inside the Apps module, brain-scoped Settings preferences, composition-owned app wiring, gitignore fix.

**Architecture:** `OsApp(Package, Replaces?)` replaces the stringly manifest. `InstallApp` gains `Adopts`: the App grain transfers legacy Postgres table ownership and retires the legacy app as a step of a successful install; `OsBootService`'s tail block is deleted. Settings keys preferences by brain. `AddOperatingSystem()` wires the OS apps through a new `ConfigureServices` hook on `DigitalBrainServerBuilder`.

**Tech Stack:** .NET / Orleans grains, xUnit v3 `ModuleTest`, sandbox C# scripts.

**Spec:** `docs/superpowers/specs/2026-10-04-os-app-lifecycle-ownership-design.md`

**Branch:** work lands on `feat/os-as-product` (PR #135).

## Global Constraints

- Persisted grain state and Orleans-serialized records: append `[Id(n)]`, never renumber (`InstallApp` gets `[property: Id(4)]`).
- Never build the `.slnx`; `dotnet test` per project.
- Tests: sentence names, `TestContext.Current.CancellationToken`, real grains (`ModuleTest.Create().WithModule<AppsModule>()`), no grain mocks.
- The App grain never self-calls; the legacy app it retires is a *different* grain (cross-grain call is fine).
- Settings ships through the publish gate: `app.cs` and `tests.cs` change together and must verify green.
- Each task ends with its suite green and a commit ending `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## Review Focus

1. Adoption when Postgres module is absent from the composition — the transfer step must degrade like `CompleteUninstall` does (its `postgresAvailable` guard), not throw install into failure. Pinned in Task 2 (the base `ModuleTest` host has no Postgres module, so the happy-path fact itself covers it).
2. Repeated `Activated` delivery after a completed adoption — the legacy app is already gone; the retire must be a read-guarded no-op. Pinned in Task 2.
3. `OsApp.Parse` with a malformed value must name the offending string. Pinned in Task 1.
4. A host calling both `AddOperatingSystem()` (now wiring apps) and a leftover explicit `AddOperatingSystemApps()` would double-register the manifest — `AddOperatingSystemApps` must be idempotent on the manifest (guard: add only ids not already present). Pinned in Task 4.
5. Settings preferences must survive uninstall + reinstall under a different package id. Pinned in Task 3 (tests.cs scenario).

---

### Task 1: Typed manifest

**Files:**
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS/OsApps.cs`
- Test: `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsShippedAppFacts.cs`, `OsBootFacts.cs`

**Interfaces:**
- Produces: `public sealed record OsApp(PackageId Package, PackageId? Replaces = null)` with `public static OsApp Parse(string package, string? replaces = null)` that wraps `PackageId.Parse` and rethrows with the raw string in the message; `OsAppsOptions.Apps` becomes `IList<OsApp>`; `Replaces` dictionary is **deleted**. Task 2 consumes `OsApp.Replaces`; Task 4 consumes `AddOperatingSystemApps`.

- [ ] **Step 1: Write the failing facts** — in `OsShippedAppFacts`, replace the manifest assertions and add the parse fact:

```csharp
[Fact]
public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
{
    var services = new ServiceCollection();
    services.AddOperatingSystemApps(os => os.Apps.Add(OsApp.Parse("intochat/customer-researcher")));
    using var provider = services.BuildServiceProvider();
    var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
    Assert.Equal(["os/settings", "os/assistant", "intochat/customer-researcher"],
        options.Apps.Select(app => app.Package.ToString()));
    Assert.Equal(PackageId.Parse("intochat/settings"),
        options.Apps.Single(app => app.Package.ToString() == "os/settings").Replaces);
}

[Fact]
public void AMalformedManifestEntryFailsAtParseNamingTheValue()
{
    var error = Assert.ThrowsAny<Exception>(() => OsApp.Parse("os-settings"));
    Assert.Contains("os-settings", error.Message);
    var replaces = Assert.ThrowsAny<Exception>(() => OsApp.Parse("os/settings", "intochat settings"));
    Assert.Contains("intochat settings", replaces.Message);
}
```

- [ ] **Step 2: Run** `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit --filter OsShippedAppFacts` — Expected: FAIL (OsApp missing).

- [ ] **Step 3: Implement** in `OsApps.cs`:

```csharp
public sealed record OsApp(PackageId Package, PackageId? Replaces = null)
{
    public static OsApp Parse(string package, string? replaces = null)
    {
        try { return new(PackageId.Parse(package), replaces is null ? null : PackageId.Parse(replaces)); }
        catch (Exception error) { throw new FormatException($"Not a package id: '{replaces ?? package}' (entry '{package}').", error); }
    }
}

public sealed class OsAppsOptions
{
    public IList<OsApp> Apps { get; } = [];
}
```

(`Parse` must attribute the right value: parse `package` first in its own try if needed so the message names the actual offender — simplest is two small try blocks.) In `AddOperatingSystemApps` the manifest becomes:

```csharp
services.Configure<OsAppsOptions>(os =>
{
    os.Apps.Add(new(PackageId.Create("os", "settings"), PackageId.Parse("intochat/settings")));
    os.Apps.Add(new(PackageId.Create("os", "assistant")));
});
```

In `OsBootService`: `var apps = options.Value.Apps.ToArray();` (no parse), loop `foreach (var app in apps)`, pass `app` through; the existing tail block reads `app.Replaces` directly for now (its deletion is Task 2). Update `OsBootFacts` arrangements: `os.Apps.Add(OsApp.Parse(...))` / `os.Apps.Add(new(replacement, legacy))`.

- [ ] **Step 4: Run the suite** `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit` — Expected: PASS. Also `dotnet build src/DigitalBrain/OS/DigitalBrain.OS.Host` — Expected: 0 errors (Program.cs takes no manifest literals).

- [ ] **Step 5: Commit** `git add -A && git commit -m "Typed OS-app manifest: parse errors die where they are written"`

---

### Task 2: Adoption inside the Apps module

**Files:**
- Modify: `src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps.Contracts/Workspace/InstallApp.cs`
- Modify: `src/Modules/DigitalBrain/Apps/DigitalBrain.Modules.Apps/Workspace/App.cs` (Install path; read `CompleteUninstall` at :181-210 first — the adoption transfer mirrors its structure)
- Modify: Postgres table-owners machinery — locate `IPostgresTables` / `IPostgresStorageMigration` ("table-owners-v1") in the Postgres module; add a `Transfer` that re-points ownership rows to a new owner key without touching physical tables, mirroring `Retire`'s shape.
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS/OsApps.cs` (delete the tail block; pass `Adopts`)
- Test: `src/Modules/DigitalBrain/Apps/tests/DigitalBrain.Modules.Apps.Tests/Workspace/AppAdoptionFacts.cs` (new), `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsBootFacts.cs`

**Interfaces:**
- Consumes: `OsApp.Replaces` (Task 1), `IApp.Install`, `UninstallApp`, `AbandonAppStorage`, `IPostgresTables`.
- Produces: `InstallApp` gains `[property: Id(4)] PackageId? Adopts = null`. Contract: when `Adopts` names an app that is `Installed` in the same brain, a successful install (a) transfers the legacy app's Postgres table ownership to the installing app (physical names unchanged), (b) retires the legacy app leaving its storage behind (abandon-then-uninstall), (c) is idempotent — a repeat install/receipt replay with the legacy already gone is a no-op. A failing retire surfaces in the install's error **after** the new app is Installed-and-persisted, so the replacement survives (same contract the boot loop had).

- [ ] **Step 1: Write the failing facts** — `AppAdoptionFacts.cs`, real grains, modeled on `AppFacts`/`AppStorageFacts` arrangements (copy their install helpers):

```csharp
[Fact]
public async Task InstallingWithAdoptRetiresTheLegacyAppAndKeepsTheReplacementWhenReplayed()
{
    // Arrange: publish legacy intochat/hello and os/hello; install legacy.
    // Act: install os/hello with Adopts = intochat/hello.
    // Assert: os/hello Installed; legacy status is not Installed;
    // Act again: repeat the same Install request (receipt replay) — no throw, os/hello still Installed.
}

[Fact]
public async Task AdoptingWhenNoLegacyIsInstalledIsANoOp()
{
    // Install os/hello with Adopts = intochat/hello that was never installed.
    // Assert: os/hello Installed; legacy IApp reads NotInstalled.
}

[Fact]
public async Task AdoptionTransfersTableOwnershipWithoutRenamingTables()
{
    // Only if the Apps test host composes the Postgres fakes the existing storage facts use
    // (see AppStorageFacts): give the legacy app an owned table, adopt, assert the table's
    // owner row now names the new app key and the physical name is unchanged.
    // If AppStorageFacts shows table ownership is only exercised against real Postgres in E2E,
    // assert instead that Install with Adopts calls the transfer grain (RecordingXxx fake per
    // repo convention) and leave physical verification to the existing storage E2E.
}
```

- [ ] **Step 2: Run** `dotnet test src/Modules/DigitalBrain/Apps/tests/DigitalBrain.Modules.Apps.Tests --filter AppAdoptionFacts` — Expected: FAIL (`Adopts` missing).

- [ ] **Step 3: Implement.**
  - `InstallApp`: append `[property: Id(4)] PackageId? Adopts = null`.
  - In `App.Install`, after the install is persisted and deployed (find where `Snapshot` reaches `Installed` + receipt), append:

```csharp
if (request.Adopts is { } legacy)
{
    var legacyApp = GrainFactory.GetGrain<IApp>(BrainScope.CurrentId() + "/packages/" + legacy);
    if ((await legacyApp.Read()).Status == AppStatus.Installed)
    {
        var postgresAvailable = modules.ContractAssemblies().Contains(typeof(IPostgresTable).Assembly);
        if (postgresAvailable)
        {
            await GrainFactory.GetGrain<IPostgresStorageMigration>("table-owners-v1").Ensure();
            // Transfer every ownership row the legacy app and its storage files hold:
            // mirror CompleteUninstall's key construction, calling Transfer(newOwner) instead of Retire().
        }
        await legacyApp.AbandonStorage(new(Guid.NewGuid()));
        await legacyApp.Uninstall(new(Guid.NewGuid()));
    }
}
```

  Keep it **after** `Persist` of the installed snapshot so a throw here cannot unwind the replacement. The exact `Transfer` surface comes from reading the table-owners grain: add `Task Transfer(string newOwner)` beside `Retire()` with the same row-update shape.
  - `OsBootService.EnsureCurrentAsync`: delete the replaces tail block and the `replaces` parameter; the install call becomes `app.Install(new(Guid.NewGuid(), new(package, published), new Dictionary<string, string>(), osApp.Replaces))`. Note: adoption only fires on first install; an already-installed `os/settings` with a lingering legacy is handled because the boot loop re-sends `Install` only when NotInstalled — so ALSO keep a read-guarded retire for the already-installed case? **No**: instead, when the snapshot is already Installed and `osApp.Replaces` names an Installed legacy, call `app.Install` replay? Cleanest honest rule: the boot loop, when the replacement is already Installed and the legacy still Installed, invokes the same adoption through a dedicated `IApp.Adopt(AdoptLegacyApp request)`—**stop**: that's a new surface. Decide by test: extend `OsBootFacts.ALegacyAppIsUninstalledOnceItsReplacementIsInstalled` to the sequence "replacement installed first, legacy still present, next activation retires it". If `Install`'s receipt replay with `Adopts` set performs the adoption check on replay (make it do so: adoption runs even on the no-op replay path, guarded by the legacy read), the boot loop just always sends Install with `Adopts` — one surface, idempotent. Implement that: in the receipt-replay early-return branch of `Install`, run the adoption block before returning.
  - Update `OsBootFacts`: arrangements pass `new(replacement, legacy)`; assertions unchanged.

- [ ] **Step 4: Run** Apps suite + OS suite:
`dotnet test src/Modules/DigitalBrain/Apps/tests/DigitalBrain.Modules.Apps.Tests` and `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit` — Expected: PASS (Apps currently 106 + 3 new).

- [ ] **Step 5: Commit** `git add -A && git commit -m "Adoption is a step of Install: the Apps module owns package replacement"`

---

### Task 3: Settings preferences are the brain's

**Files:**
- Modify: `src/DigitalBrain/OS/Apps/Settings/app.cs`, `src/DigitalBrain/OS/Apps/Settings/tests.cs`
- Test: the package's own `tests.cs` is the gate; `OsShippedAppFacts` already pins spec+tests presence. Add the compile fact if OS lacks one (mirror IntoChat's `EveryShippedProgramAndTestsFileCompilesAgainstCurrentContracts` with `CSharpScriptCheck` over the `os` source).

**Interfaces:**
- Consumes: nothing new. Produces: preference neuron keys `{brain}/settings/display-name`, `{brain}/settings/theme` (stable across package identity).

- [ ] **Step 1: Write the failing scenario first** — in `tests.cs` add a scenario (stable id in `app.authoring.json` if present): install the app under one GUID-scoped package id, set a display name through the field, uninstall, install the same content under a *different* scratch package id, assert the read preference survives. Follow the suite's existing scratch-install helpers (`AppScenarioSuite`).

- [ ] **Step 2: Run the package tests RED** — through the existing test runner path used by `ShippedAppFacts`-style compile check won't execute; the executable gate runs in verification. Locally: `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit` plus the compile fact; the full scenario runs via `aspire run` smoke (Task 5) or the E2E journey. At minimum confirm the new scenario FAILS by reasoning is not allowed — run it: use the sandbox script runner the repo exposes for local package tests if available (`dotnet run tests.cs` against a dev host), otherwise defer RED/GREEN evidence to Task 5's smoke where verification must go green including the new scenario, and say so in the ledger.

- [ ] **Step 3: Implement** in `app.cs`:

```csharp
var appKey = brain.Setting("App")!;
var brainId = appKey[..appKey.IndexOf("/packages/", StringComparison.Ordinal)];
var displayName = brain.Get<ITextField>(brainId + "/settings/display-name");
var theme = brain.Get<ITextField>(brainId + "/settings/theme");
// layout references follow:
await main.Set(new("column", [new("textfield", brainId + "/settings/display-name"), new("textfield", brainId + "/settings/theme")]), ...);
```

Window/surface/layout neurons stay app-keyed (they are derived UI, re-created on open); only the two preference fields move.

- [ ] **Step 4: Run** `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit` (compile fact) — Expected: PASS.

- [ ] **Step 5: Commit** `git add -A && git commit -m "Settings preferences are the brain's, not the installation's"`

---

### Task 4: The composition wires its apps

**Files:**
- Modify: `src/Aspire/DigitalBrain.Aspire.Server/DigitalBrainServerBuilder.cs`, `DigitalBrainRuntimeHostingExtensions.cs`
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS/OsCatalog.cs`, `OsApps.cs` (idempotence guard)
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS.Host/Program.cs` (drop the explicit call)
- Test: `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsShippedAppFacts.cs`

**Interfaces:**
- Produces: `DigitalBrainServerBuilder.ConfigureServices(Action<IServiceCollection> configure)` — collected actions, applied to `builder.Services` inside `AddDigitalBrainServer` right after `configure(definition)`. `OsCatalog.AddOperatingSystem` calls `server.ConfigureServices(s => s.AddOperatingSystemApps())`. `AddOperatingSystemApps` adds manifest entries only for package ids not already present (idempotent under double registration).

- [ ] **Step 1: Write the failing facts**:

```csharp
[Fact]
public void TheOperatingSystemCompositionWiresItsAppsIntoTheHost()
{
    var builder = Host.CreateApplicationBuilder();
    builder.AddDigitalBrainServer(server => server.AddOperatingSystem());
    using var provider = builder.Services.BuildServiceProvider();
    Assert.NotNull(provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    Assert.Contains(provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps,
        app => app.Package.ToString() == "os/settings");
}

[Fact]
public void RegisteringTheOsAppsTwiceDoesNotDuplicateTheManifest()
{
    var services = new ServiceCollection();
    services.AddOperatingSystemApps();
    services.AddOperatingSystemApps();
    using var provider = services.BuildServiceProvider();
    Assert.Equal(2, provider.GetRequiredService<IOptions<OsAppsOptions>>().Value.Apps.Count);
}
```

(If `AddDigitalBrainServer` requires more host context than `Host.CreateApplicationBuilder()` provides, assert through a `DigitalBrainServerBuilder` + manual action application instead — the contract under test is "AddOperatingSystem queues the apps wiring".)

- [ ] **Step 2: Run** `--filter OsShippedAppFacts` — Expected: FAIL (`ConfigureServices` missing / duplicate manifest).

- [ ] **Step 3: Implement**:

```csharp
// DigitalBrainServerBuilder
private readonly List<Action<IServiceCollection>> _serviceActions = [];
public DigitalBrainServerBuilder ConfigureServices(Action<IServiceCollection> configure)
{ ArgumentNullException.ThrowIfNull(configure); _serviceActions.Add(configure); return this; }
internal void ApplyServiceActions(IServiceCollection services)
{ foreach (var action in _serviceActions) { action(services); } }

// DigitalBrainRuntimeHostingExtensions, right after configure(definition):
definition.ApplyServiceActions(builder.Services);

// OsCatalog.AddOperatingSystem, after the module loop:
server.ConfigureServices(services => services.AddOperatingSystemApps());

// OsApps.AddOperatingSystemApps manifest configure becomes idempotent:
services.Configure<OsAppsOptions>(os =>
{
    void Add(OsApp app) { if (!os.Apps.Any(existing => existing.Package == app.Package)) { os.Apps.Add(app); } }
    Add(new(PackageId.Create("os", "settings"), PackageId.Parse("intochat/settings")));
    Add(new(PackageId.Create("os", "assistant")));
});
```

Delete `builder.Services.AddOperatingSystemApps();` from `Program.cs`. Leave `ReferenceBrain`/E2E hosts untouched: they now get the wiring through `AddOperatingSystem()`; their existing `DigitalBrain__Apps__ShipOnStartup` env ("false" on private hosts) keeps startup shipping off where suites depend on it — check `ReferenceBrain.Create()` sets or inherits a value; if it has none, set `"DigitalBrain__Apps__ShipOnStartup": "false"` there explicitly with a comment pointing at the OS E2E golden journey as the place that ships.

- [ ] **Step 4: Run** OS suite + `dotnet build src/DigitalBrain/OS/DigitalBrain.OS.Host` + `dotnet test src/IntoChat/Tests/IntoChat.Tests.Unit` — Expected: PASS (BrandingFacts asserts Program.cs content only for Flutter; the dropped line doesn't affect it).

- [ ] **Step 5: Commit** `git add -A && git commit -m "The composition wires its own apps; a distribution cannot forget them"`

---

### Task 5: Debris, sweep, smoke

**Files:** `.gitignore`

- [ ] **Step 1:** In `.gitignore`, replace `src/IntoChat/IntoChat/applications/` with `src/DigitalBrain/OS/DigitalBrain.OS.Host/applications/`.
- [ ] **Step 2:** `rg -n "AddOperatingSystemApps|Replaces\[" src --glob '!**/bin/**' --glob '!**/obj/**'` — Expected: no call sites outside `OsCatalog`/`OsApps.cs`/facts; no dictionary-style `Replaces[` anywhere.
- [ ] **Step 3:** Run every touched suite: OS unit, Apps module, IntoChat unit. Expected: all PASS.
- [ ] **Step 4:** `aspire run` smoke from `src/IntoChat/IntoChat.AppHost`: all resources Healthy; `os/settings` + `os/assistant` ship green (Settings verification now includes the preference-survival scenario — this is Task 3's executable RED→GREEN evidence if it had none earlier); open Settings in the shell, set a display name, confirm it reads back.
- [ ] **Step 5:** Commit `git add -A && git commit -m "Sweep: lifecycle ownership follow-ups"` and push to update PR #135; re-report the five findings with outcomes.

---

## Self-review notes

- Spec coverage: principle 1 → Task 1; 2 → Task 2; 3 → Task 3; 4 → Task 4; 5 → Task 5. All five PR findings map: parse fault → T1; state loss → T2+T3; ordering test → T2 facts; bare 503 → T4; gitignore → T5.
- The one discovery risk is flagged inside Task 2 (exact `IPostgresTables` transfer surface and whether unit facts can exercise ownership rows); the task tells the implementer exactly which existing code to mirror and what the fallback assertion is.
- Task 3's RED evidence may only land at Task 5's smoke (sandbox-executed scenario); the task says to ledger that explicitly rather than fake a local run.
- Type consistency: `OsApp` (T1) consumed by T2's boot-loop change and T4's idempotence guard; `Adopts` (T2) consumed by T2's boot loop only; `ConfigureServices` (T4) consumed by `OsCatalog` only.
