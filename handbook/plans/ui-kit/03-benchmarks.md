# UK-3 · Benchmarks & Comparative Synthesis

> **Linear Issue**: `INT-1351` (`UK-3`) · **Project**: `P-INT-7` (`UI Kit`)
> **Benchmarked Systems**:
> 1. `Microsoft.UI.Reactor` (terse fluent C# MVU/DSL)
> 2. `Microsoft.Accordant` (`E:\projects\accordant` — executable specifications, `Choose.Each` Cartesian choice coordinates, `[Invariant]`, state-space exploration & DOT visualization)
> 3. **macOS Sonoma System Settings & Finder** (sidebar + grouped row panels + instant toggle save + Quick View inspector)
> 4. **Linear** (keyboard-first palette, `1..9` numeric pickers, hairline density, sub-50ms optimistic responsiveness)
> 5. **Shadcn/UI & Radix Primitives** (headless behavior vs token styling separation, composable slot anatomy, ARIA/roving-tabindex contracts)

---

## 1. Executive Synthesis Matrix

| Benchmark | What IntoChat Steals Directly | What IntoChat Explicitly Rejects | Where It Lands in UI Kit (`P-INT-7`) |
| :--- | :--- | :--- | :--- |
| **`Microsoft.UI.Reactor`** | `using static DigitalBrain.UI.Kit;` terse C# 12 collection-expression builders (`VStack([ ... ])`), expression-tree `.OnClick(s => s.Cmd())`, caller-expression deterministic auto-keys | WinUI 3 COM visual-tree coupling; React `UseState` / `UseEffect` / `UseRef` local closures that hide state from persistence, MCP, and replay | `UK-0` / `UK-7` (`DigitalBrain.UI.Kit` + `Screen<TState>`) |
| **`Microsoft.Accordant`** (`E:\projects\accordant`) | `Choose.Each` Cartesian choice coordinates (`ChoiceSpaceCoordinate`), `SameState` rejected self-loops vs forward state transitions, `[Invariant]` assertions on every reachable state, unfolded state-graph visualization + scenario path generation | Writing a *second* parallel `Spec<TState>` wrapper class (`BankAccountTests.cs:41-156`)—because our Rich DDD Aggregate `TState` **already is** a pure in-memory oracle! | `UK-0` / `UK-8` (`AggregateExplorer<TState>`) & `UK-15` (`StateGraphView`) |
| **macOS Sonoma Settings & Finder** | `248px` / `216px` `surfaceSunk` sidebars, `Panel` cards with `1px` inset hairlines, instant switch/select persistence (`InlineSave` `Saved ✓`—no page-level Save button), `BreadcrumbBar` with overflow ellipsis, List/Grid toggle + collapsible `280px` Quick View inspector | Translucent vibrancy inside window content areas (`DESIGN.md` §5.2 keeps windows `100%` opaque) | `UK-9` (`Panel`, `ResponsiveSplit`), `UK-11` (`BreadcrumbBar`, `InspectorDrawer`), `UK-22` (`Settings`), `UK-23` (`Files`) |
| **Linear** | `Ctrl/Cmd+K` + `/` palette, `1..9` single-key option selection (`PromptKeys`), `36px` row density, `1px` high-contrast hairlines, zero-jank view transitions, local text-buffer typing with ordered command commit | Monochrome developer-tool coldness (`#000` / `#FFF` stark contrast) and developer jargon (`issue`, `triage`, `webhook`) in consumer surfaces | `UK-1` / `UK-5` (Warm Ink & Paper), `UK-10` (`SearchField`), `UK-12` (`CommandPalette`), `UK-16` (`PromptOptions`) |
| **Shadcn/UI & Radix** | Strict split between **headless behavior** (focus trap, `Esc` dismiss, roving arrow tabindex, `Semantics`) and **Warm Ink & Paper token skin**; small composable primitives + `Slot` instead of 30-prop mega-widgets; declarative `Form` + `Rule` validation | Copy-paste per-app source duplication (IntoChat centralizes rendering in `os/ui` so all C# apps & Ino-authored units upgrade together) | `UK-4` (Catalogue), `UK-6` (`Slot`), `UK-10` (`Form`), `UK-12` (`Modal`, `Popover`, `DropdownMenu`) |

---

## 2. Deep-Dive 1: `Microsoft.UI.Reactor` -> `DigitalBrain.UI.Kit` (`UK-7`)

### 2.1 Actionable Takeaways
1. **Zero-Ceremony C# Tree Authoring**:
   - Replace verbose `new Surface([ new Row([ new Button("rearm", "Re-arm", new Emit("btcalert", "btcalert.rearm"), Enabled: !s.Armed) ]) ])` with fluent static builders + C# 12 collection expressions:
     ```csharp
     using static DigitalBrain.UI.Kit;

     protected override Node View(BtcAlert s) =>
         Card("Bitcoin Spot Monitor", subtitle: "Coinbase Spot · Every 1m", [
             HStack(gap: 12, [
                 MetricTile("BTC / USD", s.LastPrice == 0m ? "—" : $"${s.LastPrice:N2}"),
                 MetricTile("Alert Below", $"${s.Threshold:N0}"),
                 StatusPill(s.Armed ? "Armed" : "Triggered", s.Armed ? Tone.Accent : Tone.Working),
             ]),
             NumberField("Alert Threshold (USD)", s.Threshold)
                 .OnSubmit(x => x.SetThreshold(Arg.Decimal)),
             PrimaryButton("Re-arm Alert")
                 .OnClick(x => x.Rearm())
                 .Guard(s.CanRearm()),
         ]);
     ```
2. **Expression-Tree Command Binding & Deterministic Auto-Keys**:
   - `.OnClick(a => a.Withdraw(30m))` inspects the `LambdaExpression` (`MethodCallExpression`), extracts command method `Withdraw` and constant/captured arguments `(30m)`, generates a deterministic wire key (`"cmd:Withdraw:30"`), and registers the typed server-side activator on `Screen<TState>`—eliminating manual string keys, manual `[Signal]` command wrapper boilerplate, and `JsonElement` parsing.
3. **Shared `Rule` Guard Contract (`.Guard(Rule)`)**:
   - `.Guard(a.SufficientFunds(30m))` evaluates `Rule.Broken` at render time: when broken, it sets `Enabled = false` **and** populates `DisabledReason = rule.Message` (`"Insufficient funds: balance $0.00 < $30.00"`), which Flutter surfaces as a hover/focus `Tooltip` and MCP `screen_read` exposes to AI agents!

### 2.2 Why We Reject React Hooks (`UseState` / `UseEffect`)
- In IntoChat OS, every app and unit is a **distributed, journaled Neuron** inspectable by **Ino** (via MCP `screen_read` / `screen_act`), **Creator** (time-travel trace replay), and **Accordant** (`AggregateExplorer<TState>`). Anonymous closure state inside `Render()` breaks serialization, replay, and formal verification. All state lives on the immutable `[State]` DDD Aggregate `TState` (pure UI transitions return `TState`; persisted domain transitions return `Decision<TState>`).

---

## 3. Deep-Dive 2: `Microsoft.Accordant` (`E:\projects\accordant`) -> `AggregateExplorer<TState>` (`UK-8`, `UK-15`)

### 3.1 How `Microsoft.Accordant` Works (Inspected Source Analysis)
From `E:\projects\accordant\Accordant.Choose\Choose.cs`, `Accordant.Invariant\Invariant.cs`, and `Samples\BankAccount\BankAccount.Api.Tests\BankAccountTests.cs`:
1. **Cartesian Choice Coordinates (`Choose.cs`)**:
   - `ChoiceSpaceCoordinate` drives deterministic breadth-first / bounded-depth enumeration across nondeterministic or parameterized inputs (`Choose.Value(10m, 50m, 200m)`): `NextCoordinate()` advances the rightmost unexhausted `ChoicePoint` and resets trailing dimensions—exploring every combination without random fuzzing flakiness.
2. **Explicit `SameState` vs `ThenState` Semantics (`BankAccountTests.cs:48-154`)**:
   - Every operation explicitly distinguishes **precondition rejection** (`Expect.That(...).SameState()` — e.g., `409 Conflict` on duplicate create or `400 Bad Request` when `balance < request.Amount`) from **accepted state transition** (`Expect.That(...).ThenState(...)`).
   - In the state graph (`VisualizeStateSpace()`, `BankAccountTests.cs:344-389`), rejected commands appear as **red/dashed `SameState` self-loops** proving the guard held, while accepted commands appear as **directed forward edges** between canonicalized state nodes (`NodeLabelLambda`), bounded by `MaxDepth` and `StateConstraint`.
3. **Global `[Invariant]` Checks (`Invariant.cs`)**:
   - Every reachable state asserts domain invariants (`Balance >= 0`, no orphan IDs, every disabled button in `View(state)` matches its command `Rule`).

### 3.2 IntoChat's Architectural Advantage: Zero Separate `Spec<TState>` Needed!
In `BankAccountTests.cs`, the author had to write `BankState` + `CreateSpec()` (`115` lines) separately because the production ASP.NET controller + EF `DbContext` was coupled to HTTP and SQL.
In IntoChat's DDD Aggregate Reactor:
- `TState` **is already an immutable C# record** whose command methods (`Decision<BankAccount> Withdraw(decimal amount)`) are **pure synchronous functions** returning either `Decision.Rejected(rule)` (`SameState`!) or `Decision.Accepted(nextState, signals)` (`ThenState`!), and `View(TState)` is a **pure projection** `TState -> Node`!
- Therefore `AggregateExplorer.Explore<BankAccount>(initial: new(), maxDepth: 5, stateConstraint: ...)` can:
  1. Reflect `[Command]` methods + `[Choice]` / `InputSet` parameter domains + `[Invariant]` methods directly off `BankAccount`.
  2. Unfold the complete reachable state graph in **milliseconds in-memory** (zero HTTP, zero Silo boot).
  3. Run **UI-to-Domain Consistency Checks** on every reachable state `s`: render `View(s)` and verify every `Button` bound to command `C(args)` has `Enabled == false` iff `C(args)` returns `Rejected` (eliminating UI/domain drift by construction!).
  4. Emit **Graphviz DOT** (`ToDot()`), interactive **`StateGraphView` nodes** (`UK-15` in Creator & Gallery), and executable **`scenarios.json` BDD sequences** for `Creator` self-healing verification!

---

## 4. Deep-Dive 3: macOS Sonoma System Settings & Finder (`UK-9`, `UK-11`, `UK-22`, `UK-23`)

1. **System Settings Anatomy (`DESIGN.md` §12, `UK-22`)**:
   - Fixed `248px` `surfaceSunk` left sidebar with search `InputBox` at top + `34px` `NavItem` rows (`IcRadius.sm = 8`, solid `accent` + `onAccent` when selected).
   - Scrollable right pane on `surfacePage` (`maxWidth: 860px` left-aligned; `760px` centred in Profile, `32px–56px` side padding) composed of **`Panel` cards**: `surfaceCard` (`IcRadius.card = 14`, `1px borderHairline`, **no shadow**) containing `SettingRow` items (`15/600` label + `13.5` muted help on left, control on right) separated by `1px` hairlines.
   - **Instant Save & `InlineSave` Rule (`DESIGN.md` §11.4, §12.9)**: Switches, selects, and segments save at once and show a single past-tense toast (`"Theme set to Dark."`); typed values use `InlineSave` (`86px` right slot: small `Save` -> spinner `"Saving"` -> `done` check `"Saved"` for `2s`, without double-toasting)—**never** a page-level `"Save"` button!
2. **Finder Anatomy (`DESIGN.md` §13, `UK-23`)**:
   - `216px` `surfaceSunk` Locations/Favourites sidebar + `52px` unified toolbar (`Back`, `Up`, `BreadcrumbBar` with responsive `•••` overflow menu, `SearchField`, `SegmentedControl` List/Grid toggle, Quick View inspector toggle, `"Ask Ino"` handoff button).
   - Main pane switches cleanly between sortable `Table` (List view with folder disclosure chevrons + `tnum` size/date columns) and `AdaptiveGrid(minTileWidth: 96)` (Grid view) + collapsible `280px` right `InspectorDrawer` (`Quick View`) + `28px` bottom status bar.
   - All of these must be **reusable C# `Kit` components** (`ResponsiveSplit`, `BreadcrumbBar`, `AdaptiveGrid`, `Table`, `InspectorDrawer`) rather than trapped inside the `1,337`-line Dart-only `IcFilesFinderView` widget!

---

## 5. Deep-Dive 4: Linear (`UK-10`, `UK-12`, `UK-16`)

1. **Keyboard-First Speed Without Visual Clutter**:
   - Every list, sidebar, tab bar, and inline prompt is 100% operable without touching the mouse (`↑`/`↓` roving selection, `↵` open/confirm, `1..9` instant choice pick, `Ctrl/Cmd+K` / `/` command palette, `Esc` dismiss or `"Chat about this"`).
   - Shortcut hints (`⌘K`, `Esc`, `1`..`9` `22×22px` keycaps) use `textSubtle` / `surfaceSunk` so they guide keyboard users without shouting at mouse users.
2. **Optimistic Local Input + Ordered Server Commit**:
   - Text inputs (`TextField`, `SearchField`, chat composer) update at `60–120fps` locally in Flutter while `NeuronEventQueue` serializes `250ms` debounced field updates strictly ahead of any `OnSubmit` / `OnClick` command activation—preventing lost keystrokes or cursor jumps when `ViewChanged` arrives (`field.dart:59` skips overwriting `_controller.text` while `_dirty || _focus.hasFocus`).
3. **High-Signal Hairline Density**:
   - `1px` `borderHairline` borders carry structural hierarchy instead of heavy drop shadows or nested cards-inside-cards (**max 2 surface layers visible at once**: `surfacePage` -> `surfaceCard`, `DESIGN.md` §5.1).

---

## 6. Deep-Dive 5: Shadcn/UI & Radix Primitives (`UK-4`, `UK-6`, `UK-10`, `UK-12`)

1. **Headless Behavior Layer vs Token Skin**:
   - Every interactive widget in `os/ui/lib/src/components/kit/` must separate **interaction & accessibility mechanics** (`FocusNode`, `PromptKeys`/`Stop`, `Semantics`, `MenuAnchor` / overlay positioning, `Esc` dismissal, focus restoration on modal close, disabled-reason tooltip) from **visual tokens** (`IcTheme.of(context)`).
2. **Composable Anatomy + `Slot` Over Mega-Props**:
   - Instead of adding bespoke wire nodes for every domain (`DraftCardNode`, `RecordCardNode`, `SecretCardNode`, `JobCardNode`, `AppShellNode`), provide orthogonal building blocks (`Card`, `Panel`, `HeaderBar`, `DescriptionList`, `Form`, `StatusPill`, `ProgressBar`, `Slot`) that any C# app or Ino-synthesized unit can compose arbitrarily.
   - `Slot(neuronUri)` acts like Radix `Slot` / micro-frontend outlet across Neuron boundaries: a parent `Screen<TState>` embeds a child Neuron's live `View` without copying its state or rebuilding the parent when the child updates (`UK-6`).
3. **Structured `Form` + Per-Field `Rule` Feedback**:
   - Inspired by Shadcn `<FormField>` + Zod: `Kit.Form([ TextField(...), NumberField(...) ]).OnSubmit(s => s.CreateAlert(Arg.String("name"), Arg.Decimal("threshold")))` binds named form fields to a single Aggregate command and maps broken `Rule`s back to inline field `error` captions (`12px error` below the offending box) without wiping typed input.
