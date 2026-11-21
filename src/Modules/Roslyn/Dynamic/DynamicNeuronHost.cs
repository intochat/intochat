using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Modules.Microsoft.Roslyn.Dynamic;

public sealed record DynamicNeuronCompileResult(
    bool Success,
    string[] Diagnostics,
    DynamicNeuronHandle? Handle);

/// <summary>
/// S12b — Zero-restart live neuron handle backed by a collectible AssemblyLoadContext.
/// Allows Ino to compile, load, execute, hot-swap, and unload C# neuron logic on the fly
/// without restarting the host process.
/// </summary>
public sealed class DynamicNeuronHandle : IDisposable
{
    private CollectibleNeuronLoadContext? _context;
    private Assembly? _assembly;

    internal DynamicNeuronHandle(string name, CollectibleNeuronLoadContext context, Assembly assembly)
    {
        Name = name;
        _context = context;
        _assembly = assembly;
    }

    public string Name { get; }
    public bool IsLoaded => _context is not null && _assembly is not null;

    public object? Invoke(string fullTypeName, string methodName, params object?[] args)
    {
        ObjectDisposedException.ThrowIf(!IsLoaded, this);
        var type = _assembly!.GetType(fullTypeName)
            ?? throw new InvalidOperationException($"Type '{fullTypeName}' not found in dynamic neuron '{Name}'.");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            ?? throw new MissingMethodException(fullTypeName, methodName);
        var instance = method.IsStatic ? null : Activator.CreateInstance(type);
        return method.Invoke(instance, args);
    }

    public void Dispose()
    {
        _assembly = null;
        _context?.Unload();
        _context = null;
    }
}

internal sealed class CollectibleNeuronLoadContext(string name)
    : AssemblyLoadContext(name, isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}

/// <summary>
/// Compiles C# source in-memory with Roslyn and loads it into a collectible AssemblyLoadContext.
/// </summary>
public static class DynamicNeuronHost
{
    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(ResolveDefaultReferences);

    public static DynamicNeuronCompileResult CompileAndLoad(string name, string csharpSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(csharpSource);

        var assemblyName = $"DynamicNeuron_{name}_{Guid.NewGuid():N}";
        var syntaxTree = CSharpSyntaxTree.ParseText(csharpSource);
        var compilation = CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: DefaultReferences.Value,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);
        if (!emitResult.Success)
        {
            var errors = emitResult.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            return new DynamicNeuronCompileResult(false, errors, null);
        }

        peStream.Position = 0;
        var alc = new CollectibleNeuronLoadContext(assemblyName);
        var assembly = alc.LoadFromStream(peStream);
        return new DynamicNeuronCompileResult(true, [], new DynamicNeuronHandle(name, alc, assembly));
    }

    private static MetadataReference[] ResolveDefaultReferences()
    {
        var trustedAssemblies = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System.Private.CoreLib.dll",
            "System.Runtime.dll",
            "System.Console.dll",
            "System.Linq.dll",
            "System.Collections.dll",
            "System.Text.RegularExpressions.dll",
            "netstandard.dll",
        };

        var refs = new List<MetadataReference>();
        foreach (var path in trustedAssemblies)
        {
            var fileName = Path.GetFileName(path);
            if (needed.Contains(fileName) && File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return [.. refs];
    }
}
