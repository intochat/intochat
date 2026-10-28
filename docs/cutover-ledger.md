# Cutover ledger

## Step 1 — one host (in progress)

### cutover(1): LegacySpi and NeedKind.State

Deleted: 1 file, 42 lines (`framework/src/DigitalBrain/Spi/LegacySpi.cs`).

Ported: timer and reminder ticks go to `Neuron.OnReminder` through `NeuronAttachment.Remind` (`HostTimers`, `NeuronHostGrainBase`, `BrainListenerNeuron`, `SessionSynapseNeuron`, `Fakes.ManualReminders`, `TestNeuronHost`, and the product neurons that overrode `IRemindedNeuron.OnReminder`). Deactivation was already `Neuron.OnDeactivate`. `INeuronTimers`, `IDeactivatingNeuron`, `IRuntimeEndpoint` and `ICallerContextCarrier` had no implementors.

`NeedKind.GrainStore` and `Need.GrainStore` are `NeedKind.State` and `Need.State`. The module template uses `Need.State("state")`. `GrainStoreBinding` stays in the Orleans runtime.

Tests: `DigitalBrain.Tests.Unit` 192 passed; `DigitalBrain.Testing.Tests.Unit` 315 passed, 17 skipped; `DigitalBrain.Runtime.Orleans.Tests.Unit` 224 passed; `VocabularyRingFacts` 6 passed. `dotnet build Intochat.OS.slnx -c Release` 0 errors, 0 warnings.

### cutover(1): legacy grain storage and retired reminders

Deleted: 9 files, 436 lines. The kernel `Storage` folder (legacy grain store, journal, persistent-state factory) and `RetiredReminders.cs`. The host registers BrainLog with `UseClusterStore(new BrainStorageRegistration(...))` and `OsBrainKeyCustody` before `ConfigureTopologySilo`, which is what calls `AddDigitalBrainRuntime`. Production custody is still checked by `BrainStoreCustody.Validate` on silo startup. `OsStorageFacts` and `RetiredReminderFacts` were DI and migration checks and were deleted with the code.

`IntoChat.OS.Kernel.Tests.Unit` 464 passed. `SyncOverAsyncFacts` 1 passed. `TopologyCompositionFacts` and `SideBySideRuntimeFacts` 5 passed. Solution build 0 errors, 0 warnings.

### Still to do in step 1

- Delete `Kernel/{Calls,Authority}/*` and `Runtime/{PlainNeurons,PlainSignals,IStillGrains,IBrainCalls}.cs`, and point callers at `SiloNeurons` / session synapses.
- Remove the legacy branches in `OrleansHostWiring` (cloud container overlay, membership feed) once their callers are on the topology profile.
