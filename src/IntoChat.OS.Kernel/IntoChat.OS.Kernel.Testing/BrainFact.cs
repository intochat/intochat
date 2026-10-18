namespace IntoChat.OS.Kernel.Testing;

// Existing product facts keep their account/browser lifecycle while sharing the E2E telemetry API.
public abstract class BrainFact(ReferenceBrainFixture host) : E2ETest(host);
