using System.Text.Json;
using IntoChat.OS.Modules.AI.Models;
using Microsoft.Extensions.AI;

namespace IntoChat.OS.Modules.AI.Tests.Unit;

public sealed class ToolJsonFacts
{
    [Fact]
    public void NullAndPrimitivesSerializeAsPlainJson()
    {
        Assert.Equal("null", ToolJson.Serialize(null));
        Assert.Equal("42", ToolJson.Serialize(42));
        Assert.Equal("true", ToolJson.Serialize(true));
        Assert.Equal("\"hi\"", ToolJson.Serialize("hi"));
    }

    [Fact]
    public void JsonElementResultIsWrittenVerbatim()
        => Assert.Equal("{\"a\":[1,2]}", ToolJson.Serialize(JsonDocument.Parse("{\"a\":[1,2]}").RootElement));

    [Fact]
    public void ArgumentsAndRefusalDictionariesSerializeAsObjects()
    {
        IDictionary<string, object?> arguments = new Dictionary<string, object?>
        {
            ["isError"] = true,
            ["code"] = "tool_unavailable",
            ["nested"] = JsonDocument.Parse("{\"x\":1}").RootElement,
            ["none"] = null,
        };

        Assert.Equal("{\"isError\":true,\"code\":\"tool_unavailable\",\"nested\":{\"x\":1},\"none\":null}", ToolJson.Serialize(arguments));
    }

    [Fact]
    public void ArgumentsRoundTripThroughTheDictionaryContract()
    {
        var back = (Dictionary<string, object?>?)JsonSerializer.Deserialize("{\"q\":\"x\",\"n\":3}", ToolJson.Type<Dictionary<string, object?>>());

        Assert.NotNull(back);
        Assert.Equal("{\"q\":\"x\",\"n\":3}", ToolJson.Serialize(back));
    }

    [Fact]
    public void UsageDetailsSerializeThroughTheSameOptions()
        => Assert.Contains("\"inputTokenCount\":3", ToolJson.Serialize(new UsageDetails { InputTokenCount = 3 }), StringComparison.Ordinal);
}
