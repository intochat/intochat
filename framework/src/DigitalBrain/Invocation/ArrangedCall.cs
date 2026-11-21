using System.Reflection;
using System.Text.Json;

namespace DigitalBrain.Invocation;

/// <summary>A contract method bound to its full positional JSON arguments (defaults materialized, CancellationToken omitted).</summary>
public sealed record ArrangedCall(MethodInfo Method, JsonElement[] Arguments);
