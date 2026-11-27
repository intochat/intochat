# Native AOT Architecture, Readiness & Deployment Guide

This page describes how `IntoChat.OS.Kernel`, standalone Orleans clients, and the Sandbox/DevKit compilation boundary build as **Full Native AOT (`<PublishAot>true</PublishAot>`, switched on by `IntoChatOSAot=true`)**, and how that fits with **Aspire**, the **Desktop / MSIX Installer**, and **Azure Test Stands**.

---

## 1. Problems Solved & Root-Cause Fixes

Bringing an Orleans + DigitalBrain + ASP.NET Core Minimal API + MCP server runtime to Native AOT required solving five distinct categories of runtime code generation and metadata trimming issues:

### A. Build Speed & Roslyn Separation (14 min -> 3.5 min AOT Publish)
- **Problem**: Embedding `Microsoft.CodeAnalysis.CSharp` and `IntoChat.OS.Modules.Roslyn` directly inside `IntoChat.OS.Kernel` forced the Native AOT compiler (`ilc.exe`) to compile ~60 MB of Roslyn syntax trees, semantic analyzers, and MSBuild workspace loaders into native machine code (~14 minutes per publish). Furthermore, runtime C# compilation (`Assembly.Load(byte[])`) fundamentally belongs in the JIT-enabled Sandbox/DevKit worker, not inside the Native AOT Kernel.
- **Solution**:
  - Excluded `IntoChat.OS.Modules.Roslyn` from [IntoChat.OS.Kernel.csproj](../src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj) and [OperatingSystemModules.cs](../src/IntoChat.OS.Kernel/OperatingSystemModules.cs) when `IntoChatOSAot=true` (`INTOCHAT_OS_AOT`).
  - Guarded `CSharpCompilation` and `AnalyzerSet` in [SynapseCompiler.cs](../framework/src/DigitalBrain.Simulation/RuntimeSynapses/Compile/SynapseCompiler.cs) and [ReferenceSet.cs](../framework/src/DigitalBrain.Simulation/RuntimeSynapses/Compile/ReferenceSet.cs) under `#if !INTOCHAT_OS_AOT`.
  - Configured `<IlcOptimizationPreference>Speed</IlcOptimizationPreference>` and `<IlcFoldIdenticalMethodBodies>false</IlcFoldIdenticalMethodBodies>` in `IntoChat.OS.Kernel.csproj`.
  - Moved `IntoChat.OS.Modules.Roslyn` and `DigitalBrain.Simulation` (`RuntimeSynapses`) into [IntoChat.OS.Sandbox.csproj](../src/IntoChat.OS.Sandbox/IntoChat.OS.Sandbox.csproj) and exposed C# package compilation over both HTTP (`POST /apps/compile` in [Program.cs](../src/IntoChat.OS.Sandbox/Program.cs)) and the out-of-process `--unit-host` stdio pipe (`op: "compile"` in [UnitHostWorker.cs](../src/IntoChat.OS.Sandbox/UnitHostWorker.cs) and [AppSandboxHost.cs](../src/IntoChat.OS.Sandbox/AppSandboxHost.cs)).

### B. Orleans DI Open-Generic Value-Type & Surrogate Instantiation Failures
- **Problem**: `Microsoft.Extensions.DependencyInjection`'s `CallSiteFactory.VerifyOpenGenericAotCompatibility` throws `InvalidOperationException` under Native AOT (`RuntimeFeature.IsDynamicCodeSupported == false`) whenever an open generic service is resolved with a **value-type (`struct` or `enum`)** generic argument:
  1. `PooledResponseCodec<,>`, `PooledResponseCopier<>`, and `PooledResponseActivator<>` in `Orleans.Serialization` when grain methods return value types (`bool`, `int`, `long`, `Guid`, `DateTimeOffset`, `BrainKind`, `NeuronId`, etc.).
  2. `SurrogateCodec<TField, TSurrogate, TConverter>` and `SurrogateCopier<TField, TSurrogate, TConverter>` because Orleans `[GenerateSerializer]` generates `struct` surrogates (`TSurrogate` is a value type!).
  3. `Enum32Codec<TEnum>` and `ZeroCopyCopier<TEnum>` for enums serialized in grain state or RPC calls.
