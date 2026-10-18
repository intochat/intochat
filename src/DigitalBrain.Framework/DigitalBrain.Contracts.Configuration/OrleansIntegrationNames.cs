namespace DigitalBrain.Framework;

// The Orleans integration's id and fields: OrleansModule offers them, a deployment writes them.
public static class OrleansIntegrationNames
{
    public const string Id = "orleans";
    public const string Clustering = "Clustering";
    public const string GrainState = "GrainState";
    public const string Reminders = "Reminders";
    public const string GrainStateContainer = "GrainStateContainer";
}
