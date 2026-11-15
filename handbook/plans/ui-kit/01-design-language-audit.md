# UK-1 · Design Language Audit (`DESIGN.md` & `SPEC.md` vs `os/ui`)

> **Linear Issue**: `INT-1349` (`UK-1`) · **Project**: `P-INT-7` (`UI Kit`)
> **Canonical Sources**: `E:\intochat\design\DESIGN.md` (Warm Ink & Paper design language) & `E:\intochat\design\SPEC.md` (OS desktop & keyboard-first specification)
> **Audited Implementation**: `E:\intochat\os\ui\lib\src\theme\*`, `E:\intochat\os\ui\lib\src\design\*`, `E:\intochat\os\ui\lib\src\components\kit\*`, `E:\intochat\os\ui\lib\src\nodes\*`, `E:\intochat\os\ui\lib\workspace\workspace_app.dart`

---

## 1. Design Philosophy & Non-Negotiable Principles

IntoChat OS is a **personal computer powered by AI** whose human companion is **Ino**. Every screen—whether built by the OS team or synthesized on the fly by Ino—must obey eleven core rules (`DESIGN.md` §1, §18):

1. **Warm Ink & Paper**: Warm off-white paper (`#FBFAF7`) and soft charcoal ink (`#17140F`) in light mode; warm espresso (`#13110E`) and cream (`#F5F2EC`) in dark mode. Pure `#FFFFFF` is reserved **only** for elevated cards (`surfaceCard`) in light mode and `onAccent` / `onInk` foregrounds—never as a page canvas. Pure `#000000` never appears as a surface or text token.
2. **Colour Carries Status, Not Decoration**: Blue (`accent`), green (`done`), amber (`working`), and red (`error`) mean one thing each. Every status indicator pairs **colour + Lucide icon + plain English word**—never colour alone.
3. **One Obvious Next Step**: Cards and dialogs present **1 primary CTA** (`Btn(..., primary: true)` / `PrimaryButton`) and at most **1 secondary action** (`Cancel`, `Skip`, `Not now`). Three+ actions collapse into a `More` menu.
4. **Windows Are Opaque; Only Floating Chrome Blurs**: App windows (`surfacePage` + `surfaceCard`) have **zero backdrop blur** (`blurSigma = 0`). Backdrop blur is strictly restricted to OS chrome (`osBar` `sigma = 16`, Dock / Quick View / Switcher / Popover `sigma = 30`).
5. **Plain Human Words, Zero Internal Plumbing**: Never surface `neuron`, `synapse`, `kernel`, `grain`, `silo`, `orleans`, `signal`, `Ask`, `payload`, `schema`, `JSON`, `YAML`, `SQL`, `CLI`, `cron`, `regex`, `MCP`, `OAuth`, `token` (except compute spend), `API` (say *service* or *connection*), `webhook`, `agent`, `LLM`, `prompt`, `inference`, `pipeline`, or raw stack traces (`DESIGN.md` §15.3). Only **Creator** (`apps/creator`) and explicit developer copy actions may expose `neuron://` URIs.
6. **No Monospace Anywhere in Product UI**: `Inter` + `InterDisplay` only. Identifiers, currency, timestamps, counts, and tabular cells use `Inter` with OpenType `tnum` (`FontFeature.tabularFigures()`), never `Consolas`, `Cascadia Mono`, or `JetBrains Mono` (`DESIGN.md` §1.10, §3.1, §18). *(Scoped exception: unified code hunks inside Creator/PR `DiffCard` must still avoid leaking monospace into standard chrome, labels, or SQL/URI pills.)*
7. **Desktop-Dense Rhythm**: Standard interactive row height `36px` (compact `30px`), single-line inputs `36px`, segmented controls `32px`, window titlebar `38px`, bottom OS bar `48px`, `4px` base spacing grid (`DESIGN.md` §6–§7).
8. **Keyboard-First Prompting**: When Ino asks a question, seeks approval, or requests a secret, the card **docks in the composer slot** (`PromptScope(docked: true)` + `PromptKeys`) with `1..9` instant digit shortcuts, `↑`/`↓` stop navigation, `↵` default action, `Esc` -> `"Chat about this"`, and a `1.5px accent + 3px ring` `SelectionRing` (`DESIGN.md` §1.11, §14.8; `SPEC.md` §7).
9. **Approval Gate Is Sacred (`Gate 2.0`)**: Money movement, external messages, destructive file/record mutations, and credential grants render an `ApprovalCard` with structured diff rows (`oldValue → newValue`) and explicit `Approve` / `Reject` (`Resolve` person-only kernel boundary). AI/MCP automation cannot self-approve (`DESIGN.md` §14.3, §18).
10. **Secrets Never Echo**: Secret inputs write straight to encrypted storage, never echo into chat transcripts or `Field` readbacks, and collapse to `Saved in Settings → Connections` (`DESIGN.md` §14.6, §18).
11. **Errors Always Name What Happened + Next Action**: Every `ErrorLine` / `Banner` states what failed in one plain sentence and offers a single concrete recovery button (`Try again`, `Reconnect`, `Open Settings`).

