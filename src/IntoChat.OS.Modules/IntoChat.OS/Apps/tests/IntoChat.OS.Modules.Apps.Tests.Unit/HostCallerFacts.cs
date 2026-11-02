using DigitalBrain.Capabilities;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Apps.Tests.Unit;

public sealed class HostCallerFacts
{
    [Fact]
    public void A_person_acts_through_the_app()
    {
        var owner = CapabilityCallers.Of("owner", "owner", "local");
        var asApp = HostCaller.AsApp(owner, "local/apps/files");
        Assert.Equal(CallerKind.App, asApp.Kind);
        Assert.Equal("local/apps/files", asApp.AppKey);
        Assert.Equal("owner", asApp.AccountId);
    }

    [Fact]
    public void An_app_stamp_moves_to_the_named_app()
    {
        var other = CapabilityCallers.Of("owner", "owner", "local", CallerKind.App, "local/apps/desktop");
        Assert.Equal("local/apps/files", HostCaller.AsApp(other, "local/apps/files").AppKey);
    }

    [Fact]
    public void The_platform_fallback_stays_the_platform()
    {
        var platform = CapabilityCallers.Platform("local", "local", "local");
        Assert.Same(platform, HostCaller.AsApp(platform, "local/apps/files"));
        Assert.Equal(CallerKind.Platform, HostCaller.AsApp(platform, "local/apps/files").Kind);
    }
}
