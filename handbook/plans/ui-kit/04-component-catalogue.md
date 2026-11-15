# UK-4 · Canonical UI Kit Component Catalogue & Phase 2 Gate Spec

> **Linear Issue**: `INT-1352` (`UK-4` — **Phase 2 Gate**) · **Project**: `P-INT-7` (`UI Kit`)
> **Downstream Tracks Governed**: `UK-5`..`UK-19` (Component Families) & `UK-20`..`UK-27` (Shipped Apps, Units, Gallery, Docs)
> **Normative References**: `01-design-language-audit.md`, `02-current-inventory.md`, `03-benchmarks.md`, `E:\intochat\design\DESIGN.md`

---

## 1. The Five DDD Aggregate Reactor Laws (Applies to Every Component)

Every component in `DigitalBrain.UI.Kit` (`C#`) and `os/ui` (`Flutter`) obeys five architectural invariants established in Phase 0 (`UK-0`):

1. **Law 1 — Rich DDD Aggregate State (`TState`)**:
   - Every `Screen<TState>` owns an immutable C# `[State]` record `TState` with a parameterless constructor `new()`.
   - **Pure UI transitions** (selecting a tab, toggling a drawer, changing sort/query) are methods on `TState` returning `TState` (updated in-memory and re-published to `IView` without unnecessary journal churn unless persisted).
   - **Domain commands** (mutating business state) are methods on `TState` returning `Decision<TState>` (`Decision.Check(Rule...)` -> `Rejected` `SameState` + `Rule.Message`; `Decision.Accept(nextState, Signal...)` -> `Save` + past-tense `[Signal]` Domain Events).
2. **Law 2 — Shared `Rule` Guards & Disabled-Reason Tooltips**:
   - Business preconditions are `DigitalBrain.Rules.Rule` records on the Aggregate (`public Rule SufficientFunds(decimal amt) => ...`).
   - Attaching `.Guard(state.SufficientFunds(30m))` to any interactive `Kit` builder sets `Enabled = !rule.Broken` **and** serializes `DisabledReason = rule.Broken ? rule.Message : null` on the wire node so Flutter renders a hover/focus `Tooltip` explaining *why* the control is disabled and `AggregateExplorer<TState>` verifies UI-to-domain guard equivalence.
3. **Law 3 — Expression-Tree Command Binding & Deterministic Auto-Keys**:
   - Interactive builders bind directly to Aggregate methods via C# `Expression<Func<TState, TResult>>`:
     - Zero-arg / captured-arg click: `.OnClick(s => s.Withdraw(30m))` -> deterministic key `"cmd:Withdraw:30"`.
     - Single-value input / toggle / select: `.OnChange(s => s.SetArmed(Arg.Bool))`, `.OnChange(s => s.SelectTab(Arg.String))`, `.OnSubmit(s => s.SetThreshold(Arg.Decimal))`.
     - Multi-field atomic form: `Form("create-conn", [...]).OnSubmit(s => s.AddConnection(Arg.Field<string>("name"), Arg.Field<string>("endpoint")))`.
   - Zero manual `new Emit(...)` or stringly-typed `SignalEnvelope` unpacking is required in app code; `Screen<TState>` compiles expression bindings into typed server-side activators gated by `IView` generation checks (`409 stale` retry-safe).
4. **Law 4 — Gate 2.0 Person-Only Boundary (`RequiresPerson` / `Resolve`)**:
   - Standard commands emit `ViewActivationKind.Emit` (callable by both the human UI and authorized Ino/MCP `screen_act` tools).
   - High-consequence approvals (`ApprovalCard`) carry `Resolve(PlanId)` (`ViewActivationKind.Resolve`), enforced at the kernel/edge boundary so **only a human session on an interactive confirmation/host surface** can approve or reject—never an AI agent or embedded read-only preview (`view_host.dart:238-241`).
5. **Law 5 — Live Sub-Neuron Composition via `Slot` (`UK-6`)**:
   - `Slot(string neuronUri, string kind = "view", double? minHeight = null)` (backed by enhanced `NeuronRef` + `ViewHost`) embeds any child Neuron's live `View` inside a parent `View`.
   - `ViewHost` allows `'view'`, `'viewport'`, and `'webbrowser'` (never `'confirmation'` when `embedded == true`), tracks `ancestors` (`maxDepth = 16`, cycle guard), sizes naturally without the legacy hardcoded `480px` clamp when unbounded/scrollable, and isolates child `ViewChanged` rebuilds from the parent view.

---

## 2. Canonical Component Families (`UK-5`..`UK-19`)

