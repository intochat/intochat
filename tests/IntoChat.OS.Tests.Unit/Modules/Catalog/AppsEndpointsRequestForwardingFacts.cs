using System.Text.Json;
using DigitalBrain.Capabilities;
using DigitalBrain.Runtime.Orleans.Capabilities;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Framework;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Modules.Catalog.Tests;

public sealed class AppsEndpointsRequestForwardingFacts
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    public void BuildScreenRequestArgsMapsDesktopNarrowBooleanLiteralToNarrowField(string literal)
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("desktop", "narrow", literal);
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<DesktopNarrow>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal(literal, signal!.Narrow);
    }

    [Fact]
    public void BuildScreenRequestArgsMapsDesktopOpenAppNameToAppField()
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("desktop", "open", "photocraft");
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<DesktopOpen>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal("photocraft", signal!.App);
    }

    [Fact]
    public void BuildScreenRequestArgsMapsSettingsNavigateToSectionField()
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("settings", "navigate", "models");
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<SettingsNavigate>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal("models", signal!.Section);
    }

    [Fact]
    public void BuildScreenRequestArgsPreservesJsonObjectPayloads()
    {
        var args = AppsEndpoints.BuildScreenRequestArgs("desktop", "open", """{"app":"ino","view":"chat"}""");
        Assert.NotNull(args);

        var signal = args!.Value.Deserialize<DesktopOpen>(JsonOptions);
        Assert.NotNull(signal);
        Assert.Equal("ino", signal!.App);
        Assert.Equal("chat", signal.View);
    }

    [Fact]
    public void EnterKernelStampsRuntimeCallerContextAndDisposeRestoresClearedState()
    {
        using var cleared = InProcessCallerScope.ClearCaller();
        Assert.False(RuntimeCallerContext.TryGet(out _));

        using (var _ = InProcessCallerScope.EnterKernel("owner"))
        {
            Assert.True(RuntimeCallerContext.TryGet(out var stamped));
            Assert.Equal("owner", stamped.BrainId);
            Assert.Equal(CallerKind.Platform, stamped.Kind);
        }

        Assert.False(RuntimeCallerContext.TryGet(out _));
    }

    [Fact]
    public void EnterAppStampsRuntimeAndAmbientAppCaller()
    {
        using var cleared = InProcessCallerScope.ClearCaller();
        Assert.False(RuntimeCallerContext.TryGet(out _));

        using (var _ = InProcessCallerScope.EnterApp("owner", "owner/apps/files"))
        {
            Assert.True(RuntimeCallerContext.TryGet(out var stamped));
            Assert.Equal(CallerKind.App, stamped.Kind);
            Assert.Equal("owner/apps/files", stamped.AppId);
            Assert.Equal("app:owner/apps/files", stamped.PrincipalId);
            Assert.Equal("owner", stamped.BrainId);
        }

        Assert.False(RuntimeCallerContext.TryGet(out _));
    }
}
