# OS as Product (spec steps 1–2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The OS ships and pins Settings/Assistant itself under the `os` publisher; IntoChat shrinks to a vendor apps library; the deployable host becomes `DigitalBrain.OS.Host`.

**Architecture:** OS apps stay ordinary sandbox packages — only distribution changes. The `DigitalBrain.OS` assembly embeds the Settings/Assistant folders, publishes them via the existing `ShippedAppPublisher`, pre-populates `OsAppsOptions` with its own manifest, and `OsBootService` learns to uninstall a legacy package that a shipped app replaces. IntoChat's apps move into a small `IntoChat.Apps` class library. Branding becomes configuration. Finally the host project is physically renamed.

**Tech Stack:** .NET / Orleans grains, xUnit v3 (`ModuleTest.Create()`), Aspire AppHost.

**Spec:** `docs/superpowers/specs/2026-10-04-os-as-product-one-package-runtime-design.md`

## Global Constraints

- Publisher for OS apps is exactly `os`; package ids become `os/settings`, `os/assistant`.
- No precompiled runtime, no new E2E suites; coverage in `DigitalBrain.OS.Tests.Unit`, module unit suites, and existing journeys.
- Never build the `.slnx` (Windows handshake bug) — `dotnet build` / `dotnet test` per project only.
- Persisted grain state: new fields append `[Id(n)]`, never renumber; concrete arrays/dictionaries, no collection-expression `IReadOnlyList` wrappers.
- Tests: sentence-shaped names, `TestContext.Current.CancellationToken`, real grains (`ModuleTest.Create().WithModule<AppsModule>()`), no mocks of grains.
- No self-calls on non-reentrant grains; `OsBootService` is a hosted service, not a grain — unchanged pattern.
- Each task ends with its suite green and a commit ending in `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.

## Review Focus

1. A distribution's `Configure<BuiltInSettingsOptions>` registered *after* `AddOperatingSystemApps` must win over the OS default (standard options ordering) — pinned by test in Task 1.
2. `Replaces` when the legacy app was never installed must be a silent no-op, not an error — pinned in Task 2.
3. A failing legacy uninstall must not prevent the replacement app from being ensured on this or later activations — pinned in Task 2 (uninstall runs after ensure, inside the per-app catch).
4. `os/assistant` not yet published (publisher startup race) must skip quietly and be picked up on the next activation — already the `Published is null` early-return in `EnsureCurrentAsync`; extend `OsBootFacts` assertion in Task 1 to cover two apps where one publishes late.
5. The bare OS composition must serve `POST …/built-in/settings/open` without the 503 "no built-in settings package configured" — the OS default in Task 1 removes the only unconfigured case; `BuiltInAppRoutesFacts` keeps the route pinned after the id flip in Task 3.

---

### Task 1: The OS ships Settings and Assistant under publisher `os`

**Files:**
- Move (git mv): `src/IntoChat/Apps/Settings` → `src/DigitalBrain/OS/Apps/Settings`; `src/IntoChat/Apps/Assistant` → `src/DigitalBrain/OS/Apps/Assistant`
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS/DigitalBrain.OS.csproj` (embed the folders), `src/DigitalBrain/OS/DigitalBrain.OS/OsApps.cs`
- Test: `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsShippedAppFacts.cs` (new), `OsBootFacts.cs`

**Interfaces:**
- Consumes: `AddShippedApps(IServiceCollection, Assembly, string resourcePrefix, string publisher)`, `EmbeddedShippedAppSource`, `OsAppsOptions`, `BuiltInSettingsOptions` (all existing, from `DigitalBrain.Modules.Apps`).
- Produces: `public static IServiceCollection AddOperatingSystemApps(this IServiceCollection services, Action<OsAppsOptions>? configure = null)` in `DigitalBrain.OS.OsAppsHosting` — registers shipped source (publisher `os`), defaults `OsAppsOptions.Apps = ["os/settings", "os/assistant"]`, defaults `BuiltInSettingsOptions.Package = os/settings`, and calls `AddOperatingSystemBoot`. Tasks 3–4 call this from the host.