---

## 2. Canonical Colour Tokens (`IcColors` / `UiColors`)

All values below are normative from `DESIGN.md` §2.1–§2.10. No raw `Color(0x...)` or `Colors.*` literals are permitted outside the single token file (`UK-5`).

### 2.1 Surfaces (`DESIGN.md` §2.1)

| Semantic Token (`IcColors`) | `UiColors` Alias | Light Hex | Dark Hex | Normative Usage |
| :--- | :--- | :--- | :--- | :--- |
| `surfacePage` | `page` | `#FBFAF7` | `#13110E` | Window interior canvas, Ino conversation background |
| `surfaceCard` | `surface` | `#FFFFFF` | `#1C1916` | Cards, panels, inputs, dialogs, popovers, inactive buttons |
| `surfaceSunk` | `recessed` | `#F3F1EC` | `#0E0C0A` | Sidebars, segmented tracks, table headers, hover fills, code/log wells |
| `surfaceInk` | *(missing — add)* | `#17140F` | `#F5F2EC` | User chat bubble fill (`onInk` foreground), inverted callouts |
| `onInk` | *(missing — add)* | `#FBFAF7` | `#17140F` | Text/icons on `surfaceInk` (and toast foreground) |

### 2.2 Text Hierarchy (`DESIGN.md` §2.2)

| Semantic Token (`IcColors`) | `UiColors` Alias | Light Hex | Dark Hex | Contrast vs `surfacePage` | Normative Usage |
| :--- | :--- | :--- | :--- | :--- | :--- |
| `textStrong` | `strong` | `#17140F` | `#F5F2EC` | `16.4 : 1` (L) / `15.8 : 1` (D) | Headings, primary labels, active nav, table keys, numbers |
| `textBody` | `body` | `#3D3830` | `#CFC9BF` | `10.6 : 1` (L) / `10.9 : 1` (D) | Assistant prose, card descriptions, settings explanations |
| `textMuted` | `muted` | `#746D63` | `#918A7F` | `4.8 : 1` (L) / `5.4 : 1` (D) | Secondary captions, timestamps, column headers, inactive tabs |
| `textSubtle` | `subtle` | `#9E968B` | `#666057` | `2.9 : 1` (non-essential only) | Input placeholders, disabled hints, keyboard shortcut glyphs (`⌘K`) |

### 2.3 Borders & Focus Ring (`DESIGN.md` §2.3)

| Semantic Token (`IcColors`) | `UiColors` Alias | Light Value | Dark Value | Normative Usage |
| :--- | :--- | :--- | :--- | :--- |
| `borderHairline` | `hairline` | `#E8E4DC` | `#2A2621` | `1px` card borders, row dividers, sidebar/titlebar rules |
| `borderInput` | `inputBorder` | `#D5CFC4` | `#3A352E` | Resting input, select, and secondary button borders |
| `borderStrong` | `borderStrong` | `#B8B0A2` | `#524B42` | Switch off-thumb, hover input border, emphasized dividers |
| `ring` | `focus` | `rgba(31, 94, 255, 0.28)` (`#471F5EFF`) | `rgba(75, 125, 255, 0.35)` (`#594B7DFF`) | `3px` outer focus halo around `1.5px` `accent` stroke |