- **Solution**:
  - Created [OrleansAotResponses.cs](../src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs) with single-type-parameter closed wrappers (`ClosedResponseCopier<TResult>`, `ClosedResponseCodec<TResult>`, `ClosedResponseActivator<TResult>`, `ClosedSurrogateCopier<TField, TSurrogate, TConverter>`) and pre-registered all closed `IValueSerializer<TSurrogate>`, `IFieldCodec<TField>`, `IDeepCopier<TField>`, enums, and nullable value types in DI.
  - Wired `OrleansAotResponses.Register(...)` into the Orleans silo ([ServerHostingExtensions.cs](../src/IntoChat.OS.Kernel/AspireServer/ServerHostingExtensions.cs)). The product ships no out-of-process Orleans client.

### C. Eliminating `DispatchProxy`, `MakeGenericMethod`, `Expression.Compile`, and `GetInterfaceMap` in `DigitalBrain`
- **Problem**:
  1. `NeuronProxy` previously relied on `DispatchProxy.Create` and `InvokeValueMethod.MakeGenericMethod(returnType)` at runtime, which throws `NotSupportedException: 'NeuronProxy.InvokeValue[T](...)' is missing native code` under Native AOT.
  2. `CallPolicy.Declared`, `CallCatalog`, and `AskCall` called `Type.GetInterfaceMap(interfaceType)`, which throws `InvalidCastException` on Native AOT when an interface has default interface methods (`IAsyncDisposable`, static virtual members, etc.).
  3. `CallCatalog.CompileInvoker` and `TaskBoxing.ResultAsync` used `Expression.Lambda(...).Compile()` and `MakeGenericMethod`.
- **Solution**:
  - Updated the Roslyn source generator ([AotPreservationGenerator.cs](../framework/src/DigitalBrain.Analyzers/Neurons/AotPreservationGenerator.cs)) and [NeuronProxy.cs](../framework/src/DigitalBrain/Invocation/NeuronProxy.cs) so every `[Neuron]` contract gets a compile-time `AotNeuronProxy_*` class that calls closed generic methods (`base.CallTask<TResult>`, `base.CallValueTask<TResult>`, `base.CallStream<TItem>`, `base.CallNone`, `base.CallOneWay`) directly without `DispatchProxy` or `MakeGenericMethod`.
  - Replaced all `GetInterfaceMap` usages in [CallPolicy.cs](../framework/src/DigitalBrain/Kernel/CallPolicy.cs), [CallCatalog.cs](../framework/src/DigitalBrain/Kernel/CallCatalog.cs), and [AskCall.cs](../framework/src/DigitalBrain/Synapses/Asks/AskCall.cs) with AOT-safe method matching (`CallPolicy.FindImplementationMethod` and `CallPolicy.IsDisposeMethod`).
  - Added `!RuntimeFeature.IsDynamicCodeSupported` AOT-safe reflection invokers in `CallCatalog` and [TaskBoxing.cs](../framework/src/DigitalBrain/Invocation/TaskBoxing.cs).

### D. AOT-Safe `System.Text.Json` Serialization & Polymorphic UI Trees
- **Problem**: Under Native AOT, `System.Text.Json` disables reflection-based `JsonConverter<T>` creation and throws `NotSupportedException` when `JsonPropertyInfo<TValueType>` is instantiated via reflection. Additionally, `DigitalBrain.UI.View` uses `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]` on `DigitalBrain.UI.Node` with 32 derived UI node records.
- **Solution**:
  - Implemented direct `Utf8JsonWriter` / `Utf8JsonReader` AOT serialization and deserialization in [RuntimeJsonContext.cs](../framework/src/DigitalBrain/Invocation/RuntimeJsonContext.cs) (`RuntimeTypeInfoResolver.WriteAotValue` / `ReadAotValue` and `AotObjectJsonConverter`) with full `[JsonPolymorphic]` and `[JsonDerivedType]` discriminator support.
  - Registered `View`, `PublishedView`, and HTTP/MCP request/response DTOs in [KernelMcpJsonContext.cs](../src/IntoChat.OS.Kernel/Mcp/KernelMcpJsonContext.cs) and registered `KernelMcpJsonContext.Default` with both `RuntimeProtocol` and ASP.NET Core `HttpJsonOptions`.
  - Replaced unconfigured `JsonSerializer.SerializeToElement` calls in UI/Command binding paths with `PooledJson.Encode` (`RuntimeProtocol.Json`).

