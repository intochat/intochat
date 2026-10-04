# The OS is the product; one package form, one runtime

**Date:** 2026-10-04
**Status:** Ratified. Amends `2026-10-03-os-apps-and-creator-mode-design.md`: its "Project shape"
and step 4 (precompiled package runtime) are superseded; its problem statement, package-form
principle, and Creator-mode direction stand.

## What was wrong

Two assumptions in the 2026-10-03 spec generated nearly all of its complexity:

1. **The OS/distribution split had one occupant.** `DigitalBrain.OS` was drawn as a composition
   library whose runnability only a test fixture proved, while IntoChat — the sole "distribution" —
   carried a diff that is, in its entirety, two display strings and an apps folder. A two-layer
   architecture with one occupant is configuration wearing a project costume.
2. **"Baked into the OS" was conflated with "compiled differently."** The precompiled package
   runtime (spec step 4) existed to serve it. But the moment a user may fork an OS app in Creator
   mode, the original must be written against the same contracts surface the sandbox exposes —
   otherwise the fork breaks on first edit. A precompiled runtime can never let OS apps do
   anything a user script can't, so it buys only latency at the cost of a second runtime policed
   for fidelity forever. It is a cache pretending to be an architecture layer.

## The principle

**One form, one runtime, one gate. "OS app" is a distribution fact, not a kind.**

```
An OS app  =  an ordinary package (app.spec.md + tests.cs + behaviors/*.cs)
              whose folder lives in the OS repo,
              shipped by ShippedAppPublisher under the "os" publisher,
              pinned by OsBootService on every Activated.
Same sandbox runtime. Same verify gate. Fork = the fork that already exists.
```

Everything shipped into a brain is an app in package form; the OS itself is the runnable host
that composes the kernel, platform, modules, and ships its first-party apps. Even system surfaces
like compute-usage UI are packages a user can fork and modify (add a chart) — that forkability is
a product commitment, and it is what forbids a second runtime.

What "always present" actually requires already exists:

- `OsBootService` installs/upgrades OS apps on every `Activated` (at-least-once, read-before-write).
- `ShippedAppPublisher` embeds revisions at build time and publishes them at startup.

If OS-app latency ever measurably hurts, the fix is a transparent optimization of the same
scripts (warm pools, cached compilation, AOT of identical source) — never a second programming
model.

## Project shape

```
src/DigitalBrain/OS/           the runnable product. Program.cs composes kernel + platform +
                               module catalog; ships the OS apps. THE deployable binary.
src/DigitalBrain/OS/Apps/      Settings, Assistant (persona), Authoring, compute-usage UI, … —
                               ordinary packages, publisher "os".
src/IntoChat/ (or src/Apps/IntoChat/)
                               a vendor apps folder: CustomerResearcher, GroupChat, WordCount,
                               publisher "intochat" — the permanent in-tree third-party proof
                               that the public surface suffices, enforced by the existing
                               "shipped apps use only shipped contracts" checks.
appsettings / env              branding (DisplayName, AssistantTitle, …) — configuration, not code.
```

The distribution concept is deferred, not deleted: when a second distribution exists, extracting
a `Compose()` from a working host is a mechanical refactor. Building the layer speculatively was
the wrong-direction bet. `ReferenceBrainFixture` becomes "boot the product in-proc" — the OS E2E
fixture boots the same composition the deployed binary runs.

`DigitalBrain.Modules.Assistant` shrinks to the connector it always was: the `/agent` stream,
transcription, receipts/metering. Persona, prompts, and model-choice concerns live in the
`os/assistant` package. Modules remain connectors + kernel capabilities throughout.

## Two authoring origins, one mechanism

Authoring origin and execution are independent axes; only origin varies:

- **From code** (OS developer, IntoChat-the-vendor): the package is a folder in the repo. A
  *working-tree package source* syncs the filesystem into `IPackage` revisions — mount the folder,
  watch it, each save commits a draft revision and reinstalls it into the author's own brain.
  Edit Settings or CustomerResearcher in the IDE and watch its window re-dock: the shell
  re-derives the workspace wholesale, so the UI appears by itself when the draft's behaviors
  create neurons. No new UI mechanism.
- **At runtime** (users in Creator mode): the Author/Builder agents are the editor writing the
  same revisions. The Apps view shows the same live mirror — scenarios, behaviors, and the
  windows they created.

One mechanism, two editors. Draft revisions are installable only in the authoring brain, never
shareable or forkable, and visibly marked — the publish gate protects *other people*; your own
brain is your blast radius.

## Capability grants — the real hard problem

The runtime split was hiding the actual question: Settings writes brain configuration;
compute-usage reads platform metering; a user's fork must not inherit those powers.

- Privileged contracts exist as neurons the kernel guards; a package carries **grants**.
- OS-shipped packages receive their grants from the OS boot manifest.
- **A fork starts with only public-surface grants.** Fork compute-usage → you keep reading the
  public compute signals and can add your chart. Fork Settings → your copy renders but cannot
  write platform state until granted.

Grant design happens when the working-tree source makes Settings editable and forces it — not
speculatively. Fork-drops-grants is the rule from day one.

## Identity and migration

- Publisher for OS apps: **`os`**. Package ids change (`intochat/settings` → `os/settings`).
- Installed brains that know `intochat/settings` need a one-time migration (reinstall-as /
  alias at `OsBootService` ensure-time); the upgrade path must preserve app state and any
  file-owned Postgres tables per the existing upgrade rules (physical names and pinned origins
  unchanged).
- `BuiltInSettingsOptions` defaults to `os/settings`, set by the OS; a deployment may override
  via standard configuration precedence. No IntoChat literal remains.

## Sequencing

1. **OS becomes the runnable product.** IntoChat's host moves to `DigitalBrain.OS`; IntoChat
   shrinks to the vendor apps folder plus branding configuration. Mostly mechanical rename;
   collapses the three duplicated compositions into the host's own.
2. **OS app ownership moves.** Settings/Assistant/Authoring folders move under the OS, publisher
   `os`, pinned by `OsBootService`'s own manifest (a deployment only adds). Package-id migration
   ships here. Supersedes issue #134 as written; same acceptance intent: a fresh brain on the
   bare product gets Settings and Assistant via the `Activated` boot loop; `OsBootFacts`,
   `ShippedAppFacts`, `PathTruthFacts` updated; no new E2E suites.
3. **Working-tree package source.** Filesystem ↔ `IPackage` sync, draft installs, Apps-view live
   windows, hello-world package template (one scenario: "when the app opens, a window shows
   'Hello, world'" — onboarding, smoke test, and Creator-mode golden journey in one).
4. **Capability grants**, designed when step 3 forces it.

Steps 3 and 4 are their own design/plan cycles; this document fixes their direction only.

## Out of scope / deleted

- **Precompiled package runtime** — deleted, not deferred (was spec step 4).
- A `DigitalBrain.OS` composition library distinct from the host — not built until a second
  distribution exists.
- Cross-app subscription scopes beyond what grants require.
