using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using DigitalBrain;
using DigitalBrain.Apps.Packaging;
using DigitalBrain.Contracts;

namespace IntoChat.OS.Cli;

// Runs only in the short-lived __extract-app-metadata process. Never called by a receiving host.
internal static class BuildMetadata
{
    internal static async Task Extract(string assemblyPath, string destination, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
        Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        {
            var path = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }
        AssemblyLoadContext.Default.Resolving += Resolve;
        try
        {
            var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
            var types = assembly.GetTypes();
            var apps = types.Where(t => t.IsClass && !t.IsAbstract && t.IsDefined(typeof(AppAttribute), false)).ToArray();
            if (apps.Length != 1) { throw new InvalidOperationException("The app project must declare exactly one [App] entry class."); }
            var definition = new AppDefinition(apps[0]);
            var contexts = types.Where(t => !t.IsAbstract && typeof(JsonSerializerContext).IsAssignableFrom(t)).ToArray();
            if (contexts.Length != 1) { throw new InvalidOperationException("Declare exactly one app-owned JsonSerializerContext with [JsonSerializable] metadata for all app inputs, state and private contracts."); }
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var context = (JsonSerializerContext?)Activator.CreateInstance(contexts[0], options)
                ?? throw new InvalidOperationException("The app JsonSerializerContext needs a public JsonSerializerOptions constructor.");
            foreach (var type in RequiredTypes(definition, types).Distinct())
            {
                if (context.GetTypeInfo(type) is null)
                { throw new InvalidOperationException($"JsonSerializerContext {contexts[0].FullName} is missing {type.FullName}. Add [JsonSerializable(typeof({type.Name}))]."); }
            }
            var descriptor = TrustedBuildMetadata.Extract(apps[0], definition.Manifest);
            var shared = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFiles(directory, "*.dll"))
            {
                var name = AssemblyName.GetAssemblyName(path);
                if (Shared(name.Name!)) { shared[name.Name!] = name.Version!.ToString(); }
            }
            descriptor = descriptor with
            {
                HostMetadata = descriptor.HostMetadata.Add("jsonContext", contexts[0].FullName!),
                SharedAssemblies = shared.ToImmutable(),
                Presentation = descriptor.Presentation.SetItem("operations", JsonSerializer.Serialize(definition.Operations.Keys.Order(StringComparer.Ordinal))),
            };
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(descriptor), ct).ConfigureAwait(false);
        }
        finally { AssemblyLoadContext.Default.Resolving -= Resolve; }
    }

    private static bool Shared(string name) => name is "DigitalBrain" or "DigitalBrain.UI"
        or "DigitalBrain.Capabilities.Storage" or "DigitalBrain.Capabilities.Secrets" or "DigitalBrain.Capabilities.Identity"
        or "DigitalBrain.Runtime.Orleans" or "DigitalBrain.Runtime.Orleans.Contracts";

    private static IEnumerable<Type> RequiredTypes(AppDefinition definition, Type[] types)
    {
        foreach (var method in definition.Operations.Values)
        {
            foreach (var parameter in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))) { yield return parameter.ParameterType; }
        }
        foreach (var neuron in definition.Manifest.Neurons)
        {
            for (var current = neuron; current is not null; current = current.BaseType)
            { if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Neuron<>)) { yield return current.GetGenericArguments()[0]; } }
        }
        foreach (var type in types)
        {
            if (type.IsDefined(typeof(SignalAttribute), false)) { yield return type; }
            if (!type.IsInterface || !type.IsDefined(typeof(NeuronAliasAttribute), false)) { continue; }
            foreach (var method in type.GetMethods().Concat(type.GetInterfaces().SelectMany(i => i.GetMethods())))
            {
                foreach (var parameter in method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken))) { yield return parameter.ParameterType; }
                var result = method.ReturnType;
                if (result.IsGenericType && (result.GetGenericTypeDefinition() == typeof(Task<>) || result.GetGenericTypeDefinition() == typeof(ValueTask<>) || result.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>)))
                { yield return result.GetGenericArguments()[0]; }
            }
        }
    }
}