### Family 1 · Foundation & Typography (`UK-5`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Light / Dark Tokens & Typography | States, Keyboard & A11y Contract |
| :--- | :--- | :--- | :--- | :--- |
| **`Heading`** | `Heading(string text, int level = 1, string? subtitle = null)` (`H1`, `H2`, `H3` aliases) | `heading` | `L1`: `InterDisplay 24–28 w700 -0.5` `textStrong`; `L2`: `InterDisplay 20 w700 -0.45`; `L3`: `Inter 16 w600 -0.2`; `subtitle`: `Inter 13 w400 textMuted` | `Semantics(header: true)`; sentence case mandatory (`DESIGN.md` §3.4). |
| **`Text` / `Prose`** | `Text(string value, Tone tone = Tone.Body, bool tabular = false, double? size = null, bool selectable = false)` (`Muted(...)`, `Strong(...)`, `Tabular(...)` helpers) | `text` | `Inter 13.5` (`14` in chat), `textBody` / `textStrong` / `textMuted` / status fg; `tabular: true` enables OpenType `tnum`. **Deprecate `Mono`**. | Plain `Text` by default so parent rows receive taps cleanly; `SelectableText` only when `selectable: true`. |
| **`Eyebrow`** | `Eyebrow(string label)` | `text` (`tone: "eyebrow"`) | `Inter 11 w600 +0.55` `textMuted`, `UPPERCASE` | Only legal `UPPERCASE` text role (`DESIGN.md` §3.4). |
| **`Icon`** | `Icon(string name, Tone tone = Tone.Muted, double size = 16)` | `icon` *(or inline)* | Lucide stroke `1.75`, `15–18px`, semantic fg token | Decorative unless `label` supplied (`Semantics(label: ...)`). |
| **`BrandChip` / `AppIcon`** | `AppBadge(string appId, double size = 22)` | `tagPill` / `appIcon` | `BrandVisuals` / `IcAppIcon` squircle (`22%` radius) | Consistent app & model identity across Dock, headers, and chat. |
| **`Divider`** | `Divider(string? label = null)` | `divider` | `1px borderHairline`; optional centered `12px textMuted` label | `Semantics` separator. |
| **`Spacer`** | `Spacer(double size = 12)` / `FlexSpacer()` | `spacer` | Fixed `IcSpace` (`2..56`) or flexible `Expanded` spring in `HStack` | Layout-only (`ExcludeSemantics`). |

---

### Family 2 · Layout & Containers (`UK-6`, `UK-9`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Visual & Responsive Contract | States, Keyboard & A11y Contract |
| :--- | :--- | :--- | :--- | :--- |
| **`VStack` / `HStack`** | `VStack(ReadOnlySpan<Node> children, double gap = 12, double padding = 0, Align align = Align.Stretch)` / `HStack(...)` | `column` / `row` | `gap` & `padding` snap to `IcSpace`; `HStack` supports `CrossAxisAlignment.center` (toolbar rows) & horizontal overflow scroll guard | Preserves reading order (`top->bottom`, `left->right`) for `PromptKeys`. |
| **`AdaptiveGrid`** | `Grid(int columns, ReadOnlySpan<Node> children, double gap = 12)` / `AdaptiveGrid(double minTileWidth, ReadOnlySpan<Node> children, double gap = 12)` | `grid` | Fixed `1..12` columns or responsive `floor((width + gap) / (minTileWidth + gap))` (e.g., `96px` Finder icons, `240px` KPI tiles) | Arrow navigation across tiles when wrapped in `PromptKeys`. |
| **`ResponsiveSplit`** | `ResponsiveSplit(Node sidebar, Node content, double sidebarWidth = 248, double collapseBelow = 720, bool resizable = true)` | `splitPane` | `>= collapseBelow`: `surfaceSunk` sidebar + `1px borderHairline` sash (`3px accent` on drag/hover, double-tap reset) + `Expanded` content. `< collapseBelow`: collapses sidebar into toggleable drawer/back-stack (`DESIGN.md` §7.2). | Sash keyboard nudge (`←`/`→` `16px` step when focused) + drawer `Esc` close. |
| **`Card`** | `Card(ReadOnlySpan<Node> children, string? title = null, string? subtitle = null, Node? trailing = null, Node? footer = null, double padding = 16)` | `card` / `surface` | `surfaceCard` (`#FFFFFF` / `#1C1916`), `IcRadius.card` (`14`), `1px borderHairline`, `IcShadow.xs`, **zero blur** (`blurSigma = 0`). | Optional `.OnClick(...)` interactive card variant (`hover`: `borderInput` + `-1px` lift + `SelectionRing`). |
| **`Panel` & `SettingRow`** | `Panel(string? heading, ReadOnlySpan<Node> rows)` + `SettingRow(string label, Node control, string? description = null, bool savedFlash = false)` | `surface` (`kind: "panel"`) | macOS System Settings grouped panel (`DESIGN.md` §12.1): `surfaceCard`, `14px` radius, `1px` inset hairlines (`16px` left inset) between rows; max width `680px`. | Row label pairs semantically with trailing control; `InlineSave` announces `Saved` politely. |
| **`HeaderBar`** | `HeaderBar(string title, string? subtitle = null, Node? leading = null, Node? center = null, ReadOnlySpan<Node> actions = default)` | `row` / `headerBar` | `48–52px` height, `surfaceCard` or `surfacePage`, bottom `1px borderHairline`; integrates cleanly under `38px`/`42px` OS window chrome without double titlebars! | Toolbar actions participate in `Stop` order. |
| **`Slot`** | `Slot(string neuronUri, string kind = "view", double? minHeight = null)` | `neuronRef` | Live nested `ViewHost` (`embedded: true`); renders `Skeleton` while loading and `ErrorLine` with `Retry` on failure; cycle/depth guard (`<= 16`). | Child view manages its own `generation` and `NeuronEventQueue` without blocking parent. |

