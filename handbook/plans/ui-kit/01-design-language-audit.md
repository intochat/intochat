# UK-1 · Design Language Audit (`DESIGN.md` & `SPEC.md` vs `os/ui`)

> **Linear Issue**: `INT-1349` (`UK-1`) · **Project**: `P-INT-7` (`UI Kit`)
> **Canonical Sources**: `E:\intochat\design\DESIGN.md` & `E:\intochat\design\packages\ui\lib\src\tokens.dart` (Warm Ink & Paper design language) and `E:\intochat\design\SPEC.md` (OS desktop & keyboard-first specification)
> **Audited Implementation**: `E:\intochat\os\ui\lib\src\theme\*`, `E:\intochat\os\ui\lib\src\design\*`, `E:\intochat\os\ui\lib\src\components\kit\*`, `E:\intochat\os\ui\lib\src\nodes\*`, `E:\intochat\os\ui\lib\workspace\workspace_app.dart`

---

## 1. Design Philosophy & Non-Negotiable Principles

IntoChat OS is a **personal computer powered by AI** whose human companion is **Ino**. Every screen—whether built by the OS team or synthesized on the fly by Ino—must obey eleven core rules (`DESIGN.md` §1, §2.9, §18):

1. **Warm Ink & Paper (Plain Fills, Zero Gradients, Zero Purple)**: Warm off-white paper (`surfacePage` `#FBFAF7`) and warm charcoal ink (`textStrong` `#17140F`) in light mode; warm brown-black (`surfacePage` `#17140F`, `surfaceSunk` `#110F0B`, raised `surfaceCard` `#2A251E`) and warm cream (`textStrong` `#FBFAF7`) in dark mode. Pure `#FFFFFF` is reserved **only** for raised `surfaceCard` in light mode and `onAccent`. No gradients anywhere (except the composer's bottom scroll fade on `surfacePage`), no purple, no second accent, no decorative colour, and no photo wallpaper (`DESIGN.md` §1.3, §2.9, §18).
2. **One Accent for Action & Selection; Icons + Words for Status**: Blue (`accent` `#1F5EFF` light / `#4B7DFF` dark) means *"you can act here"* or *"this is selected"*. Status uses `done` (green), `working` (amber), `error` (red), `idle` (muted), and `needsYou` (`accent` + `hand` icon)—**always paired with its Lucide icon and plain English word, never colour dots alone** (`DESIGN.md` §1.5–§1.6, §2.5).
3. **Function First & One Obvious Next Step**: Every control states what it does before it is clicked. Each view, sheet, or card has **at most 1 primary CTA** (placed rightmost in footers; outcome verbs like `"Apply 3 changes"`, `"Review and install"`—never `"OK"`, `"Submit"`, `"Confirm"`, or `"Learn more"`, `DESIGN.md` §1.1, §11.1, §18).
4. **Windows Are Opaque; Only `ChromePanel` & Bar Blur**: Window interiors (`surfacePage`, `surfaceCard`) are `100%` opaque (`blurSigma = 0`). Translucent blur is strictly confined to floating OS `ChromePanel` surfaces (`chrome` at `90%`, `blur = 30`, `1px chromeBorder`, `IcShadow.xl`) and the bottom OS Bar (`chrome` at `90%`, `blur = 16`, top `chromeBorder`, `DESIGN.md` §6.2, §10.2).
5. **Plain Human Words, Zero Internal Plumbing**: Main screens use everyday language (`DESIGN.md` §1.9, §16). Never surface `neuron`, `synapse`, `kernel`, `grain`, `silo`, `orleans`, `signal`, `Ask`, `payload`, `schema`, `JSON`, `YAML`, `SQL`, `CLI`, `cron`, `regex`, `MCP`, `OAuth`, `webhook`, `agent`, `LLM`, `prompt`, `inference`, `pipeline`, or stack traces on consumer surfaces. Only **Creator** (`apps/creator`) and explicit developer copy actions expose `neuron://` URIs.
6. **One Font Family — Zero Monospace Anywhere**: `Inter` (`IcType.text`) everywhere and `InterDisplay` (`IcType.heading`) for headings `17px` and up. **No monospace anywhere**—not even for keys, codes, versions, SQL, or IDs (`DESIGN.md` §1.10, §3.1, §18). Changing or columnar numbers always use OpenType tabular figures (`tnum: true`).
7. **Desktop-Dense Rhythm (`DESIGN.md` §4.1–§4.2)**: Bottom OS Bar **`32px`** high, window title bar **`38px`** (min window `640 × 420`), Files toolbar & conversation header **`52px`**, Quick view header **`48px`**, Button / Input / Select **`36px`** (small button `30px`, share `40px`), Segmented **`3px` pad + `30px` segments**, `IconBtn` **`28px` box / `16px` icon** (`24/14` small), Switch **`36 × 20` (`14` thumb)**, Table **`38` header / `44` row / `48` footer**.
8. **Keyboard-First in Ino (`DESIGN.md` §1.11, §14.8; `SPEC.md` §7)**: Every card is a `PromptKeys` keyboard group of `Stop` targets. Cards waiting for an answer dock in the composer slot (`760px` max width, `IcRadius.panel = 20`, `IcShadow.lg`), render numbered `PromptOptions` (`1..9` instant keys, `↑`/`↓` move `SelectionRing`, `↵` choose, `⌘↵` first option), and always place **`"Chat about this"`** last (`Esc`).
9. **Chat Never Asks You to Fill Out a Form & `Gate 2.0` Approval Is Sacred**: Ino pre-fills values; users correct values in place (`26px` inline editor, edited text stays `accent`) and approve via `ApprovalCard` (`Resolve` person-only kernel boundary—AI/MCP automation cannot self-approve, `DESIGN.md` §1.7, §14.3, §14.7).
10. **Secrets Never Echo**: Credential inputs run in the dedicated Secure Window (`surfaceInk` top shield band) or write-only secret prompt, never echo into chat transcripts or `GET` state, and collapse to encrypted storage (`DESIGN.md` §14.7, §15.5).
11. **Errors State What Happened, What Didn't & One Next Step**: Plain human sentences (`"Salesforce stopped answering after 12 of 14 changes..."`) with a single recovery button (`Try again`, `Add credits`, `DESIGN.md` §14.5, §14.7, §16).

---

## 2. Canonical Colour Tokens (`IcColors` / `UiColors`)

All values below match `E:\intochat\design\DESIGN.md` §2.1–§2.9 and `E:\intochat\design\packages\ui\lib\src\tokens.dart` 1:1. No raw `Color(0x...)` or `Colors.*` literals are permitted outside `tokens.dart` (`DESIGN.md` §2.9, §19.5; `UK-5`).

### 2.1 Surfaces (`DESIGN.md` §2.1, §13.3, §20)

| Semantic Token (`IcColors`) | `UiColors` Projection | Light Hex | Dark Hex | Normative Usage (`DESIGN.md`) |
| :--- | :--- | :--- | :--- | :--- |
| `surfacePage` | `page` | `#FBFAF7` | `#17140F` | Window content background, Ino conversation, Settings content, Files content, share page |
| `surfaceSunk` | `recessed` | `#F2EFE9` | `#110F0B` | Recessed areas: sidebars, table header bands, segmented/switch tracks, skeletons, key caps, glyph wells |
| `surfaceCard` | `surface` | `#FFFFFF` | `#2A251E` | Raised surfaces: windows, cards, panels, inputs, sheets, toolbars, active rows (lighter than `surfacePage` in dark mode) |
| `surfaceInk` | `surfaceInk` *(add)* | `#17140F` | `#110F0B` | Inverted surfaces: light-mode tooltips & toasts, light user bubble, avatars, secure window top band, scrim base |
| `onInk` *(§20)* | `onInk` *(add)* | `#FBFAF7` | `#FBFAF7` | Paper text on `surfaceInk` (light tooltips/toasts, secure window top band) |
| `bubbleMine` / `onBubbleMine` *(§13.3, §20)* | *(add)* | `#17140F` / `#FBFAF7` | `#FBFAF7` / `#17140F` | User chat bubble fill & text (ink with paper text in light mode; paper with ink text in dark mode) |

### 2.2 Text Hierarchy (`DESIGN.md` §2.2)

| Semantic Token (`IcColors`) | `UiColors` Projection | Light Hex | Dark Hex | Normative Usage (`DESIGN.md`) |
| :--- | :--- | :--- | :--- | :--- |
| `textStrong` | `strong` | `#17140F` | `#FBFAF7` | Titles, labels, values, active items |
| `textBody` | `body` | `#2A251E` | `#F0ECE4` | Reading text, row text at rest (`>= 4.5:1` contrast) |
| `textMuted` | `muted` | `#7C7264` | `#9C9182` | Help lines, meta, `GroupLabel`, icons at rest |
| `textSubtle` | `subtle` | `#9C9182` | `#7C7264` | Placeholders, hints, disabled-looking text, decorative separators (never required-to-act copy) |

### 2.3 Borders (`DESIGN.md` §2.3)

| Semantic Token (`IcColors`) | `UiColors` Projection | Light Hex | Dark Hex | Normative Usage (`DESIGN.md`) |
| :--- | :--- | :--- | :--- | :--- |
| `borderHairline` | `hairline` | `#E4DFD5` | `#423B31` | Structure: row separators, panel & card edges, dividers, title bar bottom |
| `borderInput` | `inputBorder` | `#C2B9AB` | `#5E554A` | Interactive edges: inputs, secondary buttons, select boxes, segmented tracks, focused window frame, empty-state outlines |
| `borderStrong` | `borderStrong` | `#17140F` | `#F0ECE4` | Emphasis: switch thumb when off, hovered service tiles |

### 2.4 Accent & Focus Ring (`DESIGN.md` §2.4, §10.9, §20)

| Semantic Token (`IcColors`) | `UiColors` Projection | Light Value | Dark Value | Normative Usage (`DESIGN.md`) |
| :--- | :--- | :--- | :--- | :--- |
| `accent` | `accent` | `#1F5EFF` | `#4B7DFF` | Primary buttons, links, selected nav rows, focus border, edited values, chart main series, `needsYou` icon |
| `accentHover` | `accentHover` | `#1541C7` | `#1F5EFF` | Primary button hover, hovered chart column |
| `accentTint` | `accentTint` | `#E7EEFF` | `#4B7DFF` at 16% (`0x294B7DFF`) | Palette launcher, active icon buttons, drop targets, flash, banners, chip backgrounds, picked rows |
| `onAccent` | `onAccent` | `#FFFFFF` | **`#FFFFFF`** | Text and icons on `accent` |
| `ring` | `focus` | `#1F5EFF` at 35% (`0x591F5EFF`) | `#4B7DFF` at 45% (`0x734B7DFF`) | Focus ring glow around `1.5px` `accent` border (`2px` spread on inputs, `3px` on composer & `SelectionRing`) |
| `accentOnInk` *(§10.9, §20)* | `accentOnInk` *(add)* | `#8FB0FF` | `#8FB0FF` | Action link inside ink toast (`13 / 700`) |

### 2.5 Status Tokens: Icon + Word + Colour (`DESIGN.md` §2.5, §8.1)

| Status | Foreground (Light / Dark) | Background (Light / Dark) | Required Lucide Icon | Canonical Words (`DESIGN.md` §2.5, §16) |
| :--- | :--- | :--- | :--- | :--- |
| **Done / Connected / Saved** | `done`: `#12A150` / `#8FE0AF` | `doneBg`: `#E1F6EA` / `#12A150` at 16% (`0x2912A150`) | `check`, `circleCheck` | `Done`, `Saved`, `Installed`, `Connected` |
| **Working / Warning / Trying** | `working`: `#E8A317` / `#F6D28A` | `workingBg`: `#FCF1DA` / `#E8A317` at 16% (`0x29E8A317`) | `loaderCircle` spinning (`WorkingSpinner`), `WorkingPulse` | `Working`, `Saving`, `Syncing` |
| **Error / Failed / Destructive** | `error`: `#E0402F` / `#F3A99F` | `errorBg`: `#FCE7E3` / `#E0402F` at 16% (`0x29E0402F`) | `triangleAlert`, `x` | `Not saved`, `Failed` |
| **Idle** | `idle`: `#9C9182` / `#7C7264` | *(none)* | *(none)* | `Off`, `Waiting` |
| **Needs You** | `accent` (`#1F5EFF` / `#4B7DFF`) | `accentTint` (`#E7EEFF` / `0x294B7DFF`) | `hand` (raised hand) | `Needs you`, `Needs your response`, `Needs your approval` |

### 2.6 OS Layer, Desktop Tones, Overlay Fills & Chart Colours (`DESIGN.md` §2.6–§2.9, §6.2–§6.3, §14.7)

| Group | Token | Light Value | Dark Value | Normative Rule (`DESIGN.md`) |
| :--- | :--- | :--- | :--- | :--- |
| **OS Chrome** | `chrome` | `#FFFFFF` at 90% (`0xE6FFFFFF`) | `#2A251E` at 90% (`0xE62A251E`) | `ChromePanel` (`blur = 30`: palette, popovers, menus, switcher) & Bar (`blur = 16`, `32px` high) |
| **OS Chrome** | `chromeBorder` | `#E4DFD5` | `#423B31` | `1px` edge of `ChromePanel` and top edge of Bar |
| **Window Title Bar** | `titleBarFocused` | `#F2EFE9` | `#332D25` | `38px` title bar of the focused window |
| **Window Title Bar** | `titleBarBlurred` | `#FBFAF7` | `#2A251E` | `38px` title bar of unfocused windows behind |
| **Scrim (§6.3)** | `scrim` | `surfaceInk` at 28% (`0x4717140F`) | `surfaceInk` at 50% (`0x80110F0B`) | Behind sheets; click or `Esc` closes sheet |
| **Overlay Fills (§2.8, §20)** | `hover` | `#17140F` at 4% (`0x0A17140F`) | `#FBFAF7` at 8% (`0x14FBFAF7`) | Session rows, Files rows, crumbs, popover rows, text buttons *(bar hover `6%`/`10%` folds into `hover` per §20)* |
| **Overlay Fills (§2.8, §20)** | `selected` | `#17140F` at 7% (`0x1217140F`) | `#FBFAF7` at 12% (`0x1FFBFAF7`) | Palette selected row, `IconBtn` hover |
| **Desktop Tones (§2.7)** | `Cream` (default) | `#F2EFE9` | `#1D1914` | **Plain solid fills ONLY — zero gradients!** |
| **Desktop Tones (§2.7)** | `Sand` | `#E6DFD2` | `#231E17` | Plain solid fill (`Settings › Desktop & Dock › Background`) |
| **Desktop Tones (§2.7)** | `Ink` | `#2A251E` | `#0D0B08` | Plain solid fill (desktop hint lifts foreground to read) |
| **Chart Series (§2.6, §14.7)** | Main + `chartContext` | Main: `accent` (`#1F5EFF`, hover `#1541C7`) · Comparison: `#9C9182` | Main: `accent` (`#4B7DFF`, hover `#1F5EFF`) · Comparison: `#9C9182` | **Zero purple, zero multi-hue rainbow!** Max 2 inline series (`accent` + `chartContext` `3:1`); **3+ series go to Sheets** (`Open in Sheets`, §14.7). |

---

## 3. Typography, Spacing, Dimensions, Radii, Elevation & Motion

### 3.1 Typography Scale (`DESIGN.md` §3.1–§3.3)

- **`Inter` (`IcType.text`)**: Weights `400, 500, 600, 700`. Tracking `-0.2` at `20px+`, `0` below `20px`. Everything that is not a big heading.
- **`InterDisplay` (`IcType.heading`)**: Weights `600, 700, 800`. Headings **`17px` and up**, tracking `-0.4`, line-height `1.15`. (`Schibsted Grotesk` is wordmark artwork only.)
- **Exact 19 Allowed Sizes (`DESIGN.md` §3.2)**:
  `40, 30, 28, 26, 24, 22, 20, 18, 17, 16, 15, 14.5, 14, 13.5, 13, 12.5, 12, 11.5, 11`
  *(`26` is for `#` headings in Quick View documents; `14.5` is for memory text and document body only. Any other size—including `10`, `10.5`, or `36`—is a bug.)*
- **Casing & Styling Rules (`DESIGN.md` §3.3)**:
  - **Sentence case everywhere** (titles, buttons, menus, palette headers).
  - **`UPPERCASE` allowed in only two places**: `GroupLabel` (`11.5 / 700`, tracking `+0.6`, `textMuted`) and card object labels (`11 / 700`).
  - No italics; no underlines except links inside running prose; `tnum: true` on clocks, counts, amounts, table dates, versions, token counts, badges, and codes.

| Role (`DESIGN.md` §3.2) | Family & Style | Where Used |
| :--- | :--- | :--- |
| `Display number` | `InterDisplay` `40 / 800`, `tnum` | Billing balance |
| `Page title` | `InterDisplay` `30 / 800` | `SectionHeader` in Settings, Profile name |
| `Hero title` | `InterDisplay` `28 / 800` (`24 / 800` link gone; `26` Quick View `#`) | Ino empty state `"What do you need?"`, History title, share page app name |
| `Secure window title` | `InterDisplay` `22 / 800` | Secure window |
| `Sheet title` | `InterDisplay` `20 / 700` | `SheetFrame` |
| `Popover title` | `InterDisplay` `17 / 700` | Clock popover month, Save-to picker |
| `Input large` | `Inter` `18 / 400` | Palette search field |
| `Record name` | `Inter` `17 / 700`, height `1.3` | `RecordCard` |
| `Card title (approval)` | `Inter` `16 / 600`, height `1.4` | `ApprovalCard`, empty-folder / no-match titles |
| `Card title` | `Inter` `15 / 600` | Every other card title, conversation header, `SettingRow` label, Connections row name |
| `Body` | `Inter` `14 / 400`, height `1.5` (Ino replies `1.6`) | Messages, sheet body, help paragraphs, empty-state lines (`14.5 / 500` memory & doc body) |
| `Row label` | `Inter` `14 / 500` | Settings nav, palette row, History row |
| `Help line` / `Input text` | `Inter` `13.5 / 400` (`textMuted` help; `tnum` in `InputBox`, `500` in `SelectBox` & file names) | `SettingRow` help, `InputBox`, `SelectBox`, table cells (`13.5`) |
| `Row label compact` / `Button` | `Inter` `13 / 500` (`13 / 600` on buttons & active segments; `13 / 400` `textMuted` secondary) | Session rows, menu rows, popover rows, Files sidebar, `Btn`, meta lines |
| `Caption` | `Inter` `12.5 / 400`, `textMuted` | Source lines on tables/charts, chip names (`12.5 / 600`), hints next to group labels |
| `Label small` | `Inter` `12 / 600` (`12 / 500` in tooltips) | Card source line, sidebar/popover section labels, tags, status chips, tooltips |
| `Group label` | `Inter` `11.5 / 700`, `UPPERCASE`, tracking `+0.6`, `textMuted` (`11.5 / 400` `tnum` chart/table axes) | `GroupLabel`, Apps window sections, chart & table axis labels |
| `Micro` | `Inter` `11 / 700` | Bar notification count badge (`tnum`), card object label (`UPPERCASE`) |

### 3.2 Spacing, Fixed Dimensions, Radii, Shadows & Motion (`DESIGN.md` §4–§7)

- **Spacing Scale (`IcSpace`, `DESIGN.md` §4.1, §20 — 16 values)**:
  `2 · 4 · 6 · 8 · 10 · 12 · 14 · 16 · 18 · 20 · 24 · 28 · 32 · 40 · 48 · 56`
- **Key Fixed Heights & Widths (`DESIGN.md` §4.2–§4.3)**:
  - **OS & Window Chrome**: Bottom Bar = **`32px`**, Window title bar = **`38px`**, Window min = **`640 × 420`**, Files toolbar & conversation header = **`52px`**, Quick view header = **`48px`**, Share top bar = **`56px`**, Files status bar = **`28px`**.
  - **Controls & Rows**: `Btn` = **`36px`** (small `30`, share `40`), `InputBox` & `SelectBox` = **`36px`** (History large search `40`), Composer controls = **`38px`**, `Segmented` = `3px` pad + **`30px`** segments, `IconBtn` = **`28px` box / `16px` icon** (`24/14` small; Lucide `2px` default stroke), `IcSwitch` = **`36 × 20`** (`14` thumb), `Tag` = **`22px`**, `FileChip` / `Breadcrumbs` / `Files sidebar row` = **`28px`**, Menu / Session / Files list row = **`32px`**, Settings nav row = **`34px`** (`44` two-line), Table = **`38` header / `44` row / `48` footer** (`18px` side padding), History row = **`52px`**.
  - **Canonical Widths**: Ino sidebar `264` (hides `< 720`), Settings sidebar `248`, Files sidebar `216`, Settings content `max 860` (left-aligned), Profile `max 760` (centred), Conversation `max 760` (Ino reply `max 680`, empty state `max 620`), Card / ErrorLine `max 560` (`RecordCard` `520`), Sheet `520` (`ConfirmSheet` `460`), Palette `screenWidth − 48` clamped `320..720` at `14%` from top.
- **Responsive Breakpoints (`DESIGN.md` §4.4)**:
  - `>= 1024 × 640`: Full multi-window desktop (designed at `1440 × 900`, checked at `1280 × 800` & `1920 × 1080`).
  - `< 1024` wide or `< 640` tall: **Focus mode** — one zoomed window at a time above the `32px` bar, `radius = 0`, no border, no shadow.
  - `< 480` wide: Centred `15 / 500 textBody` message on `surfacePage`: `"Make this window wider to use IntoChat OS."`
- **Radius Scale (`IcRadius`, `DESIGN.md` §5)**:
  - `xs = 6` (tags, tooltips, menu rows, Files rows & crumbs, key caps, palette glyph wells, skeleton bars, small badges)
  - `sm = 8` (session rows, Settings nav rows, `IconBtn`, segments, file chips, chart tooltip, drag chips)
  - `button = 10` (buttons, inputs, select boxes, segmented outer track, palette rows, toast, menus, model pill, `PromptOptions` rows, inner card tables, Favourites tiles)
  - `input = 12` (large `40px` History search field, `CardResult` answered lines)
  - `card = 14` (windows, cards, Settings `Panel`, error lines, popovers, suggestions, Apps/Files grid tiles, banners, live preview)
  - `bubble = 18` (user bubbles & typing indicator, with speaker corner at `6`)
  - `panel = 20` (palette, sheets, composer, docked prompt, window switcher)
  - `pill = 999` (status chips, badges, switch track, progress bars)
  - Special: App tiles = `22%` of size; chart column tops = `4` (`IcRadius.bar`, §20); zoomed/focus windows = `0`.
- **Shadows (`IcShadow`, `DESIGN.md` §6.1)**:
  - `sm`: `0/1/3 @ 7% + 0/1/2 @ 4%` (L) · `0/1/3 @ 35%` (D) — chat cards, active session row, active segment, tooltips, badges. *(Note: Settings `Panel` has **no shadow**, only `1px borderHairline`.)*
  - `md`: `0/4/12 @ 8% + 0/1/3 @ 5%` (L) · `0/4/12 @ 40%` (D) — Quick view document page.
  - `lg`: `0/12/32 @ 10% + 0/4/10 @ 5%` (L) · `0/12/32 @ 45%` (D) — unfocused background windows, composer, docked prompt, toast, lifted drag rows.
  - `xl`: `0/24/60 @ 14% + 0/8/20 @ 6%` (L) · `0/24/60 @ 55%` (D) — focused window, `ChromePanel` (palette, popovers, menus), sheets.
- **Motion (`IcMotion`, `DESIGN.md` §7, §20)**:
  - `fast = 120ms`, `base = 200ms` *(note: `design/tokens.dart:553` currently has `220ms`—align to `200ms` in `UK-5`)*, `slow = 340ms`, `ease = Cubic(0.2, 0.8, 0.2, 1)`.
  - Loops & holds: spinner `900ms`, bar working pulse `1400ms`, skeleton `1100ms`, typing dots `1000ms`, row jump flash `120ms in / 1400ms hold / 900ms out` (`2400ms` install), hover hint `350ms` (`380ms` file chip, `0ms` bar tooltip).
  - Reduced motion (`MediaQuery.disableAnimationsOf`): all loops (spinners, pulses, skeletons, typing dots) stop on their still frame.

---

## 4. Keyboard-First & Docked Prompt Specification (`DESIGN.md` §1.11, §10.4, §14.8; `SPEC.md` §7)

1. **Global OS Shortcuts**:
   - `Ctrl/Cmd + K` (or `/` on empty desktop): Open **Palette** (`screenWidth − 48` clamped `320..720`, `14%` from top, `IcRadius.panel = 20`, `⌘↵` asks Ino, `⌘.` opens `260px` Actions list).
   - `Alt/Opt + \`` (`Ctrl + Tab`): Open **Window Switcher** (`IcRadius.panel = 20`); `Alt/Opt + S`: Focus Ino sidebar with `SelectionRing`.
   - `Esc`: Close topmost menu/popover/sheet/palette, stop running job (`Esc to stop`), or trigger `"Chat about this"` on a docked prompt.
2. **Docked Prompt Mechanics (`PromptScope` + `PromptKeys` + `Stop` + `PromptOptions`)**:
   - Any card waiting for an answer (`ApprovalCard`, `ChoiceCard`, `Credential request`, `Change preview`, `Remember this?`, `Save to`) replaces the composer (`max 760px`, `IcRadius.panel = 20`, `1px borderInput`, `IcShadow.lg`, max `2/3` session height) while leaving a compact `"Answer below ↓"` anchor (`accentTint`, `IcRadius.input = 12`, `hand` `15px`) in the stream.
   - `↑` / `↓` (`Tab` / `Shift+Tab`) move `SelectionRing` (`1.5px accent` + `3px ring`) across stops; `←` / `→` adjust values or scrub chart points; `1..9` pick numbered `PromptOptions` (`min 42px` high, `22px` number well, `IcRadius.button = 10`); `↵` / `Space` activates the selected stop; `⌘↵` invokes the first option; `Esc` invokes **`"Chat about this"`** (always the last option, restoring the composer with an `"↑ to answer"` bar above it).

---

## 5. `os/ui` Drift Audit (Current Implementation vs Canonical Spec)

| # | Severity | File & Line(s) in `os/ui` | Observed Drift | Required Fix (Target Ticket) |
| :--- | :--- | :--- | :--- | :--- |
| **D-01** | **P0 Bug (Contrast)** | `ui/lib/src/theme/ui_colors.dart:287-288` | `UiColors.intochatDark` sets `onAccent: Color(0xFF17140F)` instead of `#FFFFFF`, rendering dark charcoal text on `#4B7DFF` primary buttons in dark mode (`3.9:1` instead of crisp white). Also `accentCopy` is `#FBFAF7` instead of `#4B7DFF`. | Set `intochatDark.onAccent = Color(0xFFFFFFFF)` and `accentCopy = Color(0xFF4B7DFF)` (`UK-5`). |
| **D-02** | **P1 Token Split & Status Drift** | `ui/lib/src/theme/ui_colors.dart:292,294,296` vs `ui/lib/src/design/tokens.dart:570-574` | `UiColors.intochatDark` status backgrounds use `0x298FE0AF` / `0x29F6D28A` / `0x29F3A99F` instead of `DESIGN.md` §2.5 (`0x2912A150` / `0x29E8A317` / `0x29E0402F`), and `IcMotion.base` in `tokens.dart:553` is `220ms` vs `DESIGN.md` §7 `200ms`. | Unify `UiColors` onto `IcColors` (`tokens.dart`) as a single canonical source of truth and align `IcMotion.base = 200ms` (`UK-5`). |
| **D-03** | **P1 Identity & Blur Drift** | `ui/lib/workspace/workspace_app.dart:586-605`, `ui/lib/src/design/tokens.dart:70-124`, `ui/lib/src/nodes/surface.dart:23-28` | `_effectiveTheme` overrides `'intochat'` with `'fluent-acrylic'` (`workspace_app.dart:601-604`) alongside 7 competing style presets (`windowOpacity: 0.82`, `blurSigma: 24`), and `SurfaceNode('acrylic')` blurs inside app windows—violating `DESIGN.md` §1.3 & §6.2. | Make **Warm Ink & Paper** (`IcColors.light` / `IcColors.dark`) the sole canonical product theme; enforce `blurSigma = 0` on all window interiors (`UK-5`). |
| **D-04** | **P1 Legacy Palette & Chart Colour Leaks** | `ui/lib/src/theme/ui_theme.dart:243-288, 555` & `ui/lib/src/components/chart/ui_chart.dart:118-193` | `UiChatTheme.light()` hardcodes `UiColors.paper` (`#397B63` green!), `UiChatTheme.dark()` / `UiTheme.baseDark()` hardcode `UiColors.midnight` (`#141720` / `#7B9BE3`), and `ui_chart.dart` uses `UiColors.paper` instead of `accent` + `chartContext` (`#9C9182`). | Purge `UiColors.paper` / `UiColors.midnight`; enforce `DESIGN.md` §2.6 / §14.7 chart colours (`accent` main + `chartContext` `#9C9182` comparison, 3+ series -> Sheets) (`UK-5`, `UK-14`). |
| **D-05** | **P1 Monospace Violation** | `ui/lib/src/theme/ui_theme.dart:172-177, 198-219`, `ui/lib/src/design/tokens.dart:642-653`, `ui/lib/src/controls/ic_breadcrumb_bar.dart:160`, `DigitalBrain.UI/ViewModel.cs:233`, `DataStudioViewRenderer.cs:33` | `monoFallback` (`Cascadia Mono`, `Consolas`) is applied to `_meta`, `_metaStrong`, and `_menuLabel`; `IcType.code` (`JetBrains Mono`) is used in `IcBreadcrumbBar` `neuron://` pills; `DataStudioViewRenderer` sets `Text.Mono = true` on SQL. Violates `DESIGN.md` §1.10 / §3.1 / §18. | Remove `monoFallback` & `IcType.code` completely; use `Inter` + `tnum` everywhere and deprecate `Text.Mono` (`UK-5`, `UK-24`). |
| **D-06** | **P2 Control Geometry & Focus Drift** | `ui/lib/src/components/kit/ui_kit_field.dart:101,164`, `ui/lib/src/components/kit/ui_kit_select.dart:238,248`, `ui/lib/src/theme/ui_tokens.dart:109`, `ui/lib/src/theme/ui_theme.dart:445,519` | `UiTokens.fieldRadius` is `12` (`DESIGN.md` §5 / §11.3 specifies `IcRadius.button = 10` for `36px` `InputBox`/`SelectBox`, reserving `input = 12` for `40px` History search & `CardResult`); `UiKitSelect` is `48px` tall instead of `36px`; `UiKitTabs` uses `12px` instead of `13px`; `tooltipTheme` uses radius `8` instead of `xs = 6` (`350ms` delay) and lacks dark `surfaceCard + borderHairline`; `UiKitField` focus border stays `1px` instead of `1.5px accent` + `ring`. | Align `UiKitField`, `UiKitSelect`, `UiKitTabs`, and `Tooltip` geometry & focus rings to `DESIGN.md` §5, §9, §10.8, §11 (`UK-5`, `UK-10`, `UK-12`). |
| **D-07** | **P2 Open `DESIGN.md` §20 Token Backlog** | `ui/lib/src/design/tokens.dart` & `ui/lib/src/theme/ui_tokens.dart` | Missing `hover` (`4%/8%`), `selected` (`7%/12%`), `onInk` (`#FBFAF7`), `accentOnInk` (`#8FB0FF`), `bubbleMine`/`onBubbleMine`, `IcColors.none`, `IcSpace` (`2..56`), `IcBlur` (`chrome=30, bar=16`), `IcRadius.bar = 4`, loop duration constants (`900/1400/1100/1000/350ms`), and `reducedMotion` guards on spinners/pulses. | Land complete `DESIGN.md` §20 token set in `UK-5`. |
| **D-08** | **P2 Raw Material Widgets in Data Table** | `ui/lib/src/components/table/ui_data_table.dart:92-197, 342-363, 463-645` | `UiDataTable` uses stock Material `AlertDialog`, `DropdownButtonFormField`, `CheckboxListTile`, `InputChip`, `FilledButton`, `TextButton`, and `Icons.*` instead of Lucide `2px` icons and Warm Ink & Paper sheets/menus/inputs (`38/44/48px` table rhythm, §12.5). | Rebuild `Table` on canonical Kit primitives in `UK-14`. |

---

## 6. Acceptance Gate for `UK-5` (Tokens & Theme Alignment)

Before Phase 2 component migration closes `UK-5`, `flutter test` + architecture lints must verify:
1. Single token source (`IcColors` + `IcType` + `IcSpace` + `IcRadius` + `IcShadow` + `IcBlur` + `IcMotion`) matching `DESIGN.md` §2–§7 1:1, with `UiColors` as a lossless compatibility projection until legacy callers are retired.
2. `intochatDark.onAccent == #FFFFFF`, dark status backgrounds equal `0x2912A150` / `0x29E8A317` / `0x29E0402F`, desktop tones are plain fills (`Cream`, `Sand`, `Ink`), and chart tokens expose only `accent` + `chartContext` (`#9C9182`).
3. Zero `monoFallback` or `JetBrains Mono` in `os/ui`, zero font sizes outside the 19 allowed sizes in `DESIGN.md` §3.2, zero `UiColors.paper` / `UiColors.midnight` references, and `blurSigma == 0` on all window interiors.
4. Automated conformance test (`DESIGN.md` §19.5) rejecting raw `Color(0x...)`, `BorderRadius.circular(...)`, and `Duration(milliseconds: ...)` literals outside `tokens.dart`.
