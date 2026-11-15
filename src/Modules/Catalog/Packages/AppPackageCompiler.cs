using System.Reflection;
using IntoChat.Sdk;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Apps;

/// <summary>Result of validating and compiling an <see cref="AppPackage"/> into a sandbox-ready PE assembly.</summary>
public sealed record CompiledAppPackage(
    bool Succeeded,
    byte[] AssemblyBytes,
    AppPackage? EnrichedPackage,
    string[] Errors);

/// <summary>
/// Compiles idiomatic C# <see cref="BrainApp{TState, TSettings}"/> packages via Roslyn, enforces
/// static sandbox safety bans, and extracts attribute metadata directly from compiled symbols.
/// </summary>
public sealed class AppPackageCompiler
{
    private static readonly HashSet<string> BannedTypePrefixes =
    [
        "System.IO.File",
        "System.IO.Directory",
        "System.IO.FileInfo",
        "System.IO.DirectoryInfo",
        "System.IO.FileStream",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.FileSystemWatcher",
        "System.Diagnostics.Process",
        "System.Net.Http.HttpClient",
        "System.Net.Sockets",
        "System.Runtime.Loader.AssemblyLoadContext",
        "System.Runtime.InteropServices.DllImportAttribute",
        "System.Runtime.InteropServices.LibraryImportAttribute",
    ];

    private const string GlobalUsingsPath = "AppGlobalUsings.g.cs";

