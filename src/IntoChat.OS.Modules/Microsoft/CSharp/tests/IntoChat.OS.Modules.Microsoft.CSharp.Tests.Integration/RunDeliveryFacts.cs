using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class RunDeliveryFacts
{
    [Fact]
    public void ABehaviorDoesNotWakeForARunRevisionThatOnlyRecordsItself()
    {
        // The runtime reads a run revision as its journal JSON (06-08: BrainRunsChanged is a plain [Signal] record).
        static string Json(BrainRunsChanged changed) => System.Text.Json.JsonSerializer.Serialize(changed, System.Text.Json.JsonSerializerOptions.Web);
        var alias = SubscriptionFilter.RunsChangedAlias;
        var own = Json(new BrainRunsChanged(4, ["brain/apps/brainview/runs.cs"]));
        Assert.False(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", alias, own));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/ino/send.cs", alias, own));
        var mixed = Json(new BrainRunsChanged(5, ["brain/apps/ino/send.cs", "brain/apps/brainview/runs.cs"]));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", alias, mixed));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", alias, Json(new BrainRunsChanged(6))));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", Pings.Pinged, own));
    }
}
