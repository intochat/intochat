namespace IntoChat.OS.Host.Configuration;

// The orleans module's id and its grain-state container option. Clustering, grain state and reminders are no longer
// integration fields: IntoChatOS:Topology selects them, and IntoChatOS:Cloud:Storage names the cloud account (03-12).
public static class OrleansIntegrationNames
{
    public const string Id = "orleans";
    // The module option (Modules:orleans:Options:GrainStateContainer) a deployment writes for its grain-state container.
    public const string GrainStateContainer = "GrainStateContainer";
}