### E. ASP.NET Core `RequestDelegateFactory` Trimming Fixes
- **Problem**: ASP.NET Core Minimal APIs' `RequestDelegateFactory` inspects `Task.GetAwaiter()`, `TaskAwaiter.GetResult()`, and `StringValues.op_Implicit(StringValues -> string)` via reflection when building endpoint delegates on the first HTTP request. Trimming those members caused `RequestDelegateFactory` to misclassify `Task`-returning endpoints as JSON bodies and fail on query-string parameter coercion.
- **Solution**:
  - Preserved `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, `TaskAwaiter`, `ValueTaskAwaiter`, and `Microsoft.Extensions.Primitives.StringValues` in [OrleansAotResponses.cs](../src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs) and [TrimmerRoots.xml](../src/IntoChat.OS.Kernel/TrimmerRoots.xml).
  - Updated `RuntimeTypeInfoResolver.CreateAotFallbackTypeInfo` to return `null` for `typeof(Task)` so `DefaultJsonTypeInfoResolver` handles endpoint metadata probing cleanly.

### F. Foundry Local 2.5.0 Under Native AOT (In-Process Decision)
- **Verdict**: **In-process** (`FoundryLocalRuntime` and `FoundryLocalHost` in `src/Modules/AI/Devices/Runtimes/FoundryLocal` call `Microsoft.AI.Foundry.Local` 2.5.0 directly). No `TrimmerRootAssembly` workaround, no out-of-process CLI fallback, and no AOT exception in `OWED.md` are required.
- **NativeAOT Build & Publish Evidence (`win-x64`)**:
  - Standalone NativeAOT publish (`PublishAot=true`, `InvariantGlobalization=true`, `TrimmerSingleWarn=false`) rooting catalog listing, model download, load/unload, web service start/stop, execution provider discovery/registration, chat completion, and `AudioSession`: **0 IL2xxx/IL3xxx warnings** across all assemblies (`Microsoft.AI.Foundry.Local` 2.5.0 uses source-generated JSON serialization contexts).
  - Library AOT gate (`IsAotCompatible=true` on `IntoChat.OS.Contracts` and `IntoChat.OS.Modules.AI` Devices files): **0 IL2xxx/IL3xxx warnings**.
  - `Microsoft.AI.Foundry.Local.Runtime` 2.5.0 automatically supplies the native `win-x64` libraries (`foundry_local.dll`, `onnxruntime.dll`, `onnxruntime-genai.dll`, `Microsoft.Windows.AI.MachineLearning.dll`, `foundry-local-host.exe`).
- **NativeAOT Runtime Verification (`IsDynamicCodeSupported == false`)**:
  - Verified end-to-end under NativeAOT: `CreateAsync`, `DiscoverEps`, `GetCatalogAsync`, `ListModelsAsync`, `GetModelAsync`, `DownloadAsync`, `LoadAsync`, `StartWebServiceAsync`, `POST /v1/chat/completions`, `UnloadAsync`, and `StopWebServiceAsync` all succeeded cleanly.
- **Download Cancel & Resume**:
  - **Genuine chunk-level resume**: cancelling `DownloadAsync` via `CancellationToken` throws `OperationCanceledException` while keeping verified file markers and the partial model payload on disk; re-invoking `DownloadAsync` resumes from the last completed chunk instead of restarting from 0%.
- **Cache Directory & 2.5.0 API Notes**:
  - Cache directory configuration member: `Configuration.ModelCacheDir` (defaults to `{AppDataDir}/cache/models`; `DevicesOptions.FoundryLocal.CacheDirectory` maps onto it).
  - `DownloadAsync` accepts `Action<float>?` reporting progress on a **0.0–100.0** percent scale (not `IProgress<float>` and not `0..1`).
  - `StartWebServiceAsync` binds on `Configuration.Web` (`Urls = "http://127.0.0.1:0"`) and exposes bound URLs through `FoundryLocalManager.Instance.Urls`.

### G. sherpa-onnx (Kokoro TTS + Silero VAD)
- **Verdict (branch a — in-process)**: `SherpaOnnxRuntime` in `src/Modules/AI/Devices/Runtimes/SherpaOnnx` calls `org.k2fsa.sherpa.onnx` `2.0.1` directly in-process (Apache-2.0; transitively pulls `org.k2fsa.sherpa.onnx.runtime.win-x64` `2.0.1` with `sherpa-onnx-c-api.dll` + `onnxruntime.dll`). No KokoroSharp fallback and no `OWED.md` AOT exception are required.
- **NativeAOT Build & Publish Evidence (`win-x64`)**:
  - Standalone NativeAOT publish (`net11.0`, `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>true</InvariantGlobalization>`, `<TrimmerSingleWarn>false</TrimmerSingleWarn>`) rooting `OfflineTts` (`Generate` and `GenerateWithCallback`) and `VoiceActivityDetector`: **0 IL2xxx/IL3xxx warnings** (0 warnings total; native artifacts: `SherpaAot.exe` 1.8 MB, `sherpa-onnx-c-api.dll` 4.3 MB, `onnxruntime.dll` 17.8 MB).
- **NativeAOT Runtime Verification (`IsDynamicCodeSupported == false`, no console window)**:
  - `OfflineTts` initialized in ~984 ms (`SampleRate=24000`, `NumSpeakers=54`, `Lang="en-us"`), synthesized `"Hello from IntoChat."` in ~265 ms (26,456 samples, 1.102 s, valid 24 kHz WAV), and executed `GenerateWithCallback` cleanly under NativeAOT for barge-in cancellation.
  - `VoiceActivityDetector` (`silero_vad_v5.onnx`, 16 kHz, 512-sample windows) initialized in ~46 ms and detected the speech segment (`Start=1504` / 0.094 s, `Samples=15648` / 0.978 s).
- **Verified Model Assets (`k2-fsa/sherpa-onnx` GitHub releases)**:
  - Kokoro multi-lang v1.0 (`kokoro-multi-lang-v1_0.tar.bz2`): `https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2`, `349,886,476` bytes compressed (`401,239,297` bytes extracted via Windows `tar -xjf`), SHA-256 `60703707bc05ccd48d60e1eb723f38b07d6e474795a0b6a588d7220400e0f7a4`.
  - Silero VAD v5 (`silero_vad_v5.onnx`): `https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad_v5.onnx`, `2,313,101` bytes, SHA-256 `6b99cbfd39246b6706f98ec13c7c50c6b299181f2474fa05cbc8046acc274396`.
  - Combined download bytes (for `models.json` `kokoro-82m`): `352,199,577` bytes.
- **Kokoro Output Rate & Voices**:
  - Output sample rate: **24,000 Hz** (`OfflineTts.SampleRate` and `OfflineTtsGeneratedAudio.SampleRate`).
  - 54 voices (`0..53` embedded in `model.onnx` `id2speaker`): default English voice `speakerId = 0` is `af_alloy` (US English female; `0..19` US English `af_*`/`am_*` including `3 = af_heart`, `20..27` UK English `bf_*`/`bm_*`).
- **Real `org.k2fsa.sherpa.onnx` 2.0.1 C# API Notes**:
  - `OfflineTtsConfig`, `OfflineTtsModelConfig`, `OfflineTtsKokoroModelConfig`, `VadModelConfig`, and `SileroVadModelConfig` are **structs with public fields** (instantiate via `new OfflineTtsConfig()` / `new VadModelConfig()` for defaults, then assign nested fields directly).
  - `OfflineTts.Generate(string text, float speed, int speakerId)` names its third parameter `speakerId` (not `sid`). `GenerateWithCallback` accepts `OfflineTtsCallback(IntPtr samples, int n)` returning `1` to continue or `0` to cancel.
  - `OfflineTtsGeneratedAudio` exposes `Dispose()` **without implementing `IDisposable`** (dispose in `try`/`finally`, not `using`). `OfflineTts` and `VoiceActivityDetector` implement `IDisposable`.
  - `VoiceActivityDetector(VadModelConfig config, float bufferSizeInSeconds)` takes two constructor arguments (e.g. `60f`); feed `AcceptWaveform(float[])` and drain via `IsSpeechDetected()` / `IsEmpty()` / `Front()` (`SpeechSegment { long Start; float[] Samples; }`) / `Pop()` / `Flush()`.
  - `--kokoro-data-dir` (`Kokoro.DataDir` / `espeak-ng-data`) is deprecated and ignored in sherpa-onnx >= 2.0.0; omit `DataDir`/`DictDir` and pass `Kokoro.Lexicon = Path.Combine(dir, "lexicon-us-en.txt")` (178,401 entries). Tokens absent from the lexicon (e.g. camelCase `"IntoChat"`) are skipped as OOV, so callers should split camelCase or supply a small supplementary custom lexicon file (comma-separated in `Kokoro.Lexicon`).

---

## 2. Readiness Answers

### Q1: Can I run Native AOT with Aspire right now?
**Yes!**
- **How Aspire runs the Kernel**:
  - By default during inner-loop development (`dotnet run intochat.cs` or running `IntoChat.OS.AppHost`), Aspire launches `IntoChat.OS.Kernel.csproj` via `AddDotnetProject`, which runs the standard JIT build for fast 2-second incremental rebuilds, **unless** a live build is active (`live.json`) or you publish the AOT binary.
  - All AOT architectural changes (`AotNeuronProxy_*`, `OrleansAotResponses`, `KernelMcpJsonContext`, `RuntimeJsonContext`) are active and 100% compatible with both JIT (Aspire dev loop) and Native AOT (`IntoChatOSAot=true`).
  - If you want Aspire to orchestrate the compiled Native AOT binary directly, or if you run the Native AOT `IntoChat.OS.Kernel.exe` alongside Aspire's resources (Azurite, Postgres, ClickHouse, Foundry Local / Ollama, Sandbox container), it uses the exact same `IntoChatOS:Topology` (`local`, `local+cloud`, or `cloud`) and environment variables that Aspire injects.

### Q2: Is there already an Installer / Desktop Launcher for Native AOT?
**Yes — both the Desktop Launcher and the MSIX Packager are already built in and now configured for Native AOT!**
1. **Desktop Launcher (`IntoChat.OS.Desktop`)**:
   - Located at [IntoChat.OS.Desktop.csproj](../src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.csproj) and [IntoChat.OS.Desktop.cs](../src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.cs).
   - It is **already a Native AOT executable (`<PublishAot>true</PublishAot>`)** that supervises and launches `os/IntoChat.OS.Kernel.exe` and the Flutter desktop shell (`shell/intochat_os_flutter.exe`) **in parallel** (`<1.5s` cold start), manages `~/.intochat/secrets/master.key` (DPAPI-protected), and tracks `~/.intochat/run/kernel.pid`.
2. **Zero-Signing Windows One-Line Installer (`eng/install/install.ps1`)**:
   - Recommended mainstream install path (`irm https://github.com/intochat/intochat/releases/latest/download/install.ps1 | iex`): verifies SHA-256, clears Mark-of-the-Web (`Unblock-File`) on staged files under `~/.intochat/bin`, registers User `PATH`, creates the Start Menu shortcut (`IntoChat OS.lnk`, skippable with `-NoShortcut`, optional `-DesktopShortcut`), supports opt-in local sparse package identity via `-RegisterIdentity` (`winapp create-debug-identity`), and provides clean `-Uninstall` that removes binaries, `PATH`, and shortcuts while strictly preserving `~/.intochat/secrets` (`master.key`) and `~/.intochat/state`.