---

### Family 3 · Inputs & Forms (`UK-10`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Tokens, Geometry & States | DDD Command / Field Binding & Keyboard |
| :--- | :--- | :--- | :--- | :--- |
| **`PrimaryButton` / `Button` / `GhostButton` / `DangerButton` / `LinkButton`** | `Button(string label, ButtonStyle style = Secondary, string? icon = null, bool compact = false, bool loading = false)` + `PrimaryButton(...)`, `GhostButton(...)`, `DangerButton(...)`, `LinkButton(...)` | `button` | Height `36px` (`compact: 30px`), radius `10px` (`IcRadius.button`), `Inter 13 w600`, `15px` Lucide icon. Primary: `accent` fill + `#FFFFFF` `onAccent` in **both** light & dark (`D-01` fixed!). Secondary: `surfaceCard` + `borderInput` + `IcShadow.xs`. Ghost: transparent -> `surfaceSunk` hover. Danger: `error` border + `errorBg` hover. | `.OnClick(s => s.Cmd(...)).Guard(rule)`. `Stop` + `SelectionRing` (`isDefault: true` on `PrimaryButton`); `1px` press translate; `loading: true` shows `14px` `WorkingSpinner` and suppresses double-submit; broken `.Guard(rule)` shows `DisabledReason` tooltip! |
| **`IconButton`** | `IconButton(string icon, string tooltip, IconButtonStyle style = Ghost, double size = 32)` | `button` (`iconOnly: true`) | `32×32px` (or `28×28px` dense), radius `8px` (`sm`), `16px` Lucide glyph; mandatory `tooltip` (`IcRadius.xs = 6`). | `.OnClick(...).Guard(rule)`; `Tooltip` doubles as `Semantics(label: tooltip)`. |
| **`TextField` / `TextArea`** | `TextField(string name, string label, string? value = null, string? placeholder = null, string? helper = null, string? error = null, bool multiline = false)` | `field` | `13px w600` explicit `label` (`6px` gap), `36px` box (`multiline`: min `72px`), **`10px` radius** (`IcRadius.button`), `surfaceCard` fill, `borderInput` rest -> `borderStrong` hover -> `1.5px accent + 3px ring` focus -> `error` border + `12px error` caption on invalid. | Syncs to `ui.field` (`250ms` debounce via `NeuronEventQueue`) + `.OnChange(s => s.SetX(Arg.Text))` or `.OnSubmit(s => s.SubmitX(Arg.Text))`. Never overwritten by server `ViewChanged` while focused & dirty! |
| **`SearchField`** | `SearchField(string name, string placeholder = "Search…", string? value = null, string? shortcutHint = null)` | `field` (`fieldKind: "search"`) | Leading `LucideIcons.search` (`15px textMuted`), trailing clear `×` button when non-empty or subtle `⌘F`/`/` badge when empty; `36px` height, `10px` radius. | `Esc` clears text when non-empty, else releases focus; `Enter` triggers `.OnSubmit`. |
| **`SecretField`** | `SecretField(string name, string label, string? placeholder = "Paste key or token…", string? savedHint = null)` | `field` (`fieldKind: "secret"`) | Obscured bullets (`••••`), trailing eye reveal toggle (`LucideIcons.eye`/`eyeOff`), `autocorrect: false`, `enableSuggestions: false`. | **Write-only**: never reads stored secret back over `GET /apps/node`; clears local controller immediately on submit (`DESIGN.md` §14.6). |
| **`NumberField`** | `NumberField(string name, string label, decimal value, decimal? min = null, decimal? max = null, decimal step = 1m, string? prefix = null, string? suffix = null)` | `field` (`fieldKind: "number"`) | `36px` height, `Inter 13.5 tnum`, optional currency/unit affix (`$`, `%`, `ms`) + `-`/`+` stepper buttons. | `↑`/`↓` increments/decrements by `step` within `[min, max]`; `.OnSubmit(s => s.SetAmount(Arg.Decimal))`. |
| **`SelectBox`** | `SelectBox<T>(string label, T selected, IReadOnlyList<SelectItem<T>> items, string? hint = null)` | `select` | **`36px` height** (`D-06` fixed from `48px`!), **`10px` radius**, `LucideIcons.chevronDown`; popover menu (`IcShadow.md`, `surfaceCard`, `1px borderHairline`) auto-adds `SearchField` when `items.Count > 8`. | `.OnChange(s => s.SelectModel(Arg.Value<T>()))`; `↑`/`↓`/`Enter`/`Esc` keyboard menu navigation; per-item disabled `detail` reason. |
| **`ToggleSwitch`** | `ToggleSwitch(string label, bool isOn, string? description = null, bool locked = false)` | `switch` | `36×20px` pill track (`accent` on / `surfaceSunk + borderInput` off), `14px` thumb (`onAccent` `#FFFFFF` on / `borderStrong` off), optional `LucideIcons.lock` (`13px`). | `.OnChange(s => s.ToggleFeature(Arg.Bool)).Guard(rule)`; `Space`/`Enter` toggles; `Stop` + `SelectionRing`. |
| **`Checkbox` & `RadioGroup`** | `Checkbox(string label, bool isChecked, string? description = null)` / `RadioGroup<T>(string label, T selected, IReadOnlyList<RadioItem<T>> items)` | `checkbox` / `radioGroup` | `18×18px` checkbox (`5px` radius, `accent` fill + `LucideIcons.check` when checked, `-` indeterminate); `18×18px` circular radio (`8px` inner dot). | `.OnChange(...)`; `Space` toggles checkbox; `↑`/`↓`/`←`/`→` roves `RadioGroup`. |
| **`Slider`** | `Slider(string label, double value, double min = 0, double max = 100, double? step = null, string? valueLabel = null)` | `slider` | `4px` `surfaceSunk` track with `accent` active fill + `16px` `surfaceCard` thumb (`borderStrong` + `IcShadow.xs`) + `tnum` value readout. | `←`/`→` adjusts by `step` (`Stop.onArrow`); commits on drag-end / key-up. |
| **`SegmentedControl`** | `Segmented<T>(T selected, IReadOnlyList<SegmentItem<T>> items)` | `segmented` *(or `tabs` `variant: "pill"`)* | `32px` `surfaceSunk` track (`IcRadius.button = 10`, `2px` padding), active thumb `surfaceCard` + `IcRadius.xs = 6` + `IcShadow.xs`, `Inter 13 w600` (`DESIGN.md` §10.2). | `←`/`→` switches segment via `Stop.onArrow`; `.OnChange(s => s.SetViewMode(Arg.Value<T>()))`. |
| **`Form`** | `Form(string key, ReadOnlySpan<Node> children, Node? submitButton = null)` | `form` | Vertical field stack (`14px` gap) + right-aligned or full-width action row; displays summary `ErrorLine` if command rejects at aggregate level. | Flushes all child `ui.field` values via `NeuronEventQueue` before invoking `.OnSubmit(...)`; `Ctrl/Cmd + Enter` submits from any field; maps `Rejected` `Rule` field tags to inline field `error` props. |