    private const string GlobalUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using DigitalBrain.Rules;
        global using DigitalBrain.UI;
        global using IntoChat.OS.UI;
        global using IntoChat.Sdk;
        """;

    private static readonly Lazy<MetadataReference[]> DefaultReferences = new(BuildReferences);

    public CompiledAppPackage Compile(AppPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.SourceFiles.Length == 0)
        {
            return new CompiledAppPackage(false, [], null, ["Package contains no C# source files."]);
        }

        var parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
        var trees = package.SourceFiles
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .Select(file => CSharpSyntaxTree.ParseText(file.Code, parseOptions, path: file.Path))
            .Append(CSharpSyntaxTree.ParseText(GlobalUsingsSource, parseOptions, path: GlobalUsingsPath))
            .ToArray();

        var syntaxErrors = new List<string>();
        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            if (root.DescendantNodes().OfType<UnsafeStatementSyntax>().Any()
                || root.DescendantTokens().Any(token => token.IsKind(SyntaxKind.UnsafeKeyword)))
            {
                syntaxErrors.Add($"{tree.FilePath}: unsafe code is forbidden in sandboxed apps.");
            }
        }

        if (syntaxErrors.Count > 0)
        {
            return new CompiledAppPackage(false, [], null, [.. syntaxErrors]);
        }

        var assemblyName = $"AppPkg_{SanitizeAssemblyName(package.Key)}_{package.Version.Replace('.', '_')}";
        var compilationOptions = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            deterministic: true,
            optimizationLevel: OptimizationLevel.Release,
            allowUnsafe: false);

        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            DefaultReferences.Value,
            compilationOptions);

        var semanticErrors = ValidateBannedApis(compilation);
        if (semanticErrors.Count > 0)
        {
            return new CompiledAppPackage(false, [], null, [.. semanticErrors]);
        }

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);
        if (!emit.Success)
        {
            var diagnostics = emit.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString())
                .ToArray();
            return new CompiledAppPackage(false, [], null, diagnostics);
        }

        var extracted = ExtractNeeds(compilation, package);
        if (extracted.Error is { } metaError)
        {
            return new CompiledAppPackage(false, [], null, [metaError]);
        }

        var digest = AppPackage.ComputeDigest(
            extracted.Package!.Key,
            extracted.Package.Version,
            extracted.Package.SourceFiles,
            extracted.Package.DefaultSettingsJson);

        var finalPackage = extracted.Package with { Digest = digest };
        return new CompiledAppPackage(true, peStream.ToArray(), finalPackage, []);
    }

    private static List<string> ValidateBannedApis(CSharpCompilation compilation)
    {
        var errors = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();
            foreach (var node in root.DescendantNodes())
            {
                ISymbol? symbol = node switch
                {
                    InvocationExpressionSyntax invocation => model.GetSymbolInfo(invocation).Symbol,
                    ObjectCreationExpressionSyntax creation => model.GetSymbolInfo(creation).Symbol,
                    MemberAccessExpressionSyntax access => model.GetSymbolInfo(access).Symbol,
                    _ => null,
                };

                if (symbol is null)
                {
                    continue;
                }

                var containingType = symbol.ContainingType?.ToDisplayString();
                if (string.IsNullOrWhiteSpace(containingType))
                {
                    continue;
                }

                if (BannedTypePrefixes.Any(prefix => containingType.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    errors.Add($"{tree.FilePath}: '{containingType}' is restricted in sandboxed apps. Use IntoChat.Sdk capabilities (Brain.Folder / Brain.Notifications / Brain.Ai / Brain.Tables) instead.");
                    continue;
                }

                if (containingType == "System.Environment" && symbol.Name is "GetEnvironmentVariable" or "GetEnvironmentVariables" or "Exit")
                {
                    errors.Add($"{tree.FilePath}: 'System.Environment.{symbol.Name}' is restricted in sandboxed apps.");
                    continue;
                }

                if (containingType == "System.Reflection.Assembly" && symbol.Name.StartsWith("Load", StringComparison.Ordinal))
                {
                    errors.Add($"{tree.FilePath}: 'System.Reflection.Assembly.{symbol.Name}' is restricted in sandboxed apps.");
                }
            }
        }

        return errors.Distinct(StringComparer.Ordinal).ToList();
    }

    private static (AppPackage? Package, string? Error) ExtractNeeds(CSharpCompilation compilation, AppPackage original)
    {
        var candidates = AllTypes(compilation.Assembly.GlobalNamespace)
            .Where(type => type.TypeKind == TypeKind.Class && !type.IsAbstract)
            .Select(type => (Type: type, AppAttr: FindAttribute(type, typeof(AppAttribute).FullName!)))
            .Where(pair => pair.AppAttr is not null)
            .ToArray();

        if (candidates.Length == 0)
        {
            return (null, "App package must declare exactly one concrete class annotated with [IntoChat.Sdk.App(...)].");
        }

        if (candidates.Length > 1)
        {
            return (null, "App package must declare a single [App] entry class.");
        }

        var (appType, appAttr) = candidates[0];
        if (!InheritsBrainApp(appType))
        {
            return (null, $"[App] class '{appType.Name}' must inherit from IntoChat.Sdk.BrainApp<TState, TSettings>.");
        }

        var attrKey = appAttr!.ConstructorArguments.FirstOrDefault().Value as string;
        var key = string.IsNullOrWhiteSpace(attrKey) ? original.Key : attrKey!;
        var title = NamedArg<string>(appAttr, nameof(AppAttribute.Title)) ?? (string.IsNullOrWhiteSpace(original.Title) ? key : original.Title);
        var summary = NamedArg<string>(appAttr, nameof(AppAttribute.Summary)) ?? original.Summary;
        var version = NamedArg<string>(appAttr, nameof(AppAttribute.Version)) ?? original.Version;
        var icon = NamedArg<string>(appAttr, nameof(AppAttribute.Icon)) ?? original.Icon;
        var category = NamedArg<string>(appAttr, nameof(AppAttribute.Category)) ?? original.Category;
        var author = NamedArg<string>(appAttr, nameof(AppAttribute.Author)) ?? original.Author;

        var slots = new List<AppSlotSpec>();
        foreach (var attr in appType.GetAttributes().Where(a => a.AttributeClass?.ToDisplayString() == typeof(SlotAttribute).FullName))
        {
            var slotName = attr.ConstructorArguments.Length > 0 ? attr.ConstructorArguments[0].Value as string : null;
            var contractType = attr.ConstructorArguments.Length > 1 ? attr.ConstructorArguments[1].Value as ITypeSymbol : null;
            if (string.IsNullOrWhiteSpace(slotName) || contractType is null)
            {
                continue;
            }

            var required = NamedArg<bool?>(attr, nameof(SlotAttribute.Required)) ?? true;
            var description = NamedArg<string>(attr, nameof(SlotAttribute.Description)) ?? "";
            var accessRaw = NamedArg<int?>(attr, nameof(SlotAttribute.Access)) ?? 0;
            var defaultPath = NamedArg<string>(attr, nameof(SlotAttribute.DefaultPath)) ?? "";

            slots.Add(new AppSlotSpec(
                slotName,
                contractType.ToDisplayString(),
                required,
                description,
                (FolderAccess)accessRaw,
                defaultPath));
        }

        var signals = new SortedSet<string>(StringComparer.Ordinal);
        var schedules = new SortedSet<string>(StringComparer.Ordinal);
        var commands = new List<AppCommandSpec>();

        foreach (var method in appType.GetMembers().OfType<IMethodSymbol>().Where(m => m.MethodKind == MethodKind.Ordinary))
        {
            foreach (var attr in method.GetAttributes())
            {
                var fullName = attr.AttributeClass?.ToDisplayString();
                if (fullName == typeof(OnSignalAttribute).FullName && attr.ConstructorArguments.FirstOrDefault().Value is string sig && !string.IsNullOrWhiteSpace(sig))
                {
                    signals.Add(sig);
                }
                else if (fullName == typeof(EveryAttribute).FullName && attr.ConstructorArguments.FirstOrDefault().Value is string cron && !string.IsNullOrWhiteSpace(cron))
                {
                    schedules.Add(cron);
                }
                else if (fullName == typeof(OnCommandAttribute).FullName && attr.ConstructorArguments.FirstOrDefault().Value is string cmd && !string.IsNullOrWhiteSpace(cmd))
                {
                    var desc = NamedArg<string>(attr, nameof(OnCommandAttribute.Description)) ?? "";
                    commands.Add(new AppCommandSpec(cmd, desc));
                }
            }
        }

        var needs = new AppPackageNeeds([.. slots], [.. signals], [.. schedules], [.. commands]);
        return (original with
        {
            Key = key,
            Version = version,
            Title = title,
            Summary = summary,
            Icon = icon,
            Category = category,
            Author = author,
            Needs = needs,
        }, null);
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(INamespaceSymbol ns)
    {
        foreach (var member in ns.GetTypeMembers())
        {
            yield return member;
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var nested in AllTypes(child))
            {
                yield return nested;
            }
        }
    }

    private static AttributeData? FindAttribute(ISymbol symbol, string fullName)
        => symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == fullName);

    private static T? NamedArg<T>(AttributeData attr, string name)
    {
        foreach (var pair in attr.NamedArguments)
        {
            if (string.Equals(pair.Key, name, StringComparison.Ordinal) && pair.Value.Value is T typed)
            {
                return typed;
            }
        }

        return default;
    }

    private static bool InheritsBrainApp(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.OriginalDefinition.ToDisplayString() == "IntoChat.Sdk.BrainApp<TState, TSettings>")
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeAssemblyName(string key)
        => new([.. key.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')]);

    private static MetadataReference[] BuildReferences()
    {
        var trustedAssemblies = new List<Assembly>
        {
            typeof(object).Assembly,
            typeof(Task).Assembly,
            typeof(Enumerable).Assembly,
            typeof(System.Linq.Expressions.Expression).Assembly,
            typeof(System.Text.Json.JsonSerializer).Assembly,
            typeof(System.Text.RegularExpressions.Regex).Assembly,
            typeof(IntoChat.Sdk.IBrain).Assembly,
            typeof(IntoChat.OS.UI.Kit).Assembly,
            typeof(DigitalBrain.Signal).Assembly,
            typeof(DigitalBrain.UI.View).Assembly,
        };

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name is "System.Runtime" or "System.Collections" or "System.Linq" or "System.Linq.Expressions"
                or "System.Console" or "System.Text.Json" or "System.Text.RegularExpressions"
                or "System.ComponentModel" or "netstandard")
            {
                trustedAssemblies.Add(assembly);
            }
        }

        return [.. trustedAssemblies
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .DistinctBy(a => a.Location, StringComparer.OrdinalIgnoreCase)
            .Select(a => MetadataReference.CreateFromFile(a.Location))];
    }
}
