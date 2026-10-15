namespace DigitalBrain.Platform.Hosting;

public sealed record HostedRuntimeObservation(int ProtocolVersion, string RuntimeVersion, string AccountId,
    string BrainId, string RuntimeId, bool Registered, bool RuntimeHealthy = false, bool StorageAttached = false);
