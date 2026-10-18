using IntoChat.OS.Microsoft.CSharp.Runtime;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

public sealed class RunDeliveryFacts
{
    [Fact]
    public void ABehaviorDoesNotWakeForARunRevisionThatOnlyRecordsItself()
    {
        var own = new BrainRunsChanged(4, ["brain/apps/brainview/runs.cs"]);
        Assert.False(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", null, own));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/ino/send.cs", null, own));
        var mixed = new BrainRunsChanged(5, ["brain/apps/ino/send.cs", "brain/apps/brainview/runs.cs"]);
        Assert.True(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", null, mixed));
        Assert.True(SubscriptionFilter.Delivers("brain/apps/brainview/runs.cs", null, new BrainRunsChanged(6)));
    }
}
