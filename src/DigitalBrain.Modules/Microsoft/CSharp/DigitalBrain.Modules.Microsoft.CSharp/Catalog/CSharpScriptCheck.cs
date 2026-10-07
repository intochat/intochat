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

// The single compiler: compiles each behavior in-process against this host's loaded assemblies and,
// when every file is clean, emits one assembly per file named by its content hash. The sandbox only
// loads what this emits. The implicit console usings plus the script client's namespaces are imported.
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

    public ScriptCompilation Compile(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) { throw new ArgumentException("Pass at least one file to compile.", nameof(files)); }
        var errors = new List<ScriptDiagnostic>();
        var assemblies = new Dictionary<string, CompiledBehavior>(StringComparer.Ordinal);
        foreach (var (path, source) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            ArgumentNullException.ThrowIfNull(source, path);
            var trees = new[]
            {
                CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), Parse, path),
                CSharpSyntaxTree.ParseText(SourceText.From(GlobalUsings, Encoding.UTF8), Parse, "GlobalUsings.cs"),
            };
            var compilation = CSharpCompilation.Create("behavior", trees, References.Value, Options);
            using var assembly = new MemoryStream();
            using var symbols = new MemoryStream();
            var emitted = compilation.Emit(assembly, symbols, options: Emit);
            errors.AddRange(emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => new ScriptDiagnostic(path, diagnostic.Id,
                    diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1, diagnostic.GetMessage())));
            if (emitted.Success)
            {
                var bytes = assembly.ToArray();
                assemblies[path] = new(bytes, symbols.ToArray(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
            }
        }
        return new(errors.Count == 0, errors, errors.Count == 0 ? assemblies : new Dictionary<string, CompiledBehavior>());
    }

    private static MetadataReference[] LoadReferences()
        => [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(File.Exists)
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))];
}
