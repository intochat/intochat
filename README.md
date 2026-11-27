<p align="center">
  <img src="docs/assets/ic-mark.svg" alt="IntoChat OS" width="80" height="80" />
</p>
<h1 align="center">IntoChat OS</h1>
<p align="center">
  <strong>Your personal AI operating system.</strong><br />
  One sovereign brain with your memory, your apps, your connectors, and an agent called Ino that builds tools for you.
</p>
<p align="center">
  <a href="https://github.com/intochat/intochat/actions/workflows/ci.yml"><img src="https://github.com/intochat/intochat/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/intochat/intochat" alt="License" /></a>
  <a href="https://github.com/intochat/intochat/releases/latest"><img src="https://img.shields.io/github/v/release/intochat/intochat" alt="Release" /></a>
  <img src="https://img.shields.io/badge/.NET-11_Preview-512BD4" alt=".NET 11" />
  <img src="https://img.shields.io/badge/Flutter-3.x-02569B" alt="Flutter" />
</p>

<p align="center">
  <a href="https://os.test.intochat.io"><strong>🚀 Try Live in Browser (No Sign-Up)</strong></a> <em>(WebAssembly / Web sandbox)</em> &nbsp;·&nbsp;
  <a href="https://os.test.intochat.io/download"><strong>⬇️ Download Hub</strong></a> &nbsp;·&nbsp;
  <a href="docs/self-hosting.md"><strong>🐳 Self-Host &amp; Aspire</strong></a>
</p>

---

## Try in 1 Click, Own Forever

