# Cutover ledger

## Step 1 — one host (in progress)

### cutover(1): LegacySpi and NeedKind.State

Deleted: 1 file, 42 lines (`framework/src/DigitalBrain/Spi/LegacySpi.cs`).

Ported: timer and reminder ticks go to `Neuron.OnReminder` through `NeuronAttachment.Remind` (`HostTimers`, `NeuronHostGrainBase`, `BrainListenerNeuron`, `SessionSynapseNeuron`, `Fakes.ManualReminders`, `TestNeuronHost`, and the product neurons that overrode `IRemindedNeuron.OnReminder`). Deactivation was already `Neuron.OnDeactivate`. `INeuronTimers`, `IDeactivatingNeuron`, `IRuntimeEndpoint` and `ICallerContextCarrier` had no implementors.

`NeedKind.GrainStore` and `Need.GrainStore` are `NeedKind.State` and `Need.State`. The module template uses `Need.State("state")`. `GrainStoreBinding` stays in the Orleans runtime.

Tests: `DigitalBrain.Tests.Unit` 192 passed; `DigitalBrain.Testing.Tests.Unit` 315 passed, 17 skipped; `DigitalBrain.Runtime.Orleans.Tests.Unit` 224 passed; `VocabularyRingFacts` 6 passed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

Deferred in this commit: kernel composition `Calls`/`Storage`/`Authority`, `RetiredReminders`, `OrleansHostWiring` legacy branches, and `src/IntoChat.OS/Runtime` plain-neuron facades. Those still compile against the old host path.

### Still to do in step 1

- `Program.cs` / server composition only through `AddDigitalBrainRuntime` and `TopologyHosting`.
- Delete `Kernel/{Calls,Storage,Authority}/*`, `RetiredReminders.cs`, legacy `OrleansHostWiring` branches, and `Runtime/{PlainNeurons,PlainSignals,IStillGrains,IBrainCalls}.cs`.
