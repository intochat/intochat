using Aspire.Hosting;
using Aspire.Hosting.Yarp;
using IntoChat.OS.Contracts.Configuration;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class FrontHostingFacts
{
    [Fact]
    public void FrontEnabledKeyDefaultsToTrueAndCanBeDisabled()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        Assert.True(builder.Configuration.GetValue(IntoChatOSNames.FrontEnabledKey, true));

        builder.Configuration[IntoChatOSNames.FrontEnabledKey] = "false";
        Assert.False(builder.Configuration.GetValue(IntoChatOSNames.FrontEnabledKey, true));
        Assert.Empty(builder.Resources.OfType<YarpResource>());
    }
}
