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
        Assert.Contains(screenTools, static t => t.Name == "screen_act" && !t.ReadOnly);

        var act = screenTools.Single(static t => t.Name == "screen_act");
        using var json = JsonDocument.Parse(act.ParametersJson);
        var props = json.RootElement.GetProperty("properties");
        Assert.Equal("object", props.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal(["alias", "generation", "nodeKey", "fieldValue"], props.GetProperty("action").GetProperty("properties").EnumerateObject().Select(static p => p.Name));

        var brainTools = ToolSchemas.Of(typeof(IBrain));
        Assert.Contains(brainTools, static t => t.Name == "brain_read" && t.ReadOnly);
        Assert.Empty(ToolSchemas.Of(typeof(string)));
    }
}
