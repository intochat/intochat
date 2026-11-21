using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DigitalBrain.Contracts;

/// <summary>Compile-time JSON schema for one method of a neuron contract.</summary>
public sealed record ToolSchema(
    string Contract,
    string Name,
    string Method,
    string Description,
    string ParametersJson,
    string? DefaultKey,
    string? KeyPattern,
    bool ReadOnly);

/// <summary>Registry of generated <see cref="ToolSchema"/> entries populated by assembly module initializers.</summary>
public static class ToolSchemas
{
    private static readonly ConcurrentDictionary<string, ToolSchema[]> ByContract = new(StringComparer.Ordinal);

    /// <summary>Registers generated tool schemas for their contracts.</summary>
    public static void Register(ToolSchema[] schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        if (schemas.Length == 0) { return; }

        foreach (var group in schemas.GroupBy(static schema => schema.Contract, StringComparer.Ordinal))
        {
            ByContract[group.Key] = [.. group];
        }
    }

    /// <summary>Returns the generated tool schemas for <paramref name="contract"/>, running its module initializer first.</summary>
    public static IReadOnlyList<ToolSchema> Of(Type contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        RuntimeHelpers.RunModuleConstructor(contract.Module.ModuleHandle);
        return contract.FullName is { } fullName && ByContract.TryGetValue(fullName, out var schemas)
            ? schemas
            : [];
    }
}
