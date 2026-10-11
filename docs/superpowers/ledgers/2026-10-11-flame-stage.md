# SDD ledger — plan: docs/superpowers/plans/2026-10-11-flame-stage.md

Ledger of record for the Flame stage plan (spec `docs/superpowers/specs/2026-10-11-flame-stage-design.md`).
Appended and committed as an explicit file with the work. Scratch (briefs, reports, review packages) lives in the
git-ignored `.superpowers/sdd/2026-10-11-flame-stage/`. Implementers on Opus (user's instruction 2026-10-11).

## Preflight rulings

- Ruling F1: the branch `flame-stage` cuts from `os-runtime` HEAD (3efc136fb), not from `master` — master is 556 commits behind and has no `features/`, no Circles module and none of the kernel the plan builds on; spec D7's "from master" named the mainline, which os-runtime is in practice — cost if wrong: a rebase onto master when os-runtime lands there.
- Ruling F2: the owner stage alias key `{brain}/stage/owner` (plan Task 9 Step 3) is implemented in Task 4 from the start, with the fact `TheOwnersStageIsAddressableAsBrainSlashStageSlashOwner`, and Task 5's `FlameEndpoints.Stage` maps a caller whose account is the brain's owner to it; Task 9 then only consumes it — a later task amending an earlier task's neuron is a review-package smell — cost if wrong: one extra fact in Task 4.
- Ruling F3: a `DigitalBrain.Modules.Flame` project folder under vendor `DigitalBrain` (plan Global Constraints) stands over the spec's shorter path — `RepositoryLayoutFacts` pins vendor folders — cost if wrong: none (paths only).

## Preflight conflict scan

| Pair / task | Produces vs consumes | Finding |
|---|---|---|
| T1 → T2, T3, T4, T5, T9, T10 | record positional orders, `IScene.Report`, `IStage.SetHome` | consistent in the plan text (self-review note); no conflict |
| T2 ↔ T3 | `SceneNeuron` stubs `Fire`/`Report` in T2, filled in T3 | by design; T2's fact count (6) excludes Fire/Report |
| T4 ↔ T5 ↔ T9 | stage key: digest `ScopeOf(caller)` vs owner alias | resolved by F2 (alias lands in T4; T5 maps owner callers; T9 consumes) |
| T3 | `Publish` base method may not exist | plan gives the `Save(Snapshot, signal)` fallback; implementer records which |
| T5 | `UiHttp` may be `internal` to the Flutter module | plan gives the copy-the-helpers fallback |
| T6 → T7, T8 | `NodeComponent`, `SceneGame.componentFor/onInput`, `SceneClient` surface | T7/T8 consume exactly the names T6 produces |
| T6 | `renderer_registry` kind lists are pinned by existing gallery tests | plan says update their expected list; not a conflict |
| T7 | `BrainSnapshot.fromJson` field names | plan says adjust the test JSON to the parser |
| T9 | `AppDispatched` may not exist | plan gives the `Routed` fallback |
| T9 ↔ T10 | both add an OS default to `appsettings.json` | sequential edits of one list; no conflict |
| T8 | stage entry whose scene read 404s | handled in T8 Step 3 (drop entry, render Home) |
| each task | tests vs code in its own text | T2 asserts "6 pass" then T3 "14", T4 "20", T5 "22": 6+5+3(access)=14, +6=20, +2=22 — consistent |

Scan clean apart from F2.

## Task log