3. **Unified Windows Packager (`eng/package.cs` — MSIX + Setup `.exe` + Portable `.zip`)**:
   - Located at [package.cs](../eng/package.cs) and invoked directly or via the `intochat.cs` launcher / `package-msix` Aspire pipeline step:
     ```powershell
     dotnet run eng/package.cs --platform win-x64 --dev-cert
     # or via launcher shortcut:
     dotnet run intochat.cs package-msix --dev-cert
     ```
   - `PackageWindowsAsync` stages the 4-component bundle (`IntoChat.OS.Desktop.exe` + `intochat.cmd`, Native AOT `os/IntoChat.OS.Kernel.exe`, `unithost/IntoChat.OS.Sandbox.exe`, and `shell/intochat_os_flutter.exe` built from `ui/`), generates the unvirtualized `AppxManifest.xml` (`runFullTrust` + `unvirtualizedResources`, `intochat.exe`/`intochat-os.exe` execution aliases, `intochat://` protocol) and 2021 `IntoChat.OS.appinstaller` (`UpdateBlocksActivation="true"`), and invokes Microsoft's `winapp` CLI (`Microsoft.WinAppCLI`) to emit `IntoChat-{version}-win-x64.msix`, `IntoChat-win-x64.msix`, and `IntoChat.OS.appinstaller` alongside the dual-ship `intochat-{version}-win-x64.zip` and `IntoChat-Setup-{version}-win-x64.exe` artifacts.
   - Release signing precedence in `.github/workflows/release.yml` (`package-windows`):
     1. **Azure Artifact Signing** (`winapp az-sign`) when GitHub repository variables `AZURE_ARTIFACT_SIGNING_ENDPOINT`, `AZURE_ARTIFACT_SIGNING_ACCOUNT`, and `AZURE_ARTIFACT_SIGNING_PROFILE` (plus optional `WINDOWS_PUBLISHER_SUBJECT`) are configured.
     2. **Persistent Windows Dev-Cert Signing** (`--cert` / `--cert-password`) when `WINDOWS_DEV_CERT_PFX_BASE64` (and `WINDOWS_DEV_CERT_PASSWORD`) repository secrets are configured; the temporary PFX in `$RUNNER_TEMP\intochat-devcert.pfx` is always overwritten and deleted in an `if: always()` cleanup step, and CI asserts `AppxSignature.p7x` is present inside `IntoChat-{version}-win-x64.msix` whenever signing is active.
   - The matching public certificate (`eng/release/windows/IntoChat-DevCert.cer`, Subject `CN=IntoChat Dev`, Thumbprint `3365BF08BBE6D23778CE1B61715C1AD6DDDDAD7E`, valid through `2029-11-25`, zero private key material) is copied into every Windows release alongside `eng/install/trust-dev-cert.ps1`. Team members and testers trust it once with `irm https://github.com/intochat/intochat/releases/latest/download/trust-dev-cert.ps1 | iex` (pinned thumbprint guard, `-DryRun`, `-Uninstall`) to sideload and auto-update `IntoChat-win-x64.msix` / `IntoChat.OS.appinstaller` natively without `0x800B0109`.