---

### Family 4 · Navigation & Overlays (`UK-11`, `UK-12`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Visual & Token Contract | Keyboard & Focus Contract |
| :--- | :--- | :--- | :--- | :--- |
| **`NavList` & `NavItem`** | `NavList(ReadOnlySpan<Node> items, string? eyebrow = null)` + `NavItem(string label, string icon, bool selected, string? badge = null)` | `navItem` | `36px` height, `8px` (`IcRadius.sm`) pill, `3×18px` left `accent` indicator + `accentTint` fill when `selected`, `hoverOverlay` hover, `tnum` count badge. | **Must wrap in `Stop` + `FocusableActionDetector`** (fixes `nav_item.dart` keyboard gap!); `↑`/`↓` + `Enter`. |
| **`Tabs`** | `Tabs(string selectedId, ReadOnlySpan<TabSpec> items)` | `tabs` | Underline tab strip (`1px borderHairline` bottom rule, `2px accent` active bar, `Inter 13 w600 textStrong` active / `w500 textMuted` rest — fixes `12px` drift). | `role="tablist"`, `←`/`→` roving focus, auto-scroll active tab into view. |
| **`BreadcrumbBar`** | `Breadcrumbs(IReadOnlyList<CrumbSpec> items, Node? trailing = null)` | `breadcrumbs` | Promoted from Dart-only `IcBreadcrumbBar` to first-class wire `Node`! `surfaceCard`/`surfaceSunk` bar, `LucideIcons.chevronRight` (`13px`), responsive `•••` overflow flyout below `680px`. **Zero monospace on trailing pills** (`Inter 11.5 tnum`). | Each crumb & `•••` trigger is a keyboard `Stop`. |
| **`DropdownMenu` & `ContextMenu`** | `Menu(Node trigger, IReadOnlyList<MenuItemSpec> items)` | `menu` | `surfaceCard`, `IcRadius.button` (`10`), `1px borderHairline`, `IcShadow.md`, `32px` item rows (`8px` radius, optional `15px` Lucide icon, right-aligned `12px textSubtle` shortcut hint, danger items in `error`). | Radix/MenuAnchor contract: `↑`/`↓` navigate, typeahead jump, `Enter` invoke, `Esc` close & restore focus to `trigger`. |
| **`Modal` / `ConfirmDialog`** | `Modal(string title, Node body, Node primaryAction, Node? secondaryAction = null, bool open = true)` | `modal` | `scrim` backdrop + `surfaceCard` (`IcRadius.panel = 16`, `1px borderHairline`, `IcShadow.window`, `maxWidth: 460px`, zero blur). | Focus-trapped inside modal (`PromptKeys`); `Ctrl/Cmd+Enter` runs primary action; `Esc` invokes secondary/close action and restores prior focus. |
| **`InspectorDrawer` / `Sheet`** | `InspectorDrawer(string title, Node body, bool open = true, double width = 280)` | `sheet` | Right-hand docked inspector (`Files` Quick View, `Creator` node/edge inspector): `280–320px`, `surfaceCard` / `surfaceSunk`, left `1px borderHairline`. Collapses to slide-over sheet on narrow viewports. | `Esc` or header close button triggers `.OnClose(s => s.CloseInspector())`. |
| **`Popover` & `Tooltip`** | `Popover(Node trigger, Node content)` / `.WithTooltip(string text)` | `popover` | Tooltip: `surfaceInk` + `onInk` in light, `surfaceCard + 1px borderHairline` in dark (`DESIGN.md` §10.8), `IcRadius.xs = 6`, `12px Inter`, `400ms` hover delay. | Appears on both pointer hover and keyboard focus. |

