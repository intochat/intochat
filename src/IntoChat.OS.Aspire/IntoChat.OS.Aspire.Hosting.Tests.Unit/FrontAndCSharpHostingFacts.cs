using DigitalBrain.Modules.Configuration;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Yarp;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.Microsoft.CSharp;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class FrontAndCSharpHostingFacts
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

    [Fact]
    public void DisablingCSharpModuleAddsNoSandboxContainer()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        builder.Configuration[ModuleConfiguration.ModuleSection("csharp") + ":Enabled"] = "false";
        builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
            .WithHosting<CSharpModuleHosting>();

        Assert.DoesNotContain(builder.Resources, r => r.Name == CSharpModuleHosting.ResourceName);
        Assert.Empty(builder.Resources.OfType<ContainerResource>());
    }
}
