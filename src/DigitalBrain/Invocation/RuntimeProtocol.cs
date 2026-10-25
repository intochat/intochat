using System.Text.Json;

namespace DigitalBrain.Invocation;

/// <summary>The wire options every invocation is encoded and decoded with.</summary>
public static class RuntimeProtocol
{
    /// <summary>Web defaults with a depth of 128: a bounded UI tree of 32 nodes, each with a children array, fits inside an invocation.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
}