---

### Family 5 · Feedback & Status (`UK-13`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Visual & Status Triad Contract | Behavior & Accessibility |
| :--- | :--- | :--- | :--- | :--- |
| **`StatusPill` / `Badge`** | `StatusPill(string label, Tone tone = Tone.Neutral, string? icon = null)` (unifies `Badge` + `TagPill`) | `tagPill` | `IcRadius.xs` (`6px`), `8×3px` padding, `Inter 12 w600`; **auto-pairs canonical Lucide status icon** (`check`, `WorkingSpinner`/`clock`, `circleAlert`) with `done`/`working`/`error` fg + `16%` bg (`D-02` fixed). | Screen reader reads status word + label. |
| **`Banner` (`InfoBar`)** | `Banner(Tone tone, string title, string? detail = null, Node? action = null)` | `infoBar` | Full-width rounded card (`14px`), semantic tint bg + `35%` border + leading status icon + **inline trailing `compact` CTA button** on desktop (fixes vertical stack in `info_bar.dart`). | `Semantics(liveRegion: true)` when `tone == Tone.Error`. |
| **`ErrorLine`** | `ErrorLine(string what, string? detail = null, Node? retryAction = null)` | `errorLine` | `errorBg` fill, `error @ 35%` border, `LucideIcons.circleAlert`, plain-human explanation + 1 recovery button (`DESIGN.md` §14.7). | Mandatory single recovery step (`Try again`, `Open Settings`). |
| **`Toast`** | `Toast(string message, Tone tone = Tone.Neutral, string? actionLabel = null)` | `toast` | Bottom-center floating pill (`IcRadius.button = 10`, `surfaceInk` + `onInk` in light, `surfaceCard + hairline` in dark, `IcShadow.md`, `4s` lifetime, optional `Undo` action). | Polite `liveRegion`; pauses timer on hover/focus. |
| **`ProgressBar` & `Spinner`** | `ProgressBar(double? value = null, string? label = null, string? detail = null)` / `Spinner(string? label = null)` | `progress` | `6px` rounded pill track (`surfaceSunk` + `accent` fill); `value == null` renders smooth indeterminate bar; `Spinner` renders `14px` `WorkingSpinner` (static icon under `reducedMotion`). | `Semantics(value: "${pct}%")`. |
| **`Skeleton`** | `Skeleton(double height = 16, double? width = null, int lines = 1)` | `skeleton` | Warm `surfaceSunk` rounded bars (`1500ms` subtle opacity pulse, static under `reducedMotion`) shown after `200ms` loading threshold (`DESIGN.md` §8.3). | `ExcludeSemantics` inside `Semantics(label: "Loading…")`. |
| **`EmptyState`** | `EmptyState(string title, string? body = null, string? icon = null, string? appId = null, IReadOnlyList<Node>? suggestions = null)` | `emptyState` | Centered (`maxWidth: 560px`), configurable `icon` or `appId` (fixes hardcoded `'ino'` icon in `empty_state.dart:35`!), `InterDisplay 22–26 w700`, `PromptKeys` suggestion cards. | Keyboard `↑`/`↓`/`↵` across suggestion cards. |
| **`InlineSave`** | `InlineSave(bool saved = true, string text = "Saved")` | `tagPill` | Subtle `12px done` `✓ Saved` indicator next to instant-save settings controls (`1.6s` fade). | Polite live announcement. |

