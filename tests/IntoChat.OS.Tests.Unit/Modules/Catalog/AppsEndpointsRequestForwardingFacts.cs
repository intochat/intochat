using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using IntoChat.OS.Apps.Desktop;
using IntoChat.OS.Apps.Settings;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Tests;

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
    public void EnterKernelSetsKernelActorAndDisposeRestoresOuterCaller()
    {
        var edge = TestEdge.Authority;
        using var outer = edge.Enter(edge.Person("ada", "ada", new BrainId("owner")));
        Assert.False(edge.IsKernelActive);

        using (edge.EnterKernel(new BrainId("owner")))
        {
            Assert.True(edge.IsKernelActive);
            Assert.IsType<Actor.Kernel>(edge.Current?.Actor);
            Assert.Equal("owner", edge.Current!.Brain.Value);
        }

        Assert.False(edge.IsKernelActive);
        Assert.IsType<Actor.Person>(edge.Current?.Actor);
    }

    [Fact]
    public void EnterAppSetsAppActorAndDisposeRestoresEmptyCaller()
    {
        var edge = TestEdge.Authority;
        using var cleared = edge.ClearCaller();
        Assert.Null(edge.Current);

        var caller = edge.App("owner/apps/files", new Actor.Person("owner", "owner"), new BrainId("owner"));
        using (edge.Enter(caller))
        {
            Assert.False(edge.IsKernelActive);
            var app = Assert.IsType<Actor.App>(edge.Current?.Actor);
            Assert.Equal("owner/apps/files", app.Key);
            Assert.Equal("owner", app.For.Principal);
            Assert.Equal("owner", edge.Current!.Brain.Value);
        }

        Assert.Null(edge.Current);
    }
}