### 2.4 Accent (`DESIGN.md` §2.4)

| Semantic Token (`IcColors`) | `UiColors` Alias | Light Value | Dark Value | Normative Usage |
| :--- | :--- | :--- | :--- | :--- |
| `accent` | `accent` | `#1F5EFF` | `#4B7DFF` | Primary CTA fill, active switch track, active tab bar, focus border, links |
| `accentHover` | `accentHover` | `#174EE0` | `#6690FF` | Primary CTA hover fill |
| `accentTint` | `accentTint` | `#EBF1FF` | `rgba(75, 125, 255, 0.16)` (`#294B7DFF`) | Selected sidebar/list row fill, neutral/accent tag bg, `PromptOptions` selection |
| `onAccent` | `onAccent` | `#FFFFFF` | **`#FFFFFF`** | Foreground ink/icons on `accent` primary buttons & active switch thumbs |
| `accentOnInk` | *(missing — add)* | `#93B4FF` | `#1F5EFF` | Links rendered inside `surfaceInk` user chat bubbles |

### 2.5 Semantic Status Triads: Colour + Icon + Word (`DESIGN.md` §2.5, §9.3)

| Status | `fg` Light | `fg` Dark | `bg` Light | `bg` Dark (`16%` alpha) | Required Lucide Icon | Canonical Plain Words |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| **Done / Connected** | `#0E7A3E` | `#3CCB7F` | `#E6F5EC` | `rgba(18, 161, 80, 0.16)` (`#2912A150`) | `LucideIcons.check` / `circleCheck` | `Connected`, `Saved`, `Sent`, `Approved`, `Done` |
| **Working / Pending** | `#A86500` | `#F5B13C` | `#FDF2DC` | `rgba(232, 163, 23, 0.16)` (`#29E8A317`) | `WorkingSpinner` (animated) or `clock` (waiting on person) | `Working…`, `Sending…`, `Waiting for approval`, `Paused` |
| **Needs Attention / Error** | `#C73524` | `#FF6B5B` | `#FDECE9` | `rgba(224, 64, 47, 0.16)` (`#29E0402F`) | `LucideIcons.circleAlert` / `triangleAlert` | `Needs attention`, `Couldn't connect`, `Rejected`, `Failed` |
| **Neutral / Info** | `textMuted` | `textMuted` | `surfaceSunk` | `surfaceSunk` | Context glyph (`info`, `lock`, `sparkles`) | `Draft`, `Skipped`, `Archived`, `Read-only` |

### 2.6 OS Chrome, Overlays, Desktop Wallpaper Tones & Data Series (`DESIGN.md` §2.6–§2.9)

| Group | Token | Light Value | Dark Value | Notes |
| :--- | :--- | :--- | :--- | :--- |
| **OS Chrome** | `chromeSurface` | `rgba(251, 250, 247, 0.78)` | `rgba(24, 21, 18, 0.78)` | Floating Dock, Quick View, Switcher (`blur = 30`) |
| **OS Chrome** | `chromeBar` | `rgba(251, 250, 247, 0.86)` | `rgba(20, 18, 15, 0.86)` | Bottom OS bar (`blur = 16`, height `48px`) |
| **OS Chrome** | `chromeBorder` | `rgba(23, 20, 15, 0.08)` | `rgba(255, 255, 255, 0.09)` | `1px` hairline on blurred chrome |
| **Scrim** | `scrim` | `rgba(15, 13, 10, 0.32)` | `rgba(0, 0, 0, 0.55)` | Modal/Quick View/Switcher backdrop |
| **State Overlays** | `hoverOverlay` | `rgba(23, 20, 15, 0.04)` | `rgba(245, 242, 236, 0.05)` | Subtle row/icon hover where `surfaceSunk` is already background |
| **State Overlays** | `selectedOverlay` | `rgba(23, 20, 15, 0.07)` | `rgba(245, 242, 236, 0.09)` | Pressed / non-accent active state |
| **Desktop Tones** | `Cream` / `Sand` / `Stone` / `Slate` / `Forest` | `#EDE8DF`→`#DDD6C9` / `#E8DFD1`→`#D4C7B3` / `#E2E1DD`→`#CECCC6` / `#DCE2EA`→`#C5CFDC` / `#DCE5DF`→`#C4D2C9` | `#171410`→`#0F0D0A` / `#1B1611`→`#110E0A` / `#161615`→`#0E0E0D` / `#12161D`→`#0B0E13` / `#111815`→`#0A0F0D` | 5 subtle vertical linear gradients (`DESIGN.md` §2.7) |
| **Chart Series** | `series[0..4]` | `accent` (`#1F5EFF`), `#0E8A72` (teal), `#8B5CF6` (iris), `#D97706` (ochre), `#6E675E` (`Other`) | `accent` (`#4B7DFF`), `#2DD4B4`, `#A78BFA`, `#F59E0B`, `#8E867B` (`Other`) | Max 4 named series + `Other`; single-series uses `series[0]` (`accent`) |

