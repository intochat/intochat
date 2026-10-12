namespace DigitalBrain.Flame.Stage;

// The one place the stage's owner form is named. A synapse cannot compute a person's scope digest, so the
// brain owner's stage is addressable as {brain}/stage/owner; the module owns the digest form in the same key.
public static class StageKey
{
    public const string Segment = "stage";
    public const string OwnerName = "owner";
    public static string ForOwner(string brainId) => $"{brainId}/{Segment}/{OwnerName}";
}