Launch instantly at **[os.test.intochat.io](https://os.test.intochat.io)** or install the native shell on your device from **[os.test.intochat.io/download](https://os.test.intochat.io/download)**:

| Platform | Direct Download &amp; One-Line Install |
| :--- | :--- |
| 🪟 **Windows (x64)** | `irm https://github.com/intochat/intochat/releases/latest/download/install.ps1 \| iex` *(Recommended — zero SmartScreen, Start Menu shortcut & CLI)*<br>[Portable (.zip)](https://github.com/intochat/intochat/releases/latest/download/intochat-win-x64.zip) · [Installer (.exe)](https://github.com/intochat/intochat/releases/latest/download/IntoChat-Setup-win-x64.exe) · [MSIX (sideload / Store-ready)](https://github.com/intochat/intochat/releases/latest/download/IntoChat-win-x64.msix) · [AppInstaller](https://github.com/intochat/intochat/releases/latest/download/IntoChat.OS.appinstaller) *(dev-signed MSIX — trust the dev cert [IntoChat-DevCert.cer](https://github.com/intochat/intochat/releases/latest/download/IntoChat-DevCert.cer) once via `irm https://github.com/intochat/intochat/releases/latest/download/trust-dev-cert.ps1 \| iex`, or install via Microsoft Store)* |
| 🍎 **macOS (Apple Silicon)** | [Disk Image (.dmg)](https://github.com/intochat/intochat/releases/latest/download/IntoChat-macos-arm64.dmg) · [.tar.gz](https://github.com/intochat/intochat/releases/latest/download/IntoChat-macos-arm64.tar.gz)<br>`curl -fsSL https://github.com/intochat/intochat/releases/latest/download/install.sh \| sh` |
| 🐧 **Linux (x64)** | [AppImage](https://github.com/intochat/intochat/releases/latest/download/IntoChat-linux-x86_64.AppImage) · [.deb](https://github.com/intochat/intochat/releases/latest/download/IntoChat-linux-amd64.deb) · [.tar.gz](https://github.com/intochat/intochat/releases/latest/download/IntoChat-linux-x86_64.tar.gz) |
| 🤖 **Android** | [APK (.apk)](https://github.com/intochat/intochat/releases/latest/download/intochat-android.apk) |
| 📱 **iOS** | [Unsigned IPA (.ipa)](https://github.com/intochat/intochat/releases/latest/download/intochat-ios-unsigned.ipa) · *Or install as PWA from Safari via Share → Add to Home Screen* |

> Every shell speaks one protocol. On Android and iOS, the shell connects to your local server or cloud brain. Looking to run the container (`ghcr.io/intochat/os`) or compose a `.NET Aspire` AppHost? See [`docs/self-hosting.md`](docs/self-hosting.md).

---

## Why IntoChat OS?

### 1. One Sovereign Brain, Three Ways to Run It
Run an ephemeral brain in **Browser RAM** for an instant trial, sign into a **Cloud Brain** (`into.chat`), or run **100% locally on your machine** (`~/.intochat`, cold start `<1.5s`, TPM/DPAPI sovereign key custody, local [Ollama](https://ollama.com) or cloud models). The shell never changes — only your brain's address does.

### 2. An OS That Writes Its Own Apps
Ask **Ino** for a tool, dashboard, or workflow → Roslyn compiles it, AST-sanitizes it, sandbox-verifies it against live scenarios, and pins an interactive reactive app directly onto your workspace. Credentials stay sealed inside connections; apps never see raw tokens.

### 3. Built on DigitalBrain (`framework/`)
Powered by the open-source [`DigitalBrain`](framework/README.md) framework: event-sourced **Neurons**, **Signals**, **Synapses**, and strict DDD boundary contracts ([core vocabulary](framework/README.md#core-vocabulary)). Modular by construction with zero monolithic lock-in.

---

## Build & Contribute in 60 Seconds

Prerequisites: the .NET SDK pinned in [`global.json`](global.json) (.NET 11 Preview), Docker (Linux containers), and [Ollama](https://ollama.com) on the host for the local profile.

```bash
dotnet run intochat.cs preflight   # verify machine, Docker, Ollama
dotnet run intochat.cs local       # lean local profile
dotnet run intochat.cs             # full stack with Flutter shell
```

Run from the repository root. All profiles and flags are documented at the top of [`intochat.cs`](intochat.cs).

### `1 Folder = 1 Module` Architecture

Adding a new connector or capability requires **zero external configuration edits**:

```bash
dotnet new digitalbrain-module
```

Drop your module folder under `src/Modules/`, implement a `Module` class with `[Module]` and `Manifest`, and compile-time source generation (`[GeneratedModuleRegistry]` / `BuiltInModuleTypes()`, plus `RegisteredModuleAttribute` for external roots) registers it into the OS catalog. Every module's README stays synchronized with its bound options and secrets via `ModuleDocumentationFacts` (`UPDATE_DOCS=1`). Read the full guide in [`docs/modules.md`](docs/modules.md).

### Repository Map

| Layer | Path | What Lives Here |
| :--- | :--- | :--- |
| **Kernel** | [`src/IntoChat.OS.Kernel`](src/IntoChat.OS.Kernel), [`src/IntoChat.OS.Sandbox`](src/IntoChat.OS.Sandbox) | Host process that runs a brain on a machine + isolated Roslyn/sandbox runtime |
| **System Apps** | [`src/IntoChat.OS.Apps`](src/IntoChat.OS.Apps) | Ino, Creator, Desktop, Settings, Tasks, and built-in DigitalBrain apps |
| **Modules** | [`src/Modules`](src/Modules) | 26 modules: AI, GitHub, Google, Postgres, Storage, Playwright, MCP, and more |
| **Shell** | [`ui/`](ui) *(Flutter)*, [`src/IntoChat.OS.Desktop`](src/IntoChat.OS.Desktop) *(launcher)* | Cross-platform shell for Desktop, Web, and Mobile |
| **DigitalBrain** | [`framework/`](framework/README.md) | Core runtime, primitives (Neurons, Signals, Synapses), and templates |

---

## Documentation & Community

- **[Contributing Guide](CONTRIBUTING.md)** — PR workflow, architecture ratchets, and build/test commands
- **[Module Authoring Guide](docs/modules.md)** — `1 Folder = 1 Module` model, contracts, options, and secrets
- **[Self-Hosting & .NET Aspire](docs/self-hosting.md)** — Container hosting (`ghcr.io/intochat/os`), Aspire AppHost integration, and packages/distribution matrix
- **[Self-Improving Loop Architecture](docs/self-improving-loop-architecture.html)** — Visual architecture of the live authoring & verification loop
- **[Full Product Docs](docs/index.md)** · **[Framework Docs](framework/docs/README.md)** · **[Changelog](CHANGELOG.md)** · **[Security](SECURITY.md)** · **[License](LICENSE)**
