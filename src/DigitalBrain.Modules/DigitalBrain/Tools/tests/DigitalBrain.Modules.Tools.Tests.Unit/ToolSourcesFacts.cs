namespace DigitalBrain.Tools.Tests.Unit;

public sealed class ToolSourcesFacts
{
    [Fact]
    public void ACapabilityIsKeyedByItsSourceAndClipsItsDescription()
    {
        var entry = ToolCapabilitySource.Capability("b/mcp/photocraft", "Test.IFake", "Photo", new("render_preview", "Render", new string('d', 1500), "{\"type\":\"object\"}", false));

        Assert.Equal(("tool:b/mcp/photocraft/render_preview", "b/mcp/photocraft", "render_preview", "Test.IFake"), (entry.Id, entry.Key, entry.Tool, entry.Contract));
        Assert.Equal(ToolCapabilitySource.MaxDescription, entry.Description.Length);
        Assert.Contains("Run(\"render_preview\"", Assert.Single(entry.Methods), StringComparison.Ordinal);
    }
}
