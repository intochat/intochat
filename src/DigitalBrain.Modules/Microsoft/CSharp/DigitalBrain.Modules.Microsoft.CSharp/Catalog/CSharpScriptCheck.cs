using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace DigitalBrain.Microsoft.CSharp.Catalog;

public sealed record ScriptDiagnostic(string File, string Id, int Line, string Message);

public sealed record CompiledBehavior(byte[] Assembly, byte[] Symbols, string Hash);

public sealed record ScriptCompilation(bool Success, IReadOnlyList<ScriptDiagnostic> Errors, IReadOnlyDictionary<string, CompiledBehavior> Assemblies);

// The single compiler: compiles each behavior in-process against what the sandbox carries and,
// when every file is clean, emits one assembly per file named by its content hash. The sandbox only
// loads what this emits. The implicit console usings plus the script client's namespaces are imported.
// A file without top-level statements is shared: it is compiled into every synapse's assembly and
// yields none itself. A synapse is a file with top-level statements; the split is purely syntactic.
public sealed class CSharpScriptCheck
{
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using DigitalBrain.Client;
        global using DigitalBrain;
        """;

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);
    // Deterministic so one source always yields one hash: no PDB path, no timestamps.
    private static readonly CSharpCompilationOptions Options = new(OutputKind.ConsoleApplication, deterministic: true, nullableContextOptions: NullableContextOptions.Enable);
    private static readonly EmitOptions Emit = new(debugInformationFormat: DebugInformationFormat.PortablePdb);

    public static bool IsShared(string source)
        => !CSharpSyntaxTree.ParseText(source ?? "", Parse).GetCompilationUnitRoot().Members.OfType<global::Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any();

    public ScriptCompilation Compile(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var libraries = files.Where(file => IsShared(file.Value)).OrderBy(file => file.Key, StringComparer.Ordinal).ToArray();
        var programs = files.Where(file => !IsShared(file.Value)).OrderBy(file => file.Key, StringComparer.Ordinal).ToArray();
        if (programs.Length == 0) { throw new ArgumentException("Pass at least one synapse (a file with top-level statements) to compile; files of only type declarations are shared, not programs.", nameof(files)); }
        var errors = new List<ScriptDiagnostic>();
        var assemblies = new Dictionary<string, CompiledBehavior>(StringComparer.Ordinal);
        var shared = libraries.Select(library =>
        {
            ArgumentNullException.ThrowIfNull(library.Value, library.Key);
            return CSharpSyntaxTree.ParseText(SourceText.From(library.Value, Encoding.UTF8), Parse, library.Key);
        }).Append(CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), Parse, "GlobalUsings.cs")).ToArray();
        var reportedLibraryErrors = false;
        foreach (var (path, source) in programs)
        {
            ArgumentNullException.ThrowIfNull(source, path);
            var trees = shared.Prepend(CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), Parse, path)).ToArray();
            var compilation = CSharpCompilation.Create("behavior", trees, References.Value, Options);
            using var assembly = new MemoryStream();
            using var symbols = new MemoryStream();
            var emitted = compilation.Emit(assembly, symbols, options: Emit);
            foreach (var diagnostic in emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            {
                var file = diagnostic.Location.SourceTree?.FilePath ?? path;
                // A library error shows once, not once per program that includes it.
                if (file != path) { if (reportedLibraryErrors) { continue; } }
                errors.Add(new ScriptDiagnostic(file, diagnostic.Id, diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1, diagnostic.GetMessage()));
            }
            reportedLibraryErrors = true;
            if (emitted.Success)
            {
                var bytes = assembly.ToArray();
                assemblies[path] = new(bytes, symbols.ToArray(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        return new(errors.Count == 0, errors, errors.Count == 0 ? assemblies : new Dictionary<string, CompiledBehavior>());
    }

    // The bundle's shared classes alone, as one library: where its rules (ILogic<T>) live. Null when the
    // bundle has no shared class or they do not compile; the publish gate reports those errors.
    public CompiledBehavior? CompileShared(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var libraries = files.Where(file => IsShared(file.Value)).OrderBy(file => file.Key, StringComparer.Ordinal).ToArray();
        if (libraries.Length == 0) { return null; }
        var trees = libraries.Select(library => CSharpSyntaxTree.ParseText(SourceText.From(library.Value, Encoding.UTF8), Parse, library.Key))
            .Append(CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), Parse, "GlobalUsings.cs"));
        var compilation = CSharpCompilation.Create("rules", trees, References.Value, Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
        using var assembly = new MemoryStream();
        if (!compilation.Emit(assembly).Success) { return null; }
        var bytes = assembly.ToArray();
        return new(bytes, [], Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // Exactly what the sandbox image carries: the .NET runtime, plus the vocabulary, the client and every
    // contract assembly with what they depend on. Nothing else this host loads (the framework ring, the
    // platform, drivers, the compiler) is a behavior's to reach; it would compile here and fail to load there.
    private static MetadataReference[] LoadReferences()
    {
        var available = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .DistinctBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, StringComparer.OrdinalIgnoreCase);
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var carried = new HashSet<string>(available.Where(entry => Path.GetDirectoryName(entry.Value) == runtime).Select(entry => entry.Key), StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(available.Keys.Where(name => name is "DigitalBrain" or "DigitalBrain.Client"
            || name.EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase)));
        while (pending.TryPop(out var name))
        {
            if (!carried.Add(name)) { continue; }
            using var image = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(available[name]));
            var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(image);
            foreach (var handle in metadata.AssemblyReferences)
            {
                var dependency = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (available.ContainsKey(dependency) && !carried.Contains(dependency)) { pending.Push(dependency); }
            }
        }
        return [.. carried.Select(name => (MetadataReference)MetadataReference.CreateFromFile(available[name]))];
    }
}
