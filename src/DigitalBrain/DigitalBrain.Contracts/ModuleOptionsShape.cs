using System.Reflection;
using System.Text.Json.Serialization;
namespace DigitalBrain.Contracts;

// Module options are plain configuration, projected as env a dashboard can show. Secrets belong
// to the integrations registration — a design rule the integration contracts carry, not a name
// heuristic this type could guess at.
public static class ModuleOptionsShape
{
    public static void Validate<T>()
    {
        foreach (var property in typeof(T).GetProperties())
        {
            // A writable property the serializer skips would silently drop configuration on the
            // flatten/bind round trip.
            if (property.SetMethod is { IsPublic: true }
                && property.GetCustomAttribute<JsonIgnoreAttribute>() is { Condition: not JsonIgnoreCondition.Never })
            { throw new ArgumentException($"{typeof(T).Name}.{property.Name} is writable but excluded from module options serialization."); }
        }
    }
}
