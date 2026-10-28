using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Modules.Microsoft.CSharp.Catalog;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.BuiltinApps;

public sealed record BuiltinPackage(string Name, string Path, string Digest, long Length);

// Packs one features/os app folder the way the source runtime would compile it: every .cs file except the
// declaration (app.cs) and the MSBuild-only folders is a program file, line endings normalized to LF; files
// without top-level statements are shared by every synapse. Each synapse's assembly is emitted by the one
// compiler (CSharpScriptCheck), PDBs dropped, and the package also carries the app's files so the app stays
// editable: .cs sources keep a ".src" suffix (the package format refuses .cs payloads), everything else as is.
public static class BuiltinPacker
{
    public const string Owner = "os";
    public const string SourceSuffix = ".src";

    private static readonly string[] BuildOnly = ["compiled/", "hosting/", "tests/", "bin/", "obj/"];

    public static async Task<IReadOnlyList<BuiltinPackage>> PackAll(string featuresOwnerFolder, string output, string? only, CancellationToken ct)
    {
        var root = new DirectoryInfo(featuresOwnerFolder);
        if (!root.Exists) { throw new InvalidDataException($"Features folder '{featuresOwnerFolder}' does not exist."); }
        Directory.CreateDirectory(output);
        var check = new CSharpScriptCheck();
        var built = new List<BuiltinPackage>();
        foreach (var folder in root.EnumerateDirectories().OrderBy(folder => folder.Name, StringComparer.Ordinal))
        {
            if (!File.Exists(System.IO.Path.Combine(folder.FullName, "app.cs"))) { continue; }
            var files = ReadFolder(folder);
            var declared = Declaration.Read(folder.Name, files["app.cs"]);
            if (only is not null && !declared.Name.Equals(only, StringComparison.Ordinal)) { continue; }
            var path = System.IO.Path.Combine(output, declared.Name + ".intochat-app");
            var (digest, length) = await Pack(check, declared, files, path, ct).ConfigureAwait(false);
            built.Add(new(declared.Name, path, digest, length));
        }
        if (built.Count == 0) { throw new InvalidDataException("No built-in apps were packed."); }
        return built;
    }

    // Every file of the folder as the feature loader reads it, minus what MSBuild owns.
    public static SortedDictionary<string, string> ReadFolder(DirectoryInfo folder)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in folder.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(folder.FullName, file.FullName).Replace('\\', '/');
            if (BuildOnly.Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) { continue; }
            files[relative] = File.ReadAllText(file.FullName).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        return files;
    }

    // The files the runtime deploys: program files, not the declaration.
    public static Dictionary<string, string> Programs(IReadOnlyDictionary<string, string> files)
        => files.Where(file => file.Key.EndsWith(".cs", StringComparison.Ordinal) && file.Key != "app.cs")
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.Ordinal);

    public static string AssemblyPath(string program) => "assemblies/" + program[..^".cs".Length] + ".dll";

    public static string FilePath(string relative) => "files/" + (relative.EndsWith(".cs", StringComparison.Ordinal) ? relative + SourceSuffix : relative);

    public static async Task<(string Digest, long Length)> Pack(CSharpScriptCheck check, Declaration declared, IReadOnlyDictionary<string, string> files, string target, CancellationToken ct)
    {
        var programs = Programs(files);
        var compiled = check.Compile(programs);
        if (!compiled.Success)
        {
            throw new InvalidDataException($"{declared.Name} does not compile: " + string.Join("; ", compiled.Errors.Select(error => $"{error.File}:{error.Line} {error.Id} {error.Message}")));
        }
        var staging = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "intochat-builtin-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (program, behavior) in compiled.Assemblies) { Write(staging, AssemblyPath(program), behavior.Assembly); }
            foreach (var (relative, content) in files) { Write(staging, FilePath(relative), new UTF8Encoding(false).GetBytes(content)); }
            var synapses = compiled.Assemblies.Keys.Order(StringComparer.Ordinal).ToArray();
            var entry = synapses.Contains("open.cs") ? "open.cs" : synapses[0];
            var references = CSharpScriptCheck.ReferenceNames().Order(StringComparer.Ordinal).ToArray();
            var descriptor = new CompiledAppDescriptor
            {
                Id = Owner + "/" + declared.Name,
                Name = declared.Title,
                Version = declared.Version,
                EntryAssembly = AssemblyPath(entry),
                EntryType = "Program",
                Presentation = ImmutableSortedDictionary<string, string>.Empty
                    .Add("title", declared.Title).Add("description", declared.Description).Add("icon", declared.Icon is null ? "" : FilePath(declared.Icon)),
                HostMetadata = ImmutableSortedDictionary<string, string>.Empty
                    .Add("kind", "program")
                    .Add("owner", Owner)
                    .Add("synapses", JsonSerializer.Serialize(synapses.Select(program => new { source = FilePath(program), assembly = AssemblyPath(program) })))
                    .Add("sharedSources", JsonSerializer.Serialize(programs.Keys.Except(synapses, StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(FilePath)))
                    .Add("compiler", "CSharpScriptCheck/preview/deterministic")
                    .Add("references", JsonSerializer.Serialize(references))
                    .Add("referenceDigest", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', references))))),
            };
            await using var stream = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var digest = await new AppPackageWriter().Write(descriptor, staging, stream, ct).ConfigureAwait(false);
            return (digest, stream.Length);
        }
        finally { if (Directory.Exists(staging)) { Directory.Delete(staging, true); } }
    }

    private static void Write(string root, string relative, byte[] bytes)
    {
        var path = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
}

public sealed record Declaration(string Name, string Version, string Title, string Description, string? Icon)
{
    // app.cs declares `static class App` with Name, Version and a Manifest of named arguments; only literals are read.
    public static Declaration Read(string folder, string source)
    {
        var root = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();
        var app = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(type => type.Identifier.ValueText == "App")
            ?? throw new InvalidDataException($"{folder}/app.cs declares no `static class App`.");
        string Constant(string name) => app.Members.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables)
            .FirstOrDefault(variable => variable.Identifier.ValueText == name)?.Initializer?.Value is LiteralExpressionSyntax literal
            ? literal.Token.ValueText : throw new InvalidDataException($"{folder}/app.cs declares no literal `App.{name}`.");
        var manifest = app.Members.OfType<FieldDeclarationSyntax>().SelectMany(field => field.Declaration.Variables)
            .FirstOrDefault(variable => variable.Initializer is not null && variable.Identifier.ValueText == "Manifest")?.Initializer?.Value as BaseObjectCreationExpressionSyntax
            ?? throw new InvalidDataException($"{folder}/app.cs declares no `App.Manifest`.");
        string? Argument(string name) => manifest.ArgumentList?.Arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == name)?.Expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText : null;
        return new(Constant("Name"), Constant("Version"), Argument("Title") ?? throw new InvalidDataException($"{folder}/app.cs manifest has no Title."), Argument("Description") ?? "", Argument("Icon"));
    }
}
