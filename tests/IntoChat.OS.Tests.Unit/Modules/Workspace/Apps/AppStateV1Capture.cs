// AppStateV1.bin is an AppState stored before the caller model changes, captured on master 6a51de7e0.
// Production persists a Neuron<AppState> ("app2") through the cluster grain store (BrainGrainStorage / KernelState into the brain journal),
// which writes System.Text.Json UTF-8 (not the Orleans binary format): JsonSerializer.SerializeToUtf8Bytes(state, RuntimeProtocol.OptionsFor(typeof(AppState))),
// the web-defaults options of DigitalBrain.Invocation.RuntimeProtocol (camelCase).
// The state was new AppState { Principal = new CapabilityCaller("owner", "owner", "owner", "owner/apps/demo", "c1") }, every other member at its default.