4. **Azure Infrastructure Installer (`AzureInstaller`)**:
   - Built directly into the Kernel CLI ([AzureInstaller.cs](../src/IntoChat.OS.Kernel/Install/AzureInstaller.cs)):
     ```powershell
     IntoChat.OS.Kernel.exe install --env test --config deployment/test.json
     ```
   - Provisions Azure Container Apps, Azure Storage (Blobs/Tables/Queues), Key Vault, and module deployments directly against Azure Resource Manager (ARM) before any cluster exists.

### Q3: How ready are we to publish a Test Stand on Azure?
**Ready — with two deployment modes available:**
1. **Native AOT Linux Container (`linux-x64`) or Standard Container**:
   - The Kernel's `CloudProfile` (`IntoChatOS:Topology=cloud`), Azure Blob/Table/Queue grain storage (`DigitalBrain.Runtime.Orleans.Azure`), Azure Table clustering (`Orleans.Clustering.AzureStorage`), and Azure Reminders (`Orleans.Reminders.AzureStorage`) are **all preserved in [TrimmerRoots.xml](../src/IntoChat.OS.Kernel/TrimmerRoots.xml) and pre-registered in [OrleansAotResponses.cs](../src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs)**.
   - The health probes (`/health`, `/ready`, `/alive`) and `/drain` endpoint required by Azure Container Apps and YARP reverse proxies are verified returning `200 OK` under Native AOT.
   - **One minor note for OpenAI Image Generation / Voice Transcription in AOT**: During `ilc` compilation, `System.ClientModel` emits a notice on `OpenAIImageGeneration.GenerateAsync` and `OpenAITranscriptionService.TranscribeAsync` (`AsyncStreamingClientResult<T>` version mismatch between `OpenAI` and `System.ClientModel` NuGet packages). Standard chat/agent completions (`IChatClient`, OpenRouter, Anthropic, OpenAI, Google, xAI, Ollama) are unaffected.

---

## 3. Quick Reference Commands

### Publish Native AOT Kernel (`win-x64`)
```powershell
dotnet publish src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj `
  -c Release -r win-x64 /p:IntoChatOSAot=true
```
Output executable:
`artifacts/publish/IntoChat.OS.Kernel/release_win-x64/IntoChat.OS.Kernel.exe`

### Run Native AOT Kernel Locally
```powershell
$env:IntoChatOS__Home = "$env:USERPROFILE\.intochat-aot"
$env:IntoChatOS__Platform__Storage__ConfiguredCustodyAcknowledged = "true"
$env:ASPNETCORE_URLS = "http://127.0.0.1:5080"
.\artifacts\publish\IntoChat.OS.Kernel\release_win-x64\IntoChat.OS.Kernel.exe
```

### Build Windows MSIX + Dual-Ship Portable Package (Native AOT Desktop + Kernel + Sandbox + Flutter)
```powershell
dotnet run eng/package.cs --platform win-x64 --dev-cert --skip-flutter
# or via launcher shortcut:
dotnet run intochat.cs package-msix --dev-cert --skip-shell
```
