using System.Reflection;
using DigitalBrain.Capabilities;

namespace DigitalBrain;

/// <summary>What a module provides, requires and needs from its host.</summary>
/// <param name="Provides">The capabilities the module implements.</param>
/// <param name="Requires">The capabilities the module calls.</param>
/// <param name="Needs">The resource kinds the host must supply.</param>
/// <param name="Settings">The options the module binds.</param>
/// <param name="Secrets">The secrets the module reads.</param>
public sealed record ModuleManifest(
    IReadOnlyList<CapabilityId> Provides,
    IReadOnlyList<CapabilityId> Requires,
    IReadOnlyList<Need> Needs,
    IReadOnlyList<Setting> Settings,
    IReadOnlyList<Secret> Secrets)
{
    /// <summary>The module id from <see cref="ModuleAttribute"/>; filled by <see cref="Of(Type)"/>.</summary>
    public string? Id { get; init; }

    /// <summary>Reads the <c>[Module]</c> id and the public static <c>Manifest</c> property of a module class.</summary>
    /// <exception cref="InvalidOperationException">The type is not a module, has no <c>[Module]</c> or no static <c>Manifest</c>.</exception>
    public static ModuleManifest Of(Type moduleType)
    {
        ArgumentNullException.ThrowIfNull(moduleType);
        if (!typeof(Module).IsAssignableFrom(moduleType))
        {
            throw new InvalidOperationException($"{moduleType.FullName} does not derive from {typeof(Module).FullName}.");
        }
        var attribute = moduleType.GetCustomAttribute<ModuleAttribute>(inherit: false)
            ?? throw new InvalidOperationException($"{moduleType.FullName} has no [Module(id)] attribute.");
        var property = moduleType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static);
        if (property is null || property.PropertyType != typeof(ModuleManifest)
            || property.GetValue(null) is not ModuleManifest manifest)
        {
            throw new InvalidOperationException($"{moduleType.FullName} has no public static ModuleManifest Manifest property.");
        }
        return manifest with { Id = attribute.Id };
    }
}
