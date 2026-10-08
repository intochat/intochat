namespace DigitalBrain.Testing.E2E;

// The Production reference brain with the AI module's content capture switched off.
public sealed class ProductionCaptureOffFixture : ProductionBrainFixture
{
    protected override bool SensitiveCapture => false;
}
