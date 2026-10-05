using DigitalBrain;
using DigitalBrain.Framework;
using Microsoft.Extensions.Configuration;

namespace DigitalBrain.Testing.E2E;

// The one channel between ModuleTestHost and the relaunched module host process. Composition
// crosses the process boundary as configuration under DigitalBrain:Testing (a reserved section
// every settings validator strips), named and parsed only here.
internal static class ModuleHostChannel
{
    public const string ModuleTypesKey = "DigitalBrain:Testing:ModuleTypes";
    public const string ReferenceCompositionKey = "DigitalBrain:Testing:ReferenceComposition";

    public static string SerializeModules(IEnumerable<Framework.ModuleDefinition> modules)
        => System.Text.Json.JsonSerializer.Serialize(modules.Select(module => module.ModuleType.AssemblyQualifiedName));

    public static bool IsReferenceComposition(IConfiguration configuration)
        => configuration.GetValue<bool>(ReferenceCompositionKey);

    public static IReadOnlyList<Type> ReadModules(IConfiguration configuration)
    {
        var names = System.Text.Json.JsonSerializer.Deserialize<string[]>(configuration[ModuleTypesKey] ?? "[]")!;
        return [.. names.Select(name => Type.GetType(name, throwOnError: true)!)];
    }

    // The relaunched runtime reconstructs each selected module from its serialized type name; the
    // process runs against the test assembly's closure, so the type is already loaded and its
    // parameterless constructor is the same door every other composition uses.
    public static Module CreateModule(Type type) => (Module)Activator.CreateInstance(type)!;
}