---

## 3. Typography, Spacing, Radii, Elevation & Motion Rules

### 3.1 Typography Scale (`DESIGN.md` §3.1–§3.5)

- **Families**: `Inter` (`IcType.text` — body, controls, UI chrome, tables, numbers) and `InterDisplay` (`IcType.heading` — `20px+` headings only).
- **19 Allowed Font Sizes Only**: `10, 10.5, 11, 11.5, 12, 12.5, 13, 13.5, 14, 15, 16, 17, 18, 20, 22, 24, 26, 28, 36`. No other font size is legal.
- **Canonical Roles**:

| Role | Family | Size | Weight | Line Height | Tracking | Features / Casing |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `Hero Metric` | `InterDisplay` | `36` | `w700` | `1.10` | `-0.90` | `tnum` |
| `Empty Greeting` | `InterDisplay` | `26–28` | `w700` | `1.15` | `-0.60` | Sentence case |
| `Page / Section H1` | `InterDisplay` | `20–24` | `w700` | `1.20` | `-0.45` | Sentence case |
| `Dialog / Card Title H2` | `Inter` | `15–16` | `w600` | `1.30` | `-0.20` | Sentence case |
| `Card Header / Row Strong` | `Inter` | `13.5–14` | `w600` | `1.35` | `-0.10` | Sentence case |
| `Conversation Prose` | `Inter` | `14` | `w400` | `1.55` | `0` | Max measure `68ch` (`680–760px`) |
| `App Body / Cell / Input` | `Inter` | `13–13.5` | `w400` | `1.40` | `0` | `tnum` on numeric columns |
| `Button / Segmented / Nav` | `Inter` | `13` | `w600` (active) / `w500` | `1.00` | `-0.05` | Sentence case (2–3 words) |
| `Field Label` | `Inter` | `13` | `w600` | `1.25` | `-0.05` | Sentence case |
| `Caption / Meta / Helper` | `Inter` | `12–12.5` | `w400` / `w500` | `1.35` | `0` | `textMuted` |
| `Pill / Badge / Keycap` | `Inter` | `11.5–12` | `w600` | `1.00` | `0` | `tnum` on counts/keycaps |
| `Sidebar Group Eyebrow` | `Inter` | `11` | `w600` | `1.20` | `+0.55` | **ONLY** place `UPPERCASE` is allowed (`DESIGN.md` §3.4) |

### 3.2 Spacing, Radii, Elevation & Motion (`DESIGN.md` §4–§8)

