namespace DigitalBrain.Platform.Hosting;

// Registration is directory state, not proof that execution or storage is ready.
public sealed record RuntimeObservation(
    int ProtocolVersion,
    string RuntimeVersion,
    string AccountId,
    string BrainId,
    bool Registered,
    string[] Capabilities);
