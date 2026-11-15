# UK-2 · Current UI Inventory, State Matrix & Gap Analysis

> **Linear Issue**: `INT-1350` (`UK-2`) · **Project**: `P-INT-7` (`UI Kit`)
> **Audited Sources**:
> - `E:\intochat\digitalbrain\src\DigitalBrain.UI\ViewModel.cs` (`0.22.0`, 41 `[ViewNode]` records + `Ui` helpers)
> - `E:\intochat\digitalbrain\src\DigitalBrain\Screens\Screen.cs` & `Rules\Rules.cs`
> - `E:\intochat\os\src\IntoChat.OS.Framework\ShellContracts\{UIVocabulary.cs, UiParts.cs, ViewRef.cs, Views\IView.cs}` & `src\Modules\Shell\Shared\{AppUiEndpoints.cs, ScreenAppEmitBridge.cs, Screens\ScreensEndpoints.cs}`
> - `E:\intochat\os\ui\lib\src\nodes\*` (41 renderers), `ui\lib\src\components\*`, `ui\lib\src\design\*`, `ui\lib\src\composition\view_host.dart`
> - Git history forensics: commits `1c15af1db` (V1 runtime removal), `7df6471eb` (Ino interactive charts/tables), and `16f7fe086` (widget-neuron deletion)

---

## 1. End-to-End Wire & Hosting Architecture Today

One UI path governs `IntoChat.OS` (`OneUiPathFacts.cs`):

1. **Kept UI Neuron Kinds** (`UIVocabulary.cs`):
   - `view` (`IView`): Stores `{ Generation, View }` JSON, emits `ViewChanged(Name, Generation)`, and validates `Activate(generation, key, fieldValue)` -> `ViewActivation(Kind: None | Emit | Intent | Resolve)`. Stale generations return HTTP `409 { status: "stale" }`.
   - `ui.field` (`IField`): Stores per-field typed string state (`{scope}/apps/{app}/{field}`), emits `FieldChanged`, and queues `250ms` debounced edits through `NeuronEventQueue` **before** any subsequent button/submit activation (`node_context.dart:20-38`).
   - `confirmation` (`IConfirmationInbox`): Kernel-enforced person-only Gate 2.0 approval surface (`Resolve(PlanId)`).
   - `viewport`: Shell pointer `{ root: { kind, name }, error? }` hosting an app window's active view.
   - `webbrowser`: Embedded WebView2 / web iframe surface (`connect` / `disconnect` / `unavailable`).
2. **Codegen Contract** (`DartNodeModelsFacts.cs`):
   - Every `[JsonDerivedType(typeof(T), "kind")]` on `DigitalBrain.UI.Node` generates a `final class <Name>Node extends Node` in `ui/lib/src/generated/node_models.dart` (`INTOCHAT_REGENERATE_DART=1`).
   - Every view fixture under `src/IntoChat.OS.Apps/views/**/*.json` is validated against known `[JsonDerivedType]` discriminators.
3. **Current Authoring Friction in C#**:
   - Apps and micro-units (`HelloWorld.cs`, `BtcAlert.cs`, `DataStudioViewRenderer.cs`, `CreatorView.cs`, `SettingsApp.cs`) hand-construct positional `new Surface([...])`, manual string keys (`"group-by-" + col`), manual `new Emit("owner", "signal.alias", JsonSerializer.SerializeToElement(...))` boilerplate, and separate `[Synapse]` / `[On]` dispatchers that unpack untyped `SignalEnvelope` / `JsonElement` payloads.
   - `Rule` checks (`Rules.Judge(...)`) live inside command handlers (`BtcAlert.cs:85`) completely disconnected from the `Button(..., Enabled: !state.Armed)` predicate (`BtcAlert.cs:113`), duplicating business invariants and hiding *why* a button is disabled.

---

## 2. Complete 41-Node Inventory (`DigitalBrain.UI` -> `os/ui` -> `design`)

Legend for **States**: `R`=Rest, `H`=Hover, `P`=Pressed, `F`=Focus/`SelectionRing`, `D`=Disabled, `L`=Loading/Working, `E`=Error/Empty, `K`=Keyboard `PromptKeys`/`Stop`, `T`=Light+Dark Token Clean.

