# OS-app lifecycle: one owner per concern

**Date:** 2026-10-04
**Status:** Ratified. Extends `2026-10-04-os-as-product-one-package-runtime-design.md`; resolves
the five review findings on PR #135 as one structural change.

## What was wrong

The OS-app lifecycle was scattered across three owners, and "replace" was modeled as two
unrelated lifecycle events:

- a stringly manifest (`IList<string> Apps` + `IDictionary<string,string> Replaces`) parsed
  eagerly at service start — one typo faulted the whole boot service without naming the key;
- a tail block in `OsBootService` doing install-new then uninstall-old — ordering protected
  only by a comment, state preservation impossible from outside the Apps module;
- a helper (`AddOperatingSystemApps`) the distribution had to remember to call — forget it and
  a bare OS composition 503s on built-in settings.

## The principle

**Each concern lives with its natural owner.**

1. **The manifest is typed; parsing dies at configure time.**

   ```csharp
   public sealed record OsApp(PackageId Package, PackageId? Replaces = null)
   {
       public static OsApp Parse(string package, string? replaces = null);
   }
   public sealed class OsAppsOptions { public IList<OsApp> Apps { get; } }
   ```

   A malformed id throws where it is written, naming the value. `Replaces` rides its app entry.

2. **Replacement is adoption inside the Apps module — one grain call.** `InstallApp` gains an
   optional `Adopts` package id (`[Id(n)]` appended). After a successful deploy, the App grain
   transfers the legacy app's Postgres table ownership through the existing table-owners
   migration machinery (physical names and pinned origins unchanged) and retires the legacy
   app. Ordering is structural: adoption is a step of a successful install, not a sibling call.
   `OsBootService` passes `app.Replaces` into `Install` and its tail block is deleted. A legacy
   retire that throws must not unwind the installed replacement; the next activation retries
   the retire.

3. **Apps do not park durable user state under their installation identity.** The Settings app
   keys preferences by brain (`{brain}/settings/display-name`, `{brain}/settings/theme`), not by
   app key. Any rename, reinstall or fork of Settings keeps user preferences. No read-old-key
   fallback for the `intochat/settings` era: the one-time reset of dev-stage display-name/theme
   is accepted; keying by brain prevents all future occurrences.

4. **The composition owns its apps.** `AddOperatingSystem()` wires the OS apps itself —
   shipped source, manifest, boot loop, built-in settings default. A distribution cannot forget
   them; the bare composition (E2E reference host, module host) serves built-in settings. Hosts
   opt out of startup shipping with the existing `DigitalBrain:Apps:ShipOnStartup` selection.

5. Move debris: the stale `.gitignore` entry for the old host path is corrected.

## Acceptance

- A malformed replaces/package id fails at `OsApp.Parse` with the offending string in the
  message; the boot service never sees it.
- Apps-module facts (real grains): install-with-adopt transfers table ownership and retires the
  legacy app; a throwing retire leaves the replacement Installed; adopt with no legacy install
  is a no-op.
- Settings facts: preferences survive uninstall + reinstall under a different package id.
- The reference composition serves `POST …/built-in/settings/open` without 503.
- No new E2E suites; existing suites green.

## Out of scope

- Cross-package `Upgrade` (adoption covers the rename case).
- Capability grants (prior spec, step 4).