- [ ] **Step 1: Move the folders and embed them**

```bash
git mv src/IntoChat/Apps/Settings src/DigitalBrain/OS/Apps/Settings
git mv src/IntoChat/Apps/Assistant src/DigitalBrain/OS/Apps/Assistant
```

In `DigitalBrain.OS.csproj` add (same shape as IntoChat's):

```xml
<ItemGroup>
  <EmbeddedResource Include="../Apps/**/*" LogicalName="DigitalBrain.OS.Apps/%(RecursiveDir)%(Filename)%(Extension)" Link="Apps/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

(`DigitalBrain.OS.csproj` already references the Apps module project, which carries `AddShippedApps`.)

- [ ] **Step 2: Write the failing facts**

`src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsShippedAppFacts.cs`:

```csharp
using DigitalBrain.Apps;
using DigitalBrain.OS;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DigitalBrain.OS.Tests.Unit;

public class OsShippedAppFacts
{
    static readonly EmbeddedShippedAppSource Source =
        new(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", "os");

    [Fact]
    public void TheOperatingSystemShipsSettingsAndAssistant()
    {
        Assert.Equal(["assistant", "settings"], Source.Load().Select(app => app.Package.Name).Order());
        Assert.All(Source.Load(), app => Assert.Equal("os", app.Package.Owner));
    }

    [Fact]
    public void AddOperatingSystemAppsNamesItsOwnManifestAndADeploymentOnlyAdds()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps(os => os.Apps.Add("intochat/customer-researcher"));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<OsAppsOptions>>().Value;
        Assert.Equal(["os/settings", "os/assistant", "intochat/customer-researcher"], options.Apps);
        Assert.Equal(PackageId.Create("os", "settings"),
            provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    }

    [Fact]
    public void ADistributionRegisteredAfterTheOsOverridesTheBuiltInSettingsPackage()
    {
        var services = new ServiceCollection();
        services.AddOperatingSystemApps();
        services.Configure<BuiltInSettingsOptions>(o => o.Package = PackageId.Create("acme", "settings"));
        using var provider = services.BuildServiceProvider();
        Assert.Equal(PackageId.Create("acme", "settings"),
            provider.GetRequiredService<IOptions<BuiltInSettingsOptions>>().Value.Package);
    }
}
```

(If `EmbeddedShippedAppSource` or `BuiltInSettingsOptions` is internal to the Apps module, follow whatever access route `src/IntoChat/Tests/IntoChat.Tests.Unit/ShippedApps/ShippedAppFacts.cs` uses today — it constructs `EmbeddedShippedAppSource` directly, so public access exists.)

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit --filter OsShippedAppFacts`
Expected: FAIL — `AddOperatingSystemApps` does not exist.

- [ ] **Step 4: Implement `AddOperatingSystemApps`**

In `OsApps.cs`, extend `OsAppsHosting`:

```csharp
public static IServiceCollection AddOperatingSystemApps(this IServiceCollection services, Action<OsAppsOptions>? configure = null)
{
    ArgumentNullException.ThrowIfNull(services);
    services.AddShippedApps(typeof(OsCatalog).Assembly, "DigitalBrain.OS.Apps/", publisher: "os");
    services.Configure<OsAppsOptions>(os =>
    {
        os.Apps.Add("os/settings");
        os.Apps.Add("os/assistant");
        os.Replaces["os/settings"] = "intochat/settings";
        os.Replaces["os/assistant"] = "intochat/assistant";
    });
    services.Configure<BuiltInSettingsOptions>(options => options.Package ??= PackageId.Create("os", "settings"));
    return services.AddOperatingSystemBoot(configure);
}
```

Note the `??=`: the OS sets the default only when nothing else has; combined with ordinary `Configure` ordering this satisfies the override fact. (`Replaces` arrives in Task 2 — for this task's commit either add the empty property now, shown in Task 2 Step 1, or add the two `Replaces` lines in Task 2. Prefer adding the property now so this file compiles once.)

- [ ] **Step 5: Extend `OsBootFacts` for two apps with one late publisher**

Add a fact alongside the existing one (same helpers/style as `EstablishingABrainInstallsTheOsAppsAndReactivationUpgradesThem`):

```csharp
[Fact]
public async Task AnUnpublishedOsAppIsSkippedAndPickedUpOnTheNextActivation()
{
    // Arrange two configured apps, publish only the first, establish the brain:
    // assert the first installs and the second stays NotInstalled without faulting the loop.
    // Then publish the second, deactivate + reactivate the brain, and wait for its install.
}
```

Flesh it out by copying the existing fact's arrangement verbatim (ModuleTest, `AddOperatingSystemBoot(os => { os.Apps.Add("os/hello"); os.Apps.Add("os/later"); })`, commit+publish only `os/hello`, establish `acct/home`, assert, then publish `os/later`, reactivate, wait).

- [ ] **Step 6: Run the suite**

Run: `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "OS ships Settings and Assistant under the os publisher"
```

---

### Task 2: `OsBootService` replaces legacy packages

**Files:**
- Modify: `src/DigitalBrain/OS/DigitalBrain.OS/OsApps.cs`
- Test: `src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit/OsBootFacts.cs`

**Interfaces:**
- Consumes: `IApp.Uninstall(UninstallApp)` (exists: `IApp.cs:17`), `AppStatus`.
- Produces: `OsAppsOptions.Replaces` — `public IDictionary<string, string> Replaces { get; } = new Dictionary<string, string>();` mapping **new package id → legacy package id**. After a new app is ensured Installed, the boot loop uninstalls the legacy app if it is Installed.

- [ ] **Step 1: Write the failing facts**

In `OsBootFacts.cs` (reuse the existing fact's arrangement helpers):

```csharp
[Fact]
public async Task ALegacyAppIsUninstalledOnceItsReplacementIsInstalled()
{
    // Arrange: publish legacy "intochat/hello" and new "os/hello";
    // install intochat/hello into the brain (as the old distribution would have);
    // configure AddOperatingSystemBoot(os => { os.Apps.Add("os/hello"); os.Replaces["os/hello"] = "intochat/hello"; });
    // Act: establish/reactivate the brain.
    // Assert: os/hello reaches Installed AND intochat/hello reaches a non-Installed status.
}

[Fact]
public async Task AReplacementWithNoLegacyInstallIsANoOp()
{
    // Same configuration, but never install intochat/hello.
    // Assert: os/hello installs; reading the legacy IApp still says NotInstalled; no warning-level faults required.
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit --filter OsBootFacts`
Expected: FAIL — `Replaces` does not exist.

- [ ] **Step 3: Implement**

In `OsAppsOptions`:

```csharp
public IDictionary<string, string> Replaces { get; } = new Dictionary<string, string>();
```

In `OsBootService.ExecuteAsync`, resolve the map once next to `apps`:

```csharp
var replaces = options.Value.Replaces.ToDictionary(
    pair => PackageId.Parse(pair.Key), pair => PackageId.Parse(pair.Value));
```

At the end of `EnsureCurrentAsync` (so it runs inside the existing per-app catch, after the new app is Installed/current), pass the map in and add:

```csharp
if (replaces.TryGetValue(package, out var legacy))
{
    var legacyApp = grains.GetGrain<IApp>(activated.BrainId + "/packages/" + legacy);
    if ((await legacyApp.Read().WaitAsync(cancellationToken)).Status == AppStatus.Installed)
    {
        await legacyApp.Uninstall(new(Guid.NewGuid())).WaitAsync(cancellationToken);
    }
}
```

Read-before-write keeps it idempotent across at-least-once `Activated` deliveries; a thrown uninstall lands in the existing warning catch and retries next activation while the replacement is already ensured.

- [ ] **Step 4: Run the suite**

Run: `dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Boot loop uninstalls a legacy package once its OS replacement is installed"
```

---

### Task 3: IntoChat becomes a vendor apps library; the host consumes the OS manifest

**Files:**
- Create: `src/IntoChat/IntoChat.Apps/IntoChat.Apps.csproj`, `src/IntoChat/IntoChat.Apps/IntoChatApps.cs`
- Modify: `src/IntoChat/IntoChat/Program.cs`, `src/IntoChat/IntoChat/IntoChat.csproj`
- Modify tests: `src/IntoChat/Tests/IntoChat.Tests.Unit/ShippedApps/ShippedAppFacts.cs`, `src/IntoChat/Tests/IntoChat.Tests.E2E/IntoChatHostFixture.cs`, `src/IntoChat/Tests/IntoChat.Tests.E2E/Apps/BuiltInAppRoutesFacts.cs`

**Interfaces:**
- Consumes: `AddOperatingSystemApps` (Task 1), `AddShippedApps`.
- Produces: `IntoChat.Apps` class library whose assembly embeds `src/IntoChat/Apps/**` under `IntoChat.ShippedApps/`, with anchor type `public static class IntoChatApps { }` (namespace `IntoChat`). Task 4's host keeps referencing it.

- [ ] **Step 1: Create the vendor library**

`IntoChat.Apps.csproj` (classlib, net floor matching sibling projects — copy `<TargetFramework>` from `DigitalBrain.OS.csproj`; reference the Apps module project the same way `DigitalBrain.OS.csproj` does):

```xml
<ItemGroup>
  <EmbeddedResource Include="../Apps/**/*" LogicalName="IntoChat.ShippedApps/%(RecursiveDir)%(Filename)%(Extension)" Link="Apps/%(RecursiveDir)%(Filename)%(Extension)" />
</ItemGroup>
```

`IntoChatApps.cs`:

```csharp
namespace IntoChat;

// Anchor for the embedded vendor packages; the host ships them with
// AddShippedApps(typeof(IntoChatApps).Assembly, "IntoChat.ShippedApps/", publisher: "intochat").
public static class IntoChatApps;
```

- [ ] **Step 2: Rewire the host**

In `Program.cs`, the shipped/boot/settings lines become:

```csharp
builder.Services.AddOperatingSystemApps();
builder.Services.AddShippedApps(typeof(IntoChat.IntoChatApps).Assembly, "IntoChat.ShippedApps/", publisher: "intochat");
```

Delete the `AddOperatingSystemBoot(os => os.Apps.Add("intochat/settings"))` and `Configure<BuiltInSettingsOptions>` lines. Remove the `<EmbeddedResource>` item from `IntoChat.csproj`; add a project reference to `IntoChat.Apps`.

- [ ] **Step 3: Update the facts to the new truth**

`ShippedAppFacts.cs`: source becomes `new EmbeddedShippedAppSource(typeof(IntoChat.IntoChatApps).Assembly, "IntoChat.ShippedApps/", "intochat")`; expected names become `["customer-researcher", "group-chat", "word-count"]` (the customer-researcher program assertions stay).

`IntoChatHostFixture.cs:9`: `ShippedPackages = ["os/settings", "os/assistant", "intochat/customer-researcher"]`.

`BuiltInAppRoutesFacts.cs`: every `intochat/settings` → `os/settings` (lines 13, 16, 37; `intochat/customer-researcher` stays).

Also sweep: `rg -l "intochat/settings" src docs` — the two Postgres/Supabase test literals reference `intochat/customer-researcher` only and stay; anything else surfacing `intochat/settings` in `src` flips to `os/settings`.

- [ ] **Step 4: Build and run the unit suites**

```bash
dotnet build src/IntoChat/IntoChat
dotnet test src/IntoChat/Tests/IntoChat.Tests.Unit
dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit
```

Expected: PASS. (E2E fixture edits compile; the E2E suite itself runs in Task 5's smoke.)

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "IntoChat shrinks to a vendor apps library; the host takes the OS manifest"
```

---

### Task 4: The host becomes DigitalBrain.OS.Host; branding becomes configuration

**Files:**
- Move: `src/IntoChat/IntoChat/**` → `src/DigitalBrain/OS/DigitalBrain.OS.Host/**` (git mv; csproj renamed `DigitalBrain.OS.Host.csproj`)
- Modify: the moved csproj (`AssemblyName`/`RootNamespace` → `DigitalBrain.OS.Host`), `Program.cs`, `appsettings.json`, `Dockerfile`, `Properties/PublishProfiles/Container.pubxml`
- Modify: `src/IntoChat/IntoChat.AppHost/AppHost.cs` + `IntoChat.AppHost.csproj` (project reference and linked `IntoChatConfiguration.cs` path)
- Modify: `src/IntoChat/Tests/IntoChat.Tests.Unit/PathTruthFacts.cs`, both test csprojs' project-reference paths, `InternalsVisibleTo` values

**Interfaces:**
- Consumes: everything as wired in Task 3.
- Produces: deployable project `DigitalBrain.OS.Host` (assembly `DigitalBrain.OS.Host`); the Aspire resource keeps its `"IntoChat"` resource name and port 5080 so dashboards/deployment stay stable — the brand is configuration, the binary is the OS.

- [ ] **Step 1: Move and rename the project**

```bash
git mv src/IntoChat/IntoChat src/DigitalBrain/OS/DigitalBrain.OS.Host
git mv src/DigitalBrain/OS/DigitalBrain.OS.Host/IntoChat.csproj src/DigitalBrain/OS/DigitalBrain.OS.Host/DigitalBrain.OS.Host.csproj
```

In the csproj set `<AssemblyName>DigitalBrain.OS.Host</AssemblyName>` and `<RootNamespace>DigitalBrain.OS.Host</RootNamespace>`; fix all relative `<ProjectReference>` paths for the new depth; change `InternalsVisibleTo` values only if the test projects actually use internals (check with `rg "internal" --files-with-matches` on the moved project first; if nothing is consumed, delete the attributes).

- [ ] **Step 2: Branding moves to configuration**

Delete from `Program.cs`:

```csharp
builder.Services.Configure<AssistantOptions>(options => { options.DisplayName = "IntoChat assistant"; options.Instructions = "..."; });
builder.Services.Configure<DigitalBrain.Flutter.FlutterModuleOptions>(options => options.AssistantTitle = "IntoChat");
```

Add to the host's `appsettings.json` (module options bind under `DigitalBrain:Modules:{Name}:Options:{Property}`; module ids per `OsCatalog`: `assistant`, `flutter`):

```json
"DigitalBrain": {
  "Modules": {
    "assistant": { "Options": {
      "DisplayName": "IntoChat assistant",
      "Instructions": "You are the IntoChat workspace assistant. Customer Researcher stores results in Postgres public.customer_research; filter its workspace column to the current workspace id."
    } },
    "flutter": { "Options": { "AssistantTitle": "IntoChat" } }
  }
}
```

If after this step a smoke run shows the defaults instead of the IntoChat strings, the binding path is wrong — read how `IModule<TOptions>` composes keys (search `Modules:` under `src/DigitalBrain/Kernel`) and fix the casing/segment; do not reintroduce the `Configure` calls.

- [ ] **Step 3: Update AppHost, Dockerfile, publish profile, PathTruthFacts**

- `AppHost.cs`: `AddProject<Projects.IntoChat>("IntoChat")` → `AddProject<Projects.DigitalBrain_OS_Host>("IntoChat")`.
- `IntoChat.AppHost.csproj`: project-reference path → `../../DigitalBrain/OS/DigitalBrain.OS.Host/DigitalBrain.OS.Host.csproj`; the linked `IntoChatConfiguration.cs` compile item path likewise.
- `Dockerfile` + `Container.pubxml`: project path and assembly/dll name `IntoChat.dll` → `DigitalBrain.OS.Host.dll`; keep every `DigitalBrain__Modules__*__Enabled` pair intact.
- `PathTruthFacts.cs`: `IntoChat.deps.json` → `DigitalBrain.OS.Host.deps.json`; Dockerfile path → the moved location. Run this fact first — it is the designated tripwire for everything this step touches.
- Test csprojs under `src/IntoChat/Tests/**`: fix project-reference paths to the moved host.

- [ ] **Step 4: Build everything touched**

```bash
dotnet build src/DigitalBrain/OS/DigitalBrain.OS.Host
dotnet build src/IntoChat/IntoChat.AppHost
dotnet test src/IntoChat/Tests/IntoChat.Tests.Unit
```

Expected: PASS, including `PathTruthFacts`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "The deployable host is DigitalBrain.OS.Host; IntoChat branding is configuration"
```

---

### Task 5: Verification sweep

**Files:** none new — this task runs the truth checks the repo already owns.

- [ ] **Step 1: Straggler grep**

```bash
rg -n "intochat/settings|intochat/assistant" src
```

Expected: zero hits (docs may still mention the migration). Any hit gets fixed in place.

- [ ] **Step 2: Unit suites for every touched module**

```bash
dotnet test src/DigitalBrain/OS/tests/DigitalBrain.OS.Tests.Unit
dotnet test src/IntoChat/Tests/IntoChat.Tests.Unit
dotnet test src/Modules/DigitalBrain/Apps/tests/DigitalBrain.Modules.Apps.Tests
```

Expected: all PASS.

- [ ] **Step 3: Flutter shell checks** (wire ids appear in `customer_researcher_window_test.dart`; settings id may appear in shell code)

```bash
rg -n "intochat/settings" src/Modules/Google/Flutter/app/shell
```

Fix any hit to `os/settings`, then from `src/Modules/Google/Flutter/app/shell`: `flutter analyze` and `flutter test`. Expected: clean.

- [ ] **Step 4: Aspire smoke**

From `src/IntoChat/IntoChat.AppHost`: `aspire run`, wait for all resources Healthy; confirm in logs that `ShippedAppPublisher` published `os/settings`, `os/assistant`, and the three `intochat/*` apps, and that opening Settings in the shell works (exercises `BuiltInSettingsEndpoints` with the OS default).

- [ ] **Step 5: Final commit if the sweep changed anything**

```bash
git add -A
git commit -m "Sweep: os publisher ids everywhere the wire names them"
```

---

## Self-review notes

- Spec coverage: step 1 (runnable product) = Task 4; step 2 (ownership move, manifest, `BuiltInSettingsOptions` default, migration, facts) = Tasks 1–3; steps 3–4 of the spec are explicitly separate cycles.
- The legacy migration is uninstall-only. The Settings app's durable state is preference-shaped and the built-in settings endpoint re-installs on demand; if during Task 2 the E2E journeys reveal user-visible state lost by uninstall+reinstall, stop and surface it — a state-carrying migration is a design change, not a task tweak.
- `OsAppsOptions.Replaces` is options-level, not persisted grain state — no `[Id]` concerns.
- Type consistency: `AddOperatingSystemApps` (Task 1) is what Tasks 3–4 call; `IntoChatApps` anchor (Task 3) is what Task 3's facts and `Program.cs` reference; `Replaces` (Task 2) is seeded inside `AddOperatingSystemApps` (Task 1 notes the ordering).