---

### Family 6 · Data, Charts & Accordant State Graphs (`UK-14`, `UK-15`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Restored Interactivity & Token Contract | Keyboard & MCP Contract |
| :--- | :--- | :--- | :--- | :--- |
| **`Table`** | `Table(string title, IReadOnlyList<TableColumnSpec> columns, IReadOnlyList<TableRowSpec> rows, string? sortColumn = null, bool sortDesc = false, int? page = null, int? totalRows = null)` | `dataTable` | Unifies passive `TablePreviewCard` + orphaned `UiDataTable` (`ui_data_table.dart`) using **pure Warm Ink & Paper Kit primitives** (zero Material `AlertDialog`/`InputChip`, `D-08` fixed): `surfaceSunk` `36px` sticky header, `40px` hoverable rows, right-aligned `tnum` numeric columns, inline `SearchField`/filter chips, pagination footer, and `"Open in Data Studio"` action. | `.OnSort(s => s.SortBy(Arg.Column, Arg.Bool))`, `.OnRowClick(s => s.InspectRow(Arg.RowId))`, `.OnPage(s => s.GoToPage(Arg.Int))`; `↑`/`↓` row selection + `↵` activate. |
| **`BarChart` / `LineChart` / `AreaChart` / `DonutChart`** | `BarChart(string title, IReadOnlyList<ChartSeries> series, ...)` / `LineChart(...)` / `AreaChart(...)` / `DonutChart(...)` | `chart` | **Fixes post-`1c15af1db` / `16f7fe086` regression**: supports `bar` (`columns`), `line`, `area`, and **`donut`/`pie`** (fixing `chart.dart:11` bug where `"pie"` drew a line chart!); multi-series palette from `IcColors.series[0..4]` (`accent`, teal, iris, ochre, `Other` — zero `UiColors.paper` green); `tnum` Y-axis & hover crosshair tooltip; optional `threshold` dashed rule; wired header kind-switcher & `"Open in Data Studio"` action! | `.OnSelectPoint(s => s.DrillDown(Arg.Series, Arg.Label))`, `.OnSwitchKind(s => s.SwitchChart(Arg.String))`; keyboard `←`/`→` point scrubber inside focused chart card. |
| **`MetricTile` & `Sparkline`** | `MetricTile(string title, string value, string? delta = null, Tone tone = Tone.Neutral, IReadOnlyList<double>? sparkline = null, double? threshold = null, IReadOnlyList<Node>? actions = null)` + standalone `Sparkline(IReadOnlyList<double> points, Tone tone = Tone.Accent)` | `metricTile` | Unifies `StatCardNode` + `MetricTileNode`: `InterDisplay`/`Inter` `26–28px w700 tnum` hero figure, directional delta `StatusPill` (`trendingUp`/`Down`), smooth `48px` cubic-bezier `SparklinePainter` + hover crosshair + dashed threshold. | Action buttons participate in `Stop` order; `StatCardNode` kept as wire alias mapping to `MetricTile`. |
| **`DescriptionList`** | `DescriptionList(IReadOnlyList<(string Label, string Value, bool Tabular, Tone Tone)> items)` | `descriptionList` *(or `collection`)* | Clean key/value pairs (`13px textMuted` label left, `13.5px textStrong` value right, `1px borderHairline` dividers, `tnum` on amounts/dates). Replaces bespoke `RecordCardNode` field tables. | Optional copy-value intent per row. |
| **`TreeView`** | `TreeView(IReadOnlyList<TreeNodeSpec> roots, string? selectedId = null)` | `treeView` | Hierarchical folder / neuron / AST explorer (`28–32px` dense rows, `16px` indent per depth, `LucideIcons.chevronRight`/`Down` disclosure, folder/type icon). | WAI-ARIA Treegrid keyboard contract: `↓`/`↑` move, `→` expand/enter child, `←` collapse/move to parent, `↵` open. |
| **`StateGraphView` & `BrainScene`** | `StateGraphView(StateGraphModel graph, string? activeStateId = null, IReadOnlyList<string>? highlightedPath = null)` / `BrainScene(...)` | `stateGraph` & `brainScene` | **Accordant + Creator centerpiece (`UK-8`, `UK-15`)**: Crisp 2D pan/zoom interactive DAG/state-machine canvas rendering `AggregateExplorer<TState>` output: canonical state boxes (`surfaceCard`, `accent` ring on `activeStateId`), directed `Accepted` command edges (`textMuted` / `done` along `highlightedPath`), **dashed amber/red `SameState` self-loop arcs** for `Rule`-rejected preconditions, and red `!` badge on any `[Invariant]` violation! `BrainScene` remains the live journal-pulse topology view. | `.OnSelectState(s => s.InspectState(Arg.String))`, `.OnSelectTransition(s => s.ReplayStep(Arg.Int))`; `←`/`→` steps through scenario trace! |

