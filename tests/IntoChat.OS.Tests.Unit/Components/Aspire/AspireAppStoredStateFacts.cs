using System.Text;
using DigitalBrain.Invocation;
using IntoChat.Aspire.Screens;

namespace IntoChat.OS.Tests.Unit.Components.Aspire;

// The Aspire app's stored state, written before the screen moved from IntoChat.OS.Apps into the Aspire component, still loads.
// The golden was generated from the unmoved type through RuntimeProtocol (as AspireStoredStateFacts does) and is frozen.
public sealed class AspireAppStoredStateFacts
{
    private const string File = "AspireAppState.json";

    private static AspireAppState State() => new(
        BaseUrl: "https://aspire.internal:18888",
        Section: "traces",
        TraceId: "4bf92f3577b34da6a3ce929d0e0e4736",
        IsConfiguredEndpoint: true,
        ShowConnectBar: false,
        NavigationLocked: true,
        ReloadRevision: 1,
        LastTransition: IntoChat.OS.UI.ViewTransition.FadeThrough,
        Status: "Aspire Dashboard · Trace 4bf92f3577b3");

    private static string Golden() => Path.Combine(AppContext.BaseDirectory, "AspireGolden", File);

    [Fact]
    public void TheAppStateWrittenBeforeTheMoveStillLoads()
    {
        var actual = RuntimeProtocol.SerializeToUtf8Bytes(State(), typeof(AspireAppState));
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            var source = Path.Combine(Path.GetDirectoryName(IntoChat.OS.Aspire.Hosting.Tests.Unit.DeploymentSnapshot.ProjectPath("IntoChat.OS.Tests.Unit"))!, "Components", "Aspire", "Golden", File);
            System.IO.File.WriteAllBytes(source, actual);
            return;
        }
        Assert.True(System.IO.File.Exists(Golden()), $"Golden file {File} is missing.");
        var stored = System.IO.File.ReadAllBytes(Golden());
        Assert.Equal(stored, actual);
        var read = (AspireAppState)RuntimeProtocol.Deserialize(stored, typeof(AspireAppState))!;
        Assert.Equal(State(), read);
        Assert.DoesNotContain("IntoChat", Encoding.UTF8.GetString(stored), StringComparison.Ordinal);
    }
}