- **Spacing Scale (`IcSpace`)**: `2, 4, 6, 8, 10, 12, 14, 16, 20, 24, 32, 40, 56` px (`4px` base grid; `6/10/14` half-steps solely for control padding).
- **Radius Scale (`IcRadius`)**: `xs = 6` (tags, keycaps, tooltips, inner segmented thumb), `sm = 8` (sidebar nav rows, menu items), `button = 10` (buttons, inputs, selects, segmented track, toast), `card = 14` (inline chat cards, settings panels), `panel = 16` (composer box, Quick View, modals), `window = 16` (desktop app windows), `bubble = 18` (user message bubble, bottom-right `6`), `pill = 999` (switches, avatar circles, status dots). **Concentric nesting rule**: `outerRadius = innerRadius + padding`.
- **Shadows (`IcShadow`)**: Warm ink-tinted in light (`#17140F`), higher-alpha black in dark (`#000000`) **always paired with `1px borderHairline`** in dark mode:
  - `xs`: `0 1px 2px` (`4%` L / `22%` D) — cards, inputs, secondary buttons.
  - `sm`: `0 2px 6px` + `0 1px 2px` (`6%/4%` L, `30%/20%` D) — composer, popovers.
  - `md`: `0 8px 24px` + `0 2px 6px` (`10%/5%` L, `42%/24%` D) — menus, floating chrome, toast.
  - `window`: `0 24px 60px` (`16%` L / `58%` D) + `0 4px 12px` — active window (`50%` weaker when unfocused).
- **Responsive Breakpoints (`DESIGN.md` §7.2)**:
  - `>= 1024 × 640`: Full multi-window desktop + floating chrome.
  - `< 1024` width or `< 640` height: **Focus Mode** — windows auto-maximize above `48px` OS bar; `SplitView` / `ResponsiveSplit` collapses sidebar into a toggleable drawer/back-stack below `720px`.
  - `< 480` width: Centered desktop-minimum guard screen.
- **Motion (`IcMotion`, `DESIGN.md` §8)**:
  - `fast = 120ms` (hover, press `1px` translate, switch thumb, selection ring), `norm = 200ms` (popover, modal, drawer, tab underline), `slow = 320ms` (window zoom/open), `ease = Cubic(0.2, 0.8, 0.2, 1.0)`.
  - Continuous loops: `WorkingSpinner` `900ms linear`, `InoMark` breathing `2600ms easeInOut`.
  - **Reduced Motion**: When `MediaQuery.disableAnimationsOf(context)` or user preference `reducedMotion` is true, durations collapse to `Duration.zero`, `WorkingSpinner` renders a static status icon, and `InoMark` stops pulsing.

---

## 4. Keyboard-First & Docked Prompt Specification (`DESIGN.md` §1.11, §14.8; `SPEC.md` §7)

1. **Global OS Shortcuts**:
   - `Ctrl/Cmd + K` (or `/` when no text field is focused): Open **Quick View** command/question palette (`560px` wide, top `18%` of viewport).
   - `Ctrl/Cmd + 1..9`: Activate pinned app `1..9` on the OS bar.
   - `Alt/Cmd + Tab` / `Ctrl + Tab`: Cycle open windows; `Ctrl/Cmd + W`: Close focused window.
   - `Esc`: Dismiss topmost popover/Quick View, stop running Ino turn (`stop` bar), or jump from docked prompt to `"Chat about this"`.
2. **Docked Prompt Mechanics (`PromptScope` + `PromptKeys` + `Stop`)**:
   - Whenever `ConversationNode.waiting` carries an interactive prompt (`ChoiceCard`, `ApprovalCard`, `SecretCard`, or structured `Form`), the composer hides and the card mounts **inside the bottom composer dock** with `autofocus: true`.
   - `PromptKeys` orders all live `Stop` descendants top-to-bottom, left-to-right (`dy > 4px` threshold).
   - **Key bindings inside `PromptKeys`**:
     - `↑` / `↓` (and `Tab` / `Shift+Tab`): Move `SelectionRing` across stops and scroll into view.
     - `←` / `→`: Adjust value on stops with `onArrow` (e.g., `SegmentedControl`, numeric stepper), otherwise move previous/next.
     - `1..9`: Immediately select and activate numbered `PromptOption` `1..9`.
     - `Enter` / `Space`: Activate currently selected `Stop`; `Ctrl/Cmd + Enter` activates the default (`isDefault: true`) primary action from anywhere in the group.
     - `Esc`: Activate the `"Chat about this"` stop (`chat: true`), returning focus to the text composer so the user can ask a clarifying question or amend parameters before deciding.
   - Every `PromptOptions` list **automatically appends** `"Chat about this"` (`Ask or tell Ino something first`) as option `N + 1` and renders the footer hint `'↑↓ to move · ↵ to choose · Esc to chat'` when keyboard-focused.

