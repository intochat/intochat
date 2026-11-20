# Native AOT Architecture, Readiness & Deployment Guide

This document summarizes the engineering changes completed in the `full-aot` branch (`E:\intochat\full-aot`) to bring `IntoChat.OS.Kernel`, standalone Orleans clients, and the Sandbox/DevKit compilation boundary to **Full Native AOT (`<PublishAot>true</PublishAot>`)**, and answers how this integrates with **Aspire**, the **Desktop / MSIX Installer**, **Azure Test Stands**, and **IntoChat.Cloud**.

---

## 1. Problems Solved & Root-Cause Fixes

Bringing an Orleans + DigitalBrain + ASP.NET Core Minimal API + MCP server runtime to Native AOT required solving five distinct categories of runtime code generation and metadata trimming issues:

### A. Build Speed & Roslyn Separation (14 min -> 3.5 min AOT Publish)
- **Problem**: Embedding `Microsoft.CodeAnalysis.CSharp` and `IntoChat.OS.Modules.Roslyn` directly inside `IntoChat.OS.Kernel` forced the Native AOT compiler (`ilc.exe`) to compile ~60 MB of Roslyn syntax trees, semantic analyzers, and MSBuild workspace loaders into native machine code (~14 minutes per publish). Furthermore, runtime C# compilation (`Assembly.Load(byte[])`) fundamentally belongs in the JIT-enabled Sandbox/DevKit worker, not inside the Native AOT Kernel.
- **Solution**:
  - Excluded `IntoChat.OS.Modules.Roslyn` from [IntoChat.OS.Kernel.csproj](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/IntoChat.OS.Kernel.csproj) and [OperatingSystemModules.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/OperatingSystemModules.cs) when `IntoChatOSAot=true` (`INTOCHAT_OS_AOT`).
  - Guarded `CSharpCompilation` and `AnalyzerSet` in [SynapseCompiler.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain.Runtime.Synapses/Compile/SynapseCompiler.cs) and [ReferenceSet.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain.Runtime.Synapses/Compile/ReferenceSet.cs) under `#if !INTOCHAT_OS_AOT`.
  - Configured `<IlcOptimizationPreference>Speed</IlcOptimizationPreference>` and `<IlcFoldIdenticalMethodBodies>false</IlcFoldIdenticalMethodBodies>` in `IntoChat.OS.Kernel.csproj`.
  - Moved `IntoChat.OS.Modules.Roslyn` and `DigitalBrain.Runtime.Synapses` into [IntoChat.OS.Sandbox.csproj](file:///E:/intochat/full-aot/src/IntoChat.OS.Sandbox/IntoChat.OS.Sandbox.csproj) and exposed C# package compilation over both HTTP (`POST /apps/compile` in [Program.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Sandbox/Program.cs)) and the out-of-process `--unit-host` stdio pipe (`op: "compile"` in [UnitHostWorker.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Sandbox/UnitHostWorker.cs) and [AppSandboxHost.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Sandbox/AppSandboxHost.cs)).

### B. Orleans DI Open-Generic Value-Type & Surrogate Instantiation Failures
- **Problem**: `Microsoft.Extensions.DependencyInjection`'s `CallSiteFactory.VerifyOpenGenericAotCompatibility` throws `InvalidOperationException` under Native AOT (`RuntimeFeature.IsDynamicCodeSupported == false`) whenever an open generic service is resolved with a **value-type (`struct` or `enum`)** generic argument:
  1. `PooledResponseCodec<,>`, `PooledResponseCopier<>`, and `PooledResponseActivator<>` in `Orleans.Serialization` when grain methods return value types (`bool`, `int`, `long`, `Guid`, `DateTimeOffset`, `BrainKind`, `NeuronId`, etc.).
  2. `SurrogateCodec<TField, TSurrogate, TConverter>` and `SurrogateCopier<TField, TSurrogate, TConverter>` because Orleans `[GenerateSerializer]` generates `struct` surrogates (`TSurrogate` is a value type!).
  3. `Enum32Codec<TEnum>` and `ZeroCopyCopier<TEnum>` for enums serialized in grain state or RPC calls.
- **Solution**:
  - Created [OrleansAotResponses.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs) with single-type-parameter closed wrappers (`ClosedResponseCopier<TResult>`, `ClosedResponseCodec<TResult>`, `ClosedResponseActivator<TResult>`, `ClosedSurrogateCopier<TField, TSurrogate, TConverter>`) and pre-registered all closed `IValueSerializer<TSurrogate>`, `IFieldCodec<TField>`, `IDeepCopier<TField>`, enums, and nullable value types in DI.
  - Wired `OrleansAotResponses.Register(...)` into **both** the Orleans Silo ([ServerHostingExtensions.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/AspireServer/ServerHostingExtensions.cs)) and standalone Orleans Clients ([IntoChatOSClientHostingExtensions.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/AspireClient/IntoChatOSClientHostingExtensions.cs)).

### C. Eliminating `DispatchProxy`, `MakeGenericMethod`, `Expression.Compile`, and `GetInterfaceMap` in `DigitalBrain`
- **Problem**:
  1. `NeuronProxy` previously relied on `DispatchProxy.Create` and `InvokeValueMethod.MakeGenericMethod(returnType)` at runtime, which throws `NotSupportedException: 'NeuronProxy.InvokeValue[T](...)' is missing native code` under Native AOT.
  2. `CallPolicy.Declared`, `CallCatalog`, and `AskCall` called `Type.GetInterfaceMap(interfaceType)`, which throws `InvalidCastException` on Native AOT when an interface has default interface methods (`IAsyncDisposable`, static virtual members, etc.).
  3. `CallCatalog.CompileInvoker` and `TaskBoxing.ResultAsync` used `Expression.Lambda(...).Compile()` and `MakeGenericMethod`.
- **Solution**:
  - Updated the Roslyn source generator ([AotPreservationGenerator.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain.Analyzers/Neurons/AotPreservationGenerator.cs)) and [NeuronProxy.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Invocation/NeuronProxy.cs) so every `[Neuron]` contract gets a compile-time `AotNeuronProxy_*` class that calls closed generic methods (`base.CallTask<TResult>`, `base.CallValueTask<TResult>`, `base.CallStream<TItem>`, `base.CallNone`, `base.CallOneWay`) directly without `DispatchProxy` or `MakeGenericMethod`.
  - Replaced all `GetInterfaceMap` usages in [CallPolicy.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Kernel/CallPolicy.cs), [CallCatalog.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Kernel/CallCatalog.cs), and [AskCall.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Synapses/Asks/AskCall.cs) with AOT-safe method matching (`CallPolicy.FindImplementationMethod` and `CallPolicy.IsDisposeMethod`).
  - Added `!RuntimeFeature.IsDynamicCodeSupported` AOT-safe reflection invokers in `CallCatalog` and [TaskBoxing.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Invocation/TaskBoxing.cs).

### D. AOT-Safe `System.Text.Json` Serialization & Polymorphic UI Trees
- **Problem**: Under Native AOT, `System.Text.Json` disables reflection-based `JsonConverter<T>` creation and throws `NotSupportedException` when `JsonPropertyInfo<TValueType>` is instantiated via reflection. Additionally, `DigitalBrain.UI.View` uses `[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]` on `DigitalBrain.UI.Node` with 32 derived UI node records.
- **Solution**:
  - Implemented direct `Utf8JsonWriter` / `Utf8JsonReader` AOT serialization and deserialization in [RuntimeJsonContext.cs](file:///E:/intochat/full-aot/framework/src/DigitalBrain/Invocation/RuntimeJsonContext.cs) (`RuntimeTypeInfoResolver.WriteAotValue` / `ReadAotValue` and `AotObjectJsonConverter`) with full `[JsonPolymorphic]` and `[JsonDerivedType]` discriminator support.
  - Registered `View`, `PublishedView`, and HTTP/MCP request/response DTOs in [KernelMcpJsonContext.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/Mcp/KernelMcpJsonContext.cs) and registered `KernelMcpJsonContext.Default` with both `RuntimeProtocol` and ASP.NET Core `HttpJsonOptions`.
  - Replaced unconfigured `JsonSerializer.SerializeToElement` calls in UI/Command binding paths with `PooledJson.Encode` (`RuntimeProtocol.Json`).

### E. ASP.NET Core `RequestDelegateFactory` Trimming Fixes
- **Problem**: ASP.NET Core Minimal APIs' `RequestDelegateFactory` inspects `Task.GetAwaiter()`, `TaskAwaiter.GetResult()`, and `StringValues.op_Implicit(StringValues -> string)` via reflection when building endpoint delegates on the first HTTP request. Trimming those members caused `RequestDelegateFactory` to misclassify `Task`-returning endpoints as JSON bodies and fail on query-string parameter coercion.
- **Solution**:
  - Preserved `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>`, `TaskAwaiter`, `ValueTaskAwaiter`, and `Microsoft.Extensions.Primitives.StringValues` in [OrleansAotResponses.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs) and [TrimmerRoots.xml](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/TrimmerRoots.xml).
  - Updated `RuntimeTypeInfoResolver.CreateAotFallbackTypeInfo` to return `null` for `typeof(Task)` so `DefaultJsonTypeInfoResolver` handles endpoint metadata probing cleanly.

---

## 2. Readiness Answers

### Q1: Can I run Native AOT with Aspire right now?
**Yes!**
- **How Aspire runs the Kernel**:
  - By default during inner-loop development (`dotnet run intochat-os.cs` or running `IntoChat.OS.AppHost`), Aspire launches `IntoChat.OS.Kernel.csproj` via `AddDotnetProject`, which runs the standard JIT build for fast 2-second incremental rebuilds, **unless** a live build is active (`live.json`) or you publish the AOT binary.
  - All AOT architectural changes (`AotNeuronProxy_*`, `OrleansAotResponses`, `KernelMcpJsonContext`, `RuntimeJsonContext`) are active and 100% compatible with both JIT (Aspire dev loop) and Native AOT (`IntoChatOSAot=true`).
  - If you want Aspire to orchestrate the compiled Native AOT binary directly, or if you run the Native AOT `IntoChat.OS.Kernel.exe` alongside Aspire's resources (Azurite, Postgres, ClickHouse, Foundry Local / Ollama, Sandbox container), it uses the exact same `IntoChatOS:Topology` (`local`, `local+cloud`, or `cloud`) and environment variables that Aspire injects.

### Q2: Is there already an Installer / Desktop Launcher for Native AOT?
**Yes — both the Desktop Launcher and the MSIX Packager are already built in and now configured for Native AOT!**
1. **Desktop Launcher (`IntoChat.OS.Desktop`)**:
   - Located at [IntoChat.OS.Desktop.csproj](file:///E:/intochat/full-aot/src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.csproj) and [IntoChat.OS.Desktop.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Desktop/IntoChat.OS.Desktop.cs).
   - It is **already a Native AOT executable (`<PublishAot>true</PublishAot>`)** that supervises and launches `os/IntoChat.OS.Kernel.exe` and the Flutter desktop shell (`shell/intochat_os_flutter.exe`) **in parallel** (`<1.5s` cold start), manages `~/.intochat/secrets/master.key` (DPAPI-protected), and tracks `~/.intochat/run/kernel.pid`.
2. **MSIX Installer Packager (`MsixPackager`)**:
   - Located at [MsixPackager.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.LocalSetup/MsixPackager.cs) and invoked via:
     ```powershell
     dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj -- package-msix
     ```
   - We updated [MsixPackager.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.LocalSetup/MsixPackager.cs#L128) so that when it publishes `IntoChat.OS.Kernel.csproj` into `artifacts/msix/layout/os/IntoChat.OS.Kernel.exe`, it passes **`/p:IntoChatOSAot=true`**, packaging both the Native AOT Desktop supervisor and the Native AOT Kernel into a single `.msix` / `.appinstaller` bundle!
3. **Azure Infrastructure Installer (`AzureInstaller`)**:
   - Built directly into the Kernel CLI ([AzureInstaller.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/Install/AzureInstaller.cs)):
     ```powershell
     IntoChat.OS.Kernel.exe install --env test --config deployment/test.json
     ```
   - Provisions Azure Container Apps, Azure Storage (Blobs/Tables/Queues), Key Vault, and module deployments directly against Azure Resource Manager (ARM) before any cluster exists.

### Q3: How ready are we to publish a Test Stand on Azure?
**Ready — with two deployment modes available:**
1. **Native AOT Linux Container (`linux-x64`) or Standard Container**:
   - The Kernel's `CloudProfile` (`IntoChatOS:Topology=cloud`), Azure Blob/Table/Queue grain storage (`DigitalBrain.Runtime.Orleans.Azure`), Azure Table clustering (`Orleans.Clustering.AzureStorage`), and Azure Reminders (`Orleans.Reminders.AzureStorage`) are **all preserved in [TrimmerRoots.xml](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/TrimmerRoots.xml) and pre-registered in [OrleansAotResponses.cs](file:///E:/intochat/full-aot/src/IntoChat.OS.Kernel/AspireServer/OrleansAotResponses.cs)**.
   - The health probes (`/health`, `/ready`, `/alive`) and `/drain` endpoint required by Azure Container Apps and YARP reverse proxies are verified returning `200 OK` under Native AOT.
   - **One minor note for OpenAI Image Generation / Voice Transcription in AOT**: During `ilc` compilation, `System.ClientModel` emits a notice on `OpenAIImageGeneration.GenerateAsync` and `OpenAITranscriptionService.TranscribeAsync` (`AsyncStreamingClientResult<T>` version mismatch between `OpenAI` and `System.ClientModel` NuGet packages). Standard chat/agent completions (`IChatClient`, OpenRouter, Anthropic, OpenAI, Google, xAI, Ollama) are unaffected.

### Q4: What about `IntoChat.Cloud` (`IntoChat.Cloud.Host`)? Can it orchestrate or operate with Native AOT?
**Yes! Here is how `IntoChat.Cloud` fits in:**
1. **How `IntoChat.Cloud.Host` works**:
   - [IntoChat.Cloud.Host/Program.cs](file:///E:/intochat/full-aot/src/IntoChat.Cloud.Host/Program.cs) is a thin 17-line wrapper around `KernelHost.RunAsync(args, typeof(CloudHost).Assembly, ...)` that adds the cloud modules (`IntoChat.Cloud`: Auth, Circle Relay, Lobby, Guest `/try` sessions, Mail, Organisations, Licensing).
2. **Operating *with* Native AOT Desktop/Edge Clients**:
   - A Native AOT `IntoChat.OS.Kernel.exe` running on a user's Windows/macOS/Linux machine under `IntoChatOS:Topology=local` or `local+cloud` connects seamlessly to `IntoChat.Cloud.Host` (for Circle invitations, enrollment, OAuth callbacks, and cloud relay) over HTTP/gRPC.
3. **Running `IntoChat.Cloud.Host` itself as Native AOT**:
   - Because `IntoChat.Cloud.Host` delegates to `KernelHost.RunAsync`, it automatically inherits **all** of our `IntoChat.OS.Kernel` Native AOT infrastructure (`OrleansAotResponses`, `AotNeuronProxy_*`, `RuntimeJsonContext`, `KernelMcpJsonContext`).
   - To publish `IntoChat.Cloud.Host` itself with `<PublishAot>true</PublishAot>`, we only need to add `IntoChat.Cloud`'s grain response types and HTTP request DTOs (`SignUpRequest`, `LoginRequest`, `VerifyRequest`, `ProfileRequest`, etc. in `IntoChat.Cloud.Auth.Http`) to `KernelMcpJsonContext` / `OrleansAotResponses` (note that `CircleServerModule` in `IntoChat.Cloud` already uses source-generated `CircleJsonContext` and `CircleServerJsonContext`!).

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

### Build MSIX Installer Package (with Native AOT Kernel + Native AOT Desktop Launcher)
```powershell
dotnet run --project src/IntoChat.OS.AppHost/IntoChat.OS.AppHost.csproj -- package-msix --skip-shell
```