---

### Family 7 · Chat, Docked Prompts & Inline Cards (`UK-16`..`UK-18`)

| Component | C# `DigitalBrain.UI.Kit` Signature | Wire `kind` | Design & Security Contract | Keyboard (`PromptKeys`) Contract |
| :--- | :--- | :--- | :--- | :--- |
| **`ApprovalCard` (`Gate 2.0`)** | `ApprovalCard(string id, string title, string summary, IReadOnlyList<ApprovalDiffRow> rows, Resolve? resolve, string status = "pending", string? valueLabel = null, string? capabilityLabel = null)` | `approvalCard` | **Retained Sacred Security Primitive**: `CardShell` with capability shield/brand icon, structured `oldValue → newValue` diff rows (`tnum` amounts, amber `warn` highlight), `Approve` (`PrimaryButton`) + `Reject` (`Button`) + `"Chat about this"`. Read-only when `embedded` or `resolve == null`. | Docks in composer (`PromptScope(docked: true)`): `↵` / `Ctrl+↵` focuses/invokes `Approve` (person-only `Resolve`), `Esc` -> `"Chat about this"`. |
| **`ChoicePrompt` (`ChoiceCard`)** | `ChoicePrompt(string id, string question, IReadOnlyList<ChoiceSpec> options, int? pickedIndex = null, string? note = null)` | `choiceCard` | `CardShell` + `PromptOptions` (`22×22px` numbered `1..N` keycaps + automatic `N+1` `"Chat about this"` row); collapses to compact `Answered` receipt pill once picked. | `1..9` single-key instant pick, `↑`/`↓` move `SelectionRing`, `↵` choose, `Esc` -> `"Chat about this"`. |
| **`SecretPrompt`** | `SecretPrompt(string id, string secretName, string why, string savedIn = "Settings → Connections", string status = "pending")` | `secretCard` *(upgraded)* | Upgraded to embed live write-only `SecretField` + `Save` + `Skip` inside `CardShell`; collapses to `Saved in Settings → Connections` (`done` check) and never echoes key into transcript. | Auto-focuses `SecretField` when docked; `↵` saves encrypted secret; `Esc` skips/chats. |
| **`DiffCard`** | `DiffCard(string id, string repo, string targetBranch, string headSha, IReadOnlyList<DiffFileNode> files, ...)` | `diffCard` | Collapsible file sections (`A`/`M`/`D`/`R` `StatusPill` + `+add / -del` `tnum` counts) + unified diff hunks (`doneBg` / `errorBg` `2.5px` left bar). Header/path chrome uses `Inter` (`tnum` line numbers). | File headers wrapped in `Stop` so `↑`/`↓`/`↵` expands/collapses files via keyboard. |
| **`MessageStream`, `ChatComposer` & `ActivityTracePill`** | `Conversation(...)` **decomposed into** composable `MessageStream(...)`, `ActivityTracePill(...)`, `PromptDock(...)`, and `ChatComposer(...)` (`UK-18`) | `conversation` *(kept as composite convenience)* + atomic chat sub-nodes | Preserves `760px` centered column, `surfaceInk` user bubbles (`18/18/18/6` radius), markdown assistant prose (`maxWidth: 680px`), compact brand/capability activity pills, model chip, `Esc to stop` bar, and `409 stale` auto-retry—now reusable in `Ino`, `Friends` Circle rooms, and `Creator` co-pilot! | `Enter` sends, `Shift+Enter` inserts newline, `Esc` stops running turn or exits docked prompt to composer. |

---

## 3. Bespoke Mega-Node Retirement & Migration Ledger