---

## 5. `os/ui` Drift Audit (Current Implementation vs Canonical Spec)

| # | Severity | File & Line(s) in `os/ui` | Observed Drift | Required Fix (Target Ticket) |
| :--- | :--- | :--- | :--- | :--- |
| **D-01** | **P0 Bug (Contrast)** | `ui/lib/src/theme/ui_colors.dart:287-288` | `UiColors.intochatDark` sets `onAccent: Color(0xFF17140F)` (dark charcoal!) instead of `#FFFFFF`, rendering dark text on `#4B7DFF` primary buttons in dark mode (`3.9:1` muddy ink instead of crisp white). Also `accentCopy` is `#FBFAF7` instead of `#4B7DFF`. | Set `intochatDark.onAccent = Color(0xFFFFFFFF)` and `accentCopy = Color(0xFF4B7DFF)` (`UK-5`). |
| **D-02** | **P1 Token Drift** | `ui/lib/src/theme/ui_colors.dart:292,294,296` | `UiColors.intochatDark` status backgrounds use pastel tints (`0x298FE0AF`, `0x29F6D28A`, `0x29F3A99F`) instead of `DESIGN.md` §2.5 dark status fills (`#12A150`, `#E8A317`, `#E0402F` at `16%` -> `0x2912A150`, `0x29E8A317`, `0x29E0402F`). Note: `design/tokens.dart:570-574` already has the right hexes—the two token files diverged! | Unify `UiColors` and `IcColors` onto one canonical source of truth (`UK-5`). |
| **D-03** | **P1 Identity Drift** | `ui/lib/workspace/workspace_app.dart:586-605`, `ui/lib/src/design/tokens.dart:70-124`, `ui/lib/src/theme/ui_colors.dart:145-580` | `_effectiveTheme` forcefully overrides `'intochat'` with `'fluent-acrylic'` on the desktop root (`workspace_app.dart:601-604`)! Meanwhile 7 parallel style presets (`fluentAcrylic`, `spatialGlass`, `cyberSynaptic`, `warmPaper`, `fluentWarmMica`, `linearCarbon`, `nordicTactical`) + 3 legacy v1 palettes (`midnight`, `paper` green `#397B63`, `lumen`) coexist, with `windowOpacity: 0.82` and `blurSigma: 24` violating `DESIGN.md` §1.6 / §5.2 ("Windows are opaque"). | Make **Warm Ink & Paper** (`warmPaper` / `intochatLight` + `intochatDark`) the single canonical product theme; keep windows `100%` opaque (`blurSigma = 0` on `surfacePage`/`surfaceCard`) (`UK-5`). |
| **D-04** | **P1 Legacy Palette Leaks** | `ui/lib/src/theme/ui_theme.dart:243-288, 555` & `ui/lib/src/components/chart/ui_chart.dart:118-193` | `UiChatTheme.light()` hardcodes `UiColors.paper` (`#397B63` green!), `UiChatTheme.dark()` and `UiTheme.baseDark()` hardcode `UiColors.midnight` (`#141720` / `#7B9BE3`), and `ui_chart.dart` hardcodes `UiColors.paper` for bar/line/pie series! | Purge `UiColors.paper` / `UiColors.midnight` defaults; wire `IcColors.series` (`accent`, teal, iris, ochre, `Other`) (`UK-5`, `UK-14`). |
| **D-05** | **P1 Monospace Violation** | `ui/lib/src/theme/ui_theme.dart:172-177, 198-219`, `ui/lib/src/design/tokens.dart:642-653`, `ui/lib/src/controls/ic_breadcrumb_bar.dart:160`, `DigitalBrain.UI/ViewModel.cs:233`, `DataStudioViewRenderer.cs:33` | `monoFallback = ['Cascadia Mono', 'Consolas', 'Menlo', 'monospace']` is applied to `_meta`, `_metaStrong`, and `_menuLabel` (with `letterSpacing: 0.45 / 0.88`); `IcType.code` uses `JetBrains Mono` in `IcBreadcrumbBar` `neuron://` pills; `DataStudioViewRenderer` sets `Text.Mono = true` on SQL queries. Violates `DESIGN.md` §1.10 / §3.1 / §18. | Replace `monoFallback` and `IcType.code` in chrome/tables/SQL/URI pills with `Inter` + `tnum`; deprecate `Text.Mono` in favour of `Tabular: true` (`UK-5`, `UK-24`). |
| **D-06** | **P2 Control Geometry & Focus Drift** | `ui/lib/src/components/kit/ui_kit_field.dart:101,164`, `ui/lib/src/components/kit/ui_kit_select.dart:238,248`, `ui/lib/src/theme/ui_tokens.dart:109`, `ui/lib/src/theme/ui_theme.dart:445,519` | `UiTokens.fieldRadius` is `12` (`DESIGN.md` §4 / §11.3 specifies `IcRadius.button = 10` for inputs/selects); `UiKitSelect` height is `48px` instead of desktop `36px` (`DESIGN.md` §6 / §11.4); `UiKitTabs` uses `12px w400/w700` instead of `13px w500/w600`; `UiTheme.tooltipTheme` uses radius `8` instead of `xs = 6` and lacks dark-mode `surfaceCard + borderHairline` (`DESIGN.md` §10.8); `UiKitField` focus border stays `1px` instead of `1.5px accent` inside `3px ring`. | Align `UiKitField`, `UiKitSelect`, `UiKitTabs`, and `Tooltip` geometry & focus rings to `DESIGN.md` §6, §10.8, §11 (`UK-5`, `UK-10`, `UK-12`). |
| **D-07** | **P2 Missing `DESIGN.md` §20 Tokens** | `ui/lib/src/design/tokens.dart` & `ui/lib/src/theme/ui_tokens.dart` | Prototype backlog items from `DESIGN.md` §20 remain open in `os/ui`: missing `surfaceInk`/`onInk`/`accentOnInk` on `UiColors`, missing `hoverOverlay`/`selectedOverlay`, missing `IcSpace` (`2..56`) & `IcBlur` (`chrome=30, bar=16, window=0`) constants, missing reduced-motion short-circuit in `WorkingSpinner` and `AnimatedContainer` wrappers, and `SurfaceNode('acrylic')` (`nodes/surface.dart:23-28`) blurring inside app windows! | Add missing token classes in `UK-5`; clamp `SurfaceNode` inside app windows to `blurSigma = 0` (`DESIGN.md` §5.2). |
| **D-08** | **P2 Raw Material Widgets in Data Table** | `ui/lib/src/components/table/ui_data_table.dart:92-197, 342-363, 463-645` | `UiDataTable` uses stock Material `AlertDialog`, `DropdownButtonFormField`, `CheckboxListTile`, `InputChip`, `FilledButton`, `TextButton`, and `Icons.*` instead of Lucide icons and Warm Ink & Paper `Modal` / `Popover` / `SelectBox` / `Checkbox` / `FilterChip` primitives. | Rebuild `Table` on canonical Kit primitives in `UK-14`. |

---

## 6. Acceptance Gate for `UK-5` (Tokens & Theme Alignment)

Before Phase 2 component migration closes `UK-5`, `flutter test` + architecture lints must verify:
1. Single token source (`IcColors` + `IcType` + `IcSpace` + `IcRadius` + `IcShadow` + `IcBlur` + `IcMotion`) with `UiColors` as a lossless compatibility projection until legacy callers are retired.
2. `intochatDark.onAccent == #FFFFFF` and dark status backgrounds equal `0x2912A150` / `0x29E8A317` / `0x29E0402F`.
3. Zero `monoFallback` in `ui_theme.dart`, zero `UiColors.paper` / `UiColors.midnight` references in active components, and `blurSigma == 0` on all window interior surfaces.
4. Automated lint/fact rejecting raw `Color(0x...)` and `Colors.*` outside `ui/lib/src/theme/` and `ui/lib/src/design/tokens.dart` (except `Colors.transparent`).
