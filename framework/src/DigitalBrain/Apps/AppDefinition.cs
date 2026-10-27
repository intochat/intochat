using System.Collections.ObjectModel;
using System.Reflection;
using DigitalBrain.Capabilities;

namespace DigitalBrain;

/// <summary>Validated manifest and operation metadata for trusted app code. Does not construct an app.</summary>
/// <remarks>Reading a declared static Manifest property executes app code. Establish code trust before creating a definition.</remarks>
public sealed partial class AppDefinition
{
    /// <summary>Discovers a concrete, closed App class marked with AppAttribute.</summary>
    public AppDefinition(Type appType)
    {
        ArgumentNullException.ThrowIfNull(appType);
        if (!appType.IsClass || appType.IsAbstract || appType.ContainsGenericParameters || !appType.IsSubclassOf(typeof(App)))
        {
            throw new InvalidOperationException($"{appType.FullName} must be a concrete, closed App class.");
        }
        var attribute = appType.GetCustomAttribute<AppAttribute>(false)
            ?? throw new InvalidOperationException($"{appType.FullName} has no [App(id)] attribute.");
        AppType = appType;
        var operations = new Dictionary<string, MethodInfo>(StringComparer.Ordinal);
        foreach (var method in appType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.ToString(), StringComparer.Ordinal))
        {
            if (!method.IsDefined(typeof(OnAttribute), false)) { continue; }
            var parameters = method.GetParameters();
            var tokens = parameters.Count(p => p.ParameterType == typeof(CancellationToken));
            if (method.IsStatic || method.IsGenericMethodDefinition || parameters.Any(p => p.ParameterType.IsByRef || p.ParameterType.IsPointer || p.ParameterType.IsByRefLike)
                || parameters.Length - tokens > 1 || tokens > 1 || (tokens == 1 && parameters[^1].ParameterType != typeof(CancellationToken))
                || (method.ReturnType != typeof(void) && !typeof(Task).IsAssignableFrom(method.ReturnType) && method.ReturnType != typeof(ValueTask)))
            {
                throw new InvalidOperationException($"{appType.FullName}.{method.Name} has an invalid [On] operation shape; use an instance, non-generic void, Task or ValueTask method with at most one input and an optional trailing CancellationToken.");
            }
            if (!operations.TryAdd(Key(method.Name), method))
            {
                throw new InvalidOperationException($"{appType.FullName} has duplicate operation alias '{method.Name}'.");
            }
        }
        Operations = new ReadOnlyDictionary<string, MethodInfo>(operations);
        Manifest = appType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is AppManifest declared
            ? declared : DeriveManifest(appType, attribute);
        if (!string.Equals(Manifest.Id, attribute.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"App manifest id '{Manifest.Id}' does not match [App] id '{attribute.Id}'.");
        }
    }

    /// <summary>The app class this definition describes.</summary>
    public Type AppType { get; }
    /// <summary>The declared or attribute-derived manifest.</summary>
    public AppManifest Manifest { get; }
    /// <summary>Operations keyed by lowercase method name with dashes removed. MethodInfo exposes dispatch parameter metadata.</summary>
    public IReadOnlyDictionary<string, MethodInfo> Operations { get; }
    /// <summary>Finds an operation ignoring case and dashes; returns null when absent.</summary>
    public MethodInfo? FindOperation(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        return Operations.GetValueOrDefault(Key(operation));
    }

    private static string Key(string name) => name.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    private static AppManifest DeriveManifest(Type appType, AppAttribute attribute)
    {
        var requires = appType.GetConstructors().SelectMany(c => c.GetParameters())
            .Select(p => Contract(p.ParameterType)).OfType<Type>()
            .Select(t => t.GetCustomAttribute<CapabilityAttribute>(false)!.Id).Distinct().ToList();
        var neurons = appType.Assembly.GetTypes().Where(t => t is { IsAbstract: false, IsClass: true }
            && t.Namespace == appType.Namespace && typeof(Neuron).IsAssignableFrom(t) && t.IsDefined(typeof(NeuronAttribute), false)).ToList();
        return new AppManifest(attribute.Id, appType.Name, appType.Assembly.GetName().Version?.ToString() ?? "0.0.0", neurons, new AppRequirements(requires));
    }

    private static Type? Contract(Type type)
    {
        if (type.IsInterface && type.IsDefined(typeof(CapabilityAttribute), false)) { return type; }
        if (type.IsArray) { return ContractElement(type.GetElementType()!); }
        if (!type.IsGenericType) { return null; }
        var definition = type.GetGenericTypeDefinition();
        return definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IReadOnlyCollection<>)
            ? ContractElement(type.GetGenericArguments()[0]) : null;
    }

    private static Type? ContractElement(Type type) => type.IsInterface && type.IsDefined(typeof(CapabilityAttribute), false) ? type : null;
}
