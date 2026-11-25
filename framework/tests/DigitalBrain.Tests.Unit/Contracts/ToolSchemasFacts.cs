using System.Text.Json;
using DigitalBrain.Brains;
using DigitalBrain.Contracts;

namespace DigitalBrain.Tests.Unit.Contracts;

public sealed class ToolSchemasFacts
{
    [Fact]
    public void DigitalBrain_contracts_register_schemas_via_module_initializer()
    {
        var screenTools = ToolSchemas.Of(typeof(IScreen));
        Assert.Contains(screenTools, static t => t.Name == "screen_open" && t.ReadOnly);
        // A screen takes commands only through Dispatch: no tool acts on it by node key.
        Assert.DoesNotContain(screenTools, static t => t.Name == "screen_act");
        var dispatch = screenTools.Single(static t => t.Name == "screen_dispatch");
        Assert.False(dispatch.ReadOnly);
        using var json = JsonDocument.Parse(dispatch.ParametersJson);
        var command = json.RootElement.GetProperty("properties").GetProperty("command");
        Assert.Equal("object", command.GetProperty("type").GetString());
        Assert.Equal(["target", "signal", "args", "props"], command.GetProperty("properties").EnumerateObject().Select(static p => p.Name));

        var brainTools = ToolSchemas.Of(typeof(IBrain));
        Assert.Contains(brainTools, static t => t.Name == "brain_read" && t.ReadOnly);
        Assert.Empty(ToolSchemas.Of(typeof(string)));
    }
}