| # | Wire `kind` | C# Record (`ViewModel.cs`) | Dart Node Renderer (`os/ui/lib/src/nodes/`) | Backing Widget (`os/ui` vs `design`) | States Covered | Audit Verdict & Key Gaps |
| :- | :--- | :--- | :--- | :--- | :--- | :--- |
| 1 | `column` | `Column(Children, Gap, Padding, Extents?)` | `column.dart` | Flutter `Column` + `Padding` | `R` | Works; lacks alignment, scroll/fill control, and spacing-token presets (`IcSpace`). |
| 2 | `row` | `Row(Children, Gap, Padding, Extents?)` | `row.dart` | `Row` / `Wrap` / horizontal scroll | `R` | Auto-wraps chip/button-only rows; lacks vertical alignment (`center` vs `start`) & `Spacer` flex helpers. |
| 3 | `grid` | `Grid(Columns, Children, Gap)` | `grid.dart` | `LayoutBuilder` + `Wrap` | `R` | Fixed column count (`1..12`); lacks `minTileWidth` responsive auto-fit grid (`DESIGN.md` §13.1). |
| 4 | `splitPane` | `SplitPane(Leading, Trailing, LeadingExtent, Gap)` | `split_pane.dart` | `_SplitPaneWidget` (draggable sash) | `R,H,P` | Has drag + double-tap reset, but **no `<720px` responsive collapse** (`ResponsiveSplit` drawer/back-stack per `DESIGN.md` §7.2) and no keyboard sash resize. |
| 5 | `surface` | `Surface(Children, Kind, Title?, Subtitle?, Padding)` | `surface.dart` | `UiKitSurface` (`IcAcrylicBox`) | `R,T*` | **Drift**: `Kind='acrylic'` applies `blurSigma=20` inside app windows (`DESIGN.md` §5.2 violation); uses Material `textTheme` instead of `IcType`. |
| 6 | `tabs` | `Tabs(SelectedId, Items[TabItem])` | `tabs.dart` | `UiKitTabs` | `R,F,D` | Underline tabs only (`12px w700` drift vs `13px w600`); **missing pill `SegmentedControl`** (`IcRadius.button=10` track + `xs=6` thumb, `DESIGN.md` §10.2) and `←`/`→` arrow roving focus. |
| 7 | `divider` | `Divider(Label?)` | `divider.dart` | `ViewDivider` | `R,T` | Clean `1px` hairline with optional centered label. |
| 8 | `spacer` | `Spacer(Size)` | `spacer.dart` | `SizedBox.square` | `R` | Fixed pixel square only; cannot express `Expanded` flex spring in a `Row`/`HeaderBar`. |
| 9 | `collection` | `Collection(Entries[CollectionEntry])` | `collection.dart` | Static `Column` of `Row`s | `R` | **Read-only**: no `OnSelect` / `OnOpen` activation, no leading icon/avatar, no hover/selected state, no virtualization. |
| 10 | `heading` | `Heading(Value, Level, Subtitle?)` | `heading.dart` | `Semantics(header:true)` + `UiType` | `R,T` | Good `InterDisplay` (`H1–H3`) + `Inter` (`H4+`) mapping. |
| 11 | `text` | `Text(Value, Tone, Tabular, Mono, Size?)` | `text.dart` | `SelectableText` | `R` | **Drift**: `Mono=true` exists and is used for SQL (`DataStudioViewRenderer.cs:33`); renders every `Text` as `SelectableText` (steals click gestures inside interactive rows). |
| 12 | `badge` | `Badge(Value, Tone)` | `badge.dart` | `ViewBadge` | `R,T` | Overlaps 95% with `TagPill` (no icon parameter on `Badge`). Consolidate under `StatusPill` / `Badge` (`UK-13`). |
| 13 | `tagPill` | `TagPill(Label, Tone, Icon?)` | `tag_pill.dart` | `ViewBadge` | `R,T` | Good semantic tone mapping; dark status bg inherits `D-02` drift from `ui_colors.dart`. |
| 14 | `field` | `Field(Key, Name, Kind, Placeholder?, OnSubmit?)` | `field.dart` | `FieldNodeView` -> `UiKitField` | `R,H,F,D,E*` | Bound to `ui.field` neuron (`250ms` debounce). **Gaps**: derives visible label by slicing `node.name` after `/` (`field.dart:114`) instead of an explicit `Label` prop! No `Error` / `Helper` wire prop on `FieldNode`, no password reveal toggle, no `SearchField` clear button, radius `12` instead of `10`. |
| 15 | `intent` | `Intent(Kind, Arg?)` | `intent.dart` | `SizedBox.shrink()` (`IntentGate`) | N/A | Action payload (`Copy`, `OpenSettings`, `OpenWindow` allowlisted via `IntentGate`), not a visual widget—should not be a standalone `Node` in the visual Kit DSL. |
| 16 | `switch` | `Switch(Key, Label, IsOn, Emit?, Locked)` | `switch.dart` | `Row` + `UiKitSwitch` | `R,F,D,T` | Supports `Locked` icon (`LucideIcons.lock`); lacks optional `Description` subtitle, `InlineSave` confirmation flash (`Saved ✓`), and `SelectionRing` on `PromptKeys`. |
| 17 | `select` | `Select(Label, Selected, Options[SelectOption])` | `select.dart` | `UiKitSelect` (`MenuAnchor`) | `R,F,D` | Auto-adds search box over 8 items. **Drift**: `48px` height + `12px` radius instead of `36px` + `10px`; uses Material `Icons.search` / `Icons.check` / `Icons.keyboard_arrow_down` instead of Lucide. |
| 18 | `progress` | `Progress(Value, Label?, Detail?)` | `progress.dart` | `UiProgress` | `R,L` | Determinate bar only (`Indeterminate` not exposed on `ProgressNode`); `Detail` rendered as unstyled `Text(detail)`. |
| 19 | `infoBar` | `InfoBar(Tone, Title, Message?, Action?)` | `info_bar.dart` | `UiInfoBar` + `renderButton` | `R,E,T` | Action button renders **below** the bar in a `Column` instead of inline trailing on desktop (`DESIGN.md` §14.7). |
| 20 | `toast` | `Toast(Message)` | `toast.dart` | `KitToastBody` | `R,T` | Wire `ToastNode` lacks `Tone` and `Action` (`Undo`), even though `KitToastBody` supports both! Dark mode lacks `borderHairline` (`D-01`/`DESIGN.md` §10.8). |
| 21 | `card` | `Card(Title, Body, Children)` | `card.dart` | `UiKitSurface` | `R,T` | Basic container; lacks header actions slot, footer slot, `Panel` grouped row dividers (`DESIGN.md` §12.1), and hover/clickable card variant. |
| 22 | `button` | `Button(Key, Label, Emit?, Intent?, Tone, Enabled, Icon?, Small)` | `button.dart` | `UiKitButton` (`Stop` + `SelectionRing`) | `R,H,P,F,D,K` | Best-aligned control (`36/30px`, `10px` radius, `Stop` + `SelectionRing`, `1px` press offset). **Gaps**: only 3 wire tones (`Neutral`, `Accent`, `Danger` — cannot express `Ghost`, `Link`, or `IconButton` from C#!), no `Loading`/`Busy` spinner state, and **no `DisabledReason` tooltip** from `.Guard(Rule)`! Also suffers dark `onAccent` `#17140F` bug (`D-01`). |
| 23 | `dataTable` | `DataTable(Title, Columns, Rows, SourceNeuronUri?, From)` | `data_table.dart` | `TablePreviewCard` (`design/cards/table_card.dart`) | `R,E` | **Read-only preview card only!** `onOpen` (`Open in Sheets`) and `onRetry` are **unwired (`null`)**; `UiDataTable` (`components/table/ui_data_table.dart`) is **not reachable** from `DataTableNode`; no column sort, filter, pagination, or row click `Emit` on `DataTableNode`. |
| 24 | `chart` | `Chart(ChartKind, Title, Points, SeriesName?, SourceNeuronUri?, From, Prefix)` | `chart.dart` | `ChartCard` (`design/cards/chart_card.dart`) | `R,E` | **Severely regressed** (see §3 below): `onOpen`, `onRetry`, `onSwitchChartKind`, `onCopyNeuronUri` all **`null`**; `"pie"` falls through to `line` because `chart.dart:11` only checks `'bar' \|\| 'columns'`; single-series only; zero point-click `Emit`; `components/chart/ui_chart.dart` (`fl_chart`) is orphaned! |
| 25 | `approvalCard` | `ApprovalCardNode(Id, Title, Summary, Rows, Resolve?, Key, Status, ...)` | `approval_card.dart` | `ApprovalCard` (`PromptKeys` + `Stop`) | `R,H,F,D,E,K,T` | Strong Gate 2.0 component (`Approve` / `Reject` + diff rows + `Resolve` boundary). Keep as first-class security primitive (`UK-16`). |
| 26 | `choiceCard` | `ChoiceCardNode(Id, Question, Options, PickedIndex?)` | `choice_card.dart` | `ChoiceCard` (`PromptOptions`) | `R,H,F,D,K,T` | Keyboard-first `1..9` + `"Chat about this"`. `onChat` inside standalone `ChoiceCard` is unwired unless wrapped by `ConversationView` `PromptScope`. |
| 27 | `draftCard` | `DraftCardNode(Id, Channel, To, Body, Subject?, From?, Status, Send?, Discard?)` | `draft_card.dart` | `DraftCard` | `R,H,F,D,L,E,K,T` | Domain-specific mega-node (`email`/`slack`/`sms` enum hardcoded in wire schema). Candidate to compose from `CardShell` + atomic Kit fields/buttons (`UK-4` §6). |
| 28 | `recordCard` | `RecordCardNode(Id, ObjectLabel, Name, Fields, Subtitle?, LinkLabel, Open?)` | `record_card.dart` | `RecordCard` | `R,H,F,D,T` | Bespoke CRM/entity card; `DescriptionList` / `KeyValueRow` inside `Card` covers 100% of this without a special wire node. |
| 29 | `secretCard` | `SecretCardNode(Id, Name, Why, SavedIn, Status, Enter?, Skip?)` | `secret_card.dart` | `SecretCard` | `R,H,F,D,K,T` | Notice `SecretCard` in `os/ui` only triggers `Enter` / `Skip` `ButtonNode`s—it doesn't even host the `SecretField` input inline unless composed! Replace with `SecretPrompt` built on `CardShell` + `SecretField` (`UK-10`, `UK-16`). |
| 30 | `jobCard` | `JobCardNode(Id, Title, Progress, Steps, Status, ResultText?, Cancel?)` | `job_card.dart` | `JobCard` | `R,L,E,D,T` | Bespoke multi-step progress card; decompose into `Card` + `ProgressBar` + `StepList` (`UK-13`, `UK-16`). |
| 31 | `brainScene` | `BrainSceneNode(Nodes, Synapses)` | `brain_scene.dart` | `BrainSceneView` (Flame `SceneGame`) | `R,P,L` | Live Creator topology + journal signal pulse replay (`brainSelectKey = 'graph:select'`). Needs reusable **2D `StateGraphView`** sibling for Accordant `AggregateExplorer<TState>` (`UK-15`). |
| 32 | `neuronRef` | `NeuronRef(NeuronUri, RefKind)` | `neuron_ref.dart` | `ctx.reference` or `SelectableText` | `R` | **Critical Gap (`UK-6`)**: `ViewHost._referable` (`view_host.dart:218`) **blocks `'view'`** (`const _referable = {'viewport', 'webbrowser'}`) and wraps children in a hardcoded `SizedBox(height: 480)`! If an app embeds `new NeuronRef("local/units/btcalert/view", "view")`, `ctx.reference` returns `null` and falls back to raw `SelectableText` (`neuron_ref.dart:11-29`)! |
| 33 | `appShell` | `AppShellNode(Body, Sidebar?, Toolbar?, Inspector?)` | `app_shell.dart` | Hardcoded `Row(240, Expanded, 280)` | `R` | **Retire (`UK-4` §6)**: Non-responsive (`240px` + `280px` overflows narrow windows), no hairlines, no `surfaceSunk` sidebar background, duplicates `ResponsiveSplit` + `HeaderBar` + `InspectorDrawer`. |
| 34 | `navItem` | `NavItemNode(Key, Label, Icon, Selected, Emit, Badge?)` | `nav_item.dart` | `_NavItemPill` (`36px` row + `3px` accent bar) | `R,H,T` | Good visual look, but **missing keyboard focus (`Stop` / `FocusableActionDetector`)**—cannot Tab or Arrow through sidebar items! |
| 35 | `statCard` | `StatCardNode(Id, Label, Value, Delta?, Tone)` | `stat_card.dart` | `UiKitSurface` + `ViewBadge` | `R,T` | Redundant subset of `MetricTileNode` (`metricTile` already handles label, value, delta, status pill, sparkline, threshold, actions). Consolidate (`UK-14`). |
| 36 | `webBrowser` | `WebBrowser(Key, Url, Title?, Height)` | `web_browser.dart` | `UiWebBrowser` | `R,L,E` | Hosted surface (`WebView2` on Windows, stub/link on web). Retain as hosted surface primitive. |
| 37 | `conversation` | `Conversation(Id, Messages, Composer, Waiting?, Stop?, Error?, Empty?)` | `conversation.dart` | `ConversationView` (`817` lines) | `R,H,F,D,L,E,K,T` | High-craft Ino chat surface (`PromptScope` dock, `GptMarkdown`, `_ActivityPill`, stale `409` auto-retry), **but monolithic (`UK-18`)**: message stream, markdown bubble, activity trace pill, composer, and docked prompt slot cannot be reused independently in Circle rooms, Creator chat, or app sidebars. |
| 38 | `errorLine` | `ErrorLine(What, Detail?, Action?)` | `error_line.dart` | `ErrorLineView` | `R,E,T` | Clean `DESIGN.md` §14.7 inline error with recovery CTA (`Btn(small: true)`). |
| 39 | `emptyState` | `EmptyState(Title, Body?, Suggestions?)` | `emptyState` (`empty_state.dart`) | `EmptyStateView` (`PromptKeys`) | `R,H,F,K,T` | Hardcodes `IcAppIcon(appId: 'ino', size: 48)` (`empty_state.dart:35`) even when used inside non-Ino apps! Needs configurable `Icon` / `AppId` (`UK-13`). |
| 40 | `metricTile` | `MetricTileNode(Id, AppId, Title, Value, Delta?, Sparkline?, Threshold?, ...)` | `metric_tile.dart` | `MetricTileView` (`SparklinePainter`) | `R,H,T` | Rich Canvas sparkline + hover crosshair + threshold rule. Extract `Sparkline` as a standalone primitive (`UK-14`) and unify `StatCardNode` into `MetricTile`. |
| 41 | `diffCard` | `DiffCardNode(Id, Repo, TargetBranch, HeadSha, FilesChanged, Additions, Deletions, Files)` | `diff_card.dart` | `DiffCardView` | `R,H,T` | Collapsible per-file unified diff viewer; lacks keyboard `Stop` on file expand toggles. |

---

## 3. Forensic Audit: What Commit `1c15af1db` & `16f7fe086` Removed for `Chart` & `DataTable` Interactivity

Inspecting git commits `1c15af1db` (*"Remove V1 runtime and unify V2 integrations"*, 2026-07-10), `7df6471eb` (*"feat(apps): close e2e gaps across Ino, Friends, Gallery..."*, 2026-10-29), and `16f7fe086` (*"feat(os): land DigitalBrain 0.18.0 UI Views..."*, 2026-11-08) reveals a three-step regression that left charts and tables passive:

1. **Before `1c15af1db` (V1 `UiGatewayService` & `InoNeuron` dynamic `UiWidgetTree`)**:
   - `InoNeuron` emitted live `UiKitVocabulary.Panel` / `Table` / `Chart` / `Button` widget surfaces (`InoNeuronTabularDataTests`, `InoNeuronGraphCanvasTests`) where clicking a chart segment, switching chart type, or filtering table columns mutated server-held widget state and cross-filtered sibling views.
   - `1c15af1db` deleted `src/Modules/Shell/Flutter/Services/UiGatewayService.cs` (`-707` lines) and the V1 `InoNeuron` tabular/chart surface tests to unify on V2 neurons.
2. **Interim Widget Neurons (`7df6471eb` -> deleted in `16f7fe086`)**:
   - `7df6471eb` reintroduced `IChart` / `ChartNeuron` (`bar`, `line`, `pie` switching + `fl_chart` `UiChart` in `ui/lib/src/components/chart/ui_chart.dart`) and `ITable` / `TableNeuron` (`UiDataTable` with server sort, 9 filter operators, column visibility picker, and pagination).
   - When `16f7fe086` unified everything onto `DigitalBrain.UI.View` ("Units all the way"), `IChart` and `ITable` neurons were deleted (`OneUiPathFacts.cs`), **but `ViewModel.cs` replaced them with passive display-only `ChartNode` and `DataTableNode` records**:
     - `ChartNode` (`ViewModel.cs:666-679`) has **no `Key`, no `OnSelectPoint` `Emit`, no `OnSwitchKind` `Emit`, and only a flat `IReadOnlyList<ChartPointNode> Points`** (cannot even represent multi-series lines/bars or donut slices!).
     - `ui/lib/src/nodes/chart.dart` maps `ChartNode` to `ChartCard` (`design/cards/chart_card.dart`), leaving `onOpen: null`, `onRetry: null`, `onSwitchChartKind: null`, and `onCopyNeuronUri: null`! Worse, `chart.dart:11` maps only `'bar' || 'columns' => ChartKind.columns` and `_ => ChartKind.line`, so clicking **"Pie"** in `DataStudioApp` (`DataStudioViewRenderer.cs:38`) silently draws a **line chart**!
     - Meanwhile, `ui/lib/src/components/chart/ui_chart.dart` (`fl_chart` with `bar`/`line`/`pie`) and `ui/lib/src/components/table/ui_data_table.dart` (`UiDataTable` with sort/filter/pagination) sit **orphaned in the package**, unreachable from `NodeRenderer`!

---

## 4. Missing Atomic Primitives & Shell-Bypass Gaps (`UK-6`..`UK-19`)

### 4.1 Missing Atomic Controls (Force Apps into Workarounds Today)

| Family | Missing Primitives in `DigitalBrain.UI` + `os/ui` | Current Workaround in Apps | Target Ticket |
| :--- | :--- | :--- | :--- |
| **Composition** | Live `Slot(neuronUri)` for `kind == "view"` with natural sizing, skeleton, error boundary, and depth/cycle guard | Apps inline giant monolithic `View` trees (`SettingsApp`, `CreatorApp`) because `ViewHost._referable` rejects `'view'` | `UK-6` |
| **Layout** | `ResponsiveSplit`, `HeaderBar`, `Panel` (macOS grouped settings rows), `ScrollArea`, `InspectorDrawer`, `AdaptiveGrid(minTileWidth)` | `AppShellNode` hardcodes `240px`/`280px` columns; `SettingsApp` manually nests `Surface` + `Divider` | `UK-9`, `UK-11` |
| **Inputs** | `Checkbox`, `RadioGroup`, `Slider`, `NumberField` (stepper), `SearchField` (debounced `Arg.Text` + clear), `SecretField` (reveal toggle + zero-echo), `SegmentedControl` (pill thumb), `Form` (atomic multi-field command submit + `Rule` field errors) | Apps abuse `Button` rows as fake toggles/radios (`DataStudioViewRenderer.cs:36-38`) and single-field `OnSubmit` without form validation | `UK-10`, `UK-12` |
| **Navigation & Overlays** | `BreadcrumbBar` (wired as a `Node`—currently Dart-only `IcBreadcrumbBar`!), `DropdownMenu`, `ContextMenu`, `Modal` / `ConfirmDialog`, `Sheet` / `Drawer`, `Popover`, `CommandPalette` node hook | `IcBreadcrumbBar` & `IcFilesFinderView` (`1,337` lines in `ui/lib/src/controls/`) live as bespoke Dart widgets outside `NodeRenderer`! | `UK-11`, `UK-12` |
| **Feedback** | `Banner` (inline action), `Skeleton` (`shimmer`/`pulse`), `Spinner` (`WorkingSpinner` node), `Callout`, `Tooltip` (including `.Guard(Rule)` disabled-reason tooltip!) | Disabled buttons give zero explanation when `Rule` fails (`BtcAlert.cs:113`) | `UK-7`, `UK-13` |
| **Data & Viz** | Interactive `Table<TRow>` (sort, filter, row click, numeric `tnum` alignment, pagination), `BarChart`, `LineChart`, `AreaChart`, `DonutChart` (fixing broken `"pie"`!), `Sparkline`, `DescriptionList`, `TreeView`, `CodeBlock`, `Timeline` | Passive `TablePreviewCard` + passive `ChartCard` + broken Pie mode | `UK-14` |
| **Creator / Accordant** | `StateGraphView` (2D interactive state-space node/edge canvas with `SameState` self-loops & trace highlight), `ScenarioMatrix` | Only Flame 3D/2D sphere `BrainSceneNode` exists; cannot render unfolded Accordant state graphs cleanly | `UK-8`, `UK-15` |

### 4.2 Bespoke Dart Widgets Bypassing `NodeRenderer`

- `ui/lib/src/controls/ic_files_finder_view.dart` (`1,337` lines) + `ic_files_finder_inspector.dart` + `ic_breadcrumb_bar.dart` (`323` lines): Built as custom Flutter widgets with `30+` callback props (`onUpload`, `onNewFolder`, `onRename`, ...) instead of composable `DigitalBrain.UI` nodes (`ResponsiveSplit` + `BreadcrumbBar` + `TreeView`/`Table`/`AdaptiveGrid` + `InspectorDrawer`).
- `ui/lib/connections/connections_settings.dart`, `connect_window.dart`, `windows_storage_setup.dart`, `workspace/compute_usage_panel.dart`: Client-side bespoke panels that duplicate layout/form patterns instead of rendering pure `Screen<TState>` `Kit` views.
