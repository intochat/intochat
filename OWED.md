# Owed

What a change introduced a new way for, without yet removing the old way. One line each: what is owed, and who owns it.
A pull request that adds a line here is not done until the line is gone.

- Rename `Circle*` types and stored identifiers to Friends and Groups, with a data migration for existing shared brains. Owner: the repo owner.
- `ConnectionStorage.Typed` (src/IntoChat.OS.Kernel) lifts an `Actor.App` caller to the kernel of its brain for every provider call, carried from the old Platform stamp (caller-model collapse, Task 6). Narrow it to an explicit connector module actor, or to the app itself once providers admit it. Owner: the repo owner.
- `neurons_call` cannot address screen and control neurons (`app.uikit`, `control.listBox`, `control.chatThread`): `neurons_list` lists them, but `IScreen` and the control types are not in `CompiledTypes`, so the call answers "not a neuron type of this brain". Commands reach them only through `POST /brains/{id}/act` and `screen_act`; Ino's own `neurons_call` attempts fail the same way. Owner: the repo owner.
- Signals are not in telemetry: no span or log names `StateChanged`, `SelectionChanged` or a run finishing, so a control's state change is visible in Aspire only as the `INeuronHost/Invoke` and `ISynapseHost/Nudge` hops of its trace, and an Ino run only as its `invoke_agent ino` span. Owner: the repo owner.
- `ScreenDirectoryNeuron.List` names its shipped apps in a hard-coded list (`uikit` was missing until Task 15); derive it from the app catalog. Owner: the repo owner.