To satisfy Goal 3 (*"Composable UI Kit, Not Bespoke Mega-Nodes"*), the following legacy nodes and Dart bypasses are retired or decomposed in Phases 2–3:

| Legacy Node / Widget | Current Location | Action | Replacement Composition | Migration Ticket |
| :--- | :--- | :--- | :--- | :--- |
| **`AppShellNode`** | `ViewModel.cs:825`, `nodes/app_shell.dart` | **RETIRE** | `VStack([ HeaderBar(...), ResponsiveSplit(sidebar: NavList(...), content: ..., collapseBelow: 720) ])` + optional `InspectorDrawer` | `UK-9`, `UK-20`..`UK-25` |
| **`StatCardNode`** | `ViewModel.cs:847`, `nodes/stat_card.dart` | **MERGE / ALIAS** | Alias `StatCard` in C# `Kit` directly to `MetricTile` (single renderer, `tnum` + delta + optional sparkline) | `UK-14` |
| **`BadgeNode`** | `ViewModel.cs:243`, `nodes/badge.dart` | **MERGE / ALIAS** | Unify with `StatusPill` (`TagPillNode`) so every status badge gets automatic status icon + light/dark triad | `UK-13` |
| **`IntentNode` (standalone)** | `ViewModel.cs:295`, `nodes/intent.dart` | **DEMOTE FROM VISUAL NODE** | Keep `Intent` strictly as an action payload on `Button` / `NavItem` / `Menu` (`IntentGate` preserved), never rendered as a standalone visual `Node` | `UK-7` |
| **`RecordCardNode`** | `ViewModel.cs:743`, `nodes/record_card.dart` | **DEPRECATE -> COMPOSE** | `Card(title: name, subtitle: objectLabel, [ DescriptionList(fields) ], footer: LinkButton(linkLabel).OnClick(...))` | `UK-16`, `UK-21` |
| **`JobCardNode`** | `ViewModel.cs:769`, `nodes/job_card.dart` | **DEPRECATE -> COMPOSE** | `Card(title: jobTitle, [ ProgressBar(progress), StepList(steps) ], footer: Button("Cancel").OnClick(...))` | `UK-13`, `UK-16` |
| **`DraftCardNode`** | `ViewModel.cs:723`, `nodes/draft_card.dart` | **DECOMPOSE INTERNALLY** | Keep C# convenience helper `Kit.DraftPreview(...)` for Ino chat, but build it from `Card` + `DescriptionList` (`To`, `Subject`) + `Prose` + `PrimaryButton("Send")` / `Button("Discard")` instead of hardcoded `email\|slack\|sms` enum widget | `UK-16`, `UK-21` |
| **`IcFilesFinderView` (Dart-only bypass)** | `ui/lib/src/controls/ic_files_finder_view.dart` (`1,337` lines) | **REPLACE WITH C# `Screen<FilesAggregate>`** | `FilesApp` renders `HeaderBar` + `Breadcrumbs` + `ResponsiveSplit` + `Table` / `AdaptiveGrid` + `InspectorDrawer` via standard `NodeRenderer` | `UK-11`, `UK-23` |
| **`Conversation` (monolith)** | `ViewModel.cs:866`, `chat/conversation_view.dart` (`817` lines) | **DECOMPOSE INTO REUSABLE PARTS** | Extract `MessageStream`, `ChatComposer`, `PromptDock`, and `ActivityTracePill` so `Ino`, `Friends`, and `Creator` share the exact same chat primitives | `UK-18`, `UK-21`, `UK-25` |

---

## 4. Phase 2 Readiness Checklist (Gate Criteria)

Phase 2 component implementation (`UK-9`..`UK-19`) is cleared to proceed against this catalogue provided each PR enforces:
1. **C# + Dart Parity**: Every new `[ViewNode]` record in `DigitalBrain.UI` updates `node_models.dart` via `INTOCHAT_REGENERATE_DART=1` (`DartNodeModelsFacts`) and adds a fluent builder in `DigitalBrain.UI.Kit`.
2. **Token Purity**: Zero hardcoded hex colours, zero `monoFallback` on UI chrome, `onAccent == #FFFFFF` in both modes, and `blurSigma == 0` inside app windows (`01-design-language-audit.md`).
3. **Guard & Keyboard Completeness**: Every interactive control supports `.Guard(Rule)` disabled-reason tooltips, registers as a `Stop` inside `PromptKeys` (or implements standard WAI-ARIA arrow navigation), and exposes deterministic keys + semantic labels for MCP `screen_read` / `screen_act` and Playwright `?semantics=true` E2E tests.
4. **Gallery Story + Golden**: Every component lands with an interactive story (Light + Dark + Rest/Hover/Focus/Disabled/Error states) in `apps/gallery` (`UK-19`).
