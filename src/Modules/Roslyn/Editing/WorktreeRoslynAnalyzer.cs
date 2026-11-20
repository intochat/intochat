using System.Security;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

internal sealed class WorktreeRoslynAnalyzer : IWorktreeRoslyn
{
    private const int MaxAnalyzedFiles = 500;
    private const int MaxDiagnostics = 100;
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    private static readonly Lazy<MetadataReference[]> RuntimeReferences = new(LoadRuntimeReferences);

    public async Task<IReadOnlyList<WorktreeSymbolInfo>> InspectSymbolsAsync(
        string worktreeRoot,
        string? relativeFilter = null,
        CancellationToken ct = default)
    {
        var normalizedRoot = NormalizeRoot(worktreeRoot);
        var files = EnumerateConfinedCSharpFiles(normalizedRoot, relativeFilter);
        var symbols = new List<WorktreeSymbolInfo>();

        foreach (var (relPath, fullPath) in files)
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: relPath, cancellationToken: ct);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

            foreach (var decl in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
            {
                var ns = decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? string.Empty;
                var kind = decl switch
                {
                    ClassDeclarationSyntax => "class",
                    RecordDeclarationSyntax r when r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
                    RecordDeclarationSyntax => "record",
                    InterfaceDeclarationSyntax => "interface",
                    StructDeclarationSyntax => "struct",
                    EnumDeclarationSyntax => "enum",
                    _ => "type",
                };
                var line = decl.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var members = ExtractMemberNames(decl);
                symbols.Add(new WorktreeSymbolInfo(decl.Identifier.Text, ns, kind, relPath, line, members));
            }
        }

        return [.. symbols.OrderBy(static s => s.FilePath, StringComparer.Ordinal).ThenBy(static s => s.Line)];
    }

    public async Task<IReadOnlyList<WorktreeDiagnostic>> AnalyzeAsync(
        string worktreeRoot,
        IReadOnlyList<string>? relativePaths = null,
        CancellationToken ct = default)
    {
        var normalizedRoot = NormalizeRoot(worktreeRoot);
        List<(string RelPath, string FullPath)> targetFiles;

        if (relativePaths is { Count: > 0 })
        {
            targetFiles = [];
            foreach (var raw in relativePaths)
            {
                var resolved = ResolveConfinedCSharpPath(normalizedRoot, raw);
                if (File.Exists(resolved.FullPath))
                {
                    targetFiles.Add(resolved);
                }
            }
        }
        else
        {
            targetFiles = EnumerateConfinedCSharpFiles(normalizedRoot, filter: null);
        }

        if (targetFiles.Count == 0)
        {
            return [];
        }

        // Include sibling C# files in the same directories/worktree (up to MaxAnalyzedFiles) so cross-file references resolve cleanly.
        var allFiles = EnumerateConfinedCSharpFiles(normalizedRoot, filter: null);
        var targetRelSet = targetFiles.Select(static f => f.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var trees = new List<(string RelPath, SyntaxTree Tree)>(allFiles.Count);
        foreach (var (rel, full) in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            var text = await File.ReadAllTextAsync(full, Encoding.UTF8, ct).ConfigureAwait(false);
            var tree = CSharpSyntaxTree.ParseText(text, ParseOptions, path: rel, cancellationToken: ct);
            trees.Add((rel, tree));
        }

        var hasExternalProjectRefs = Directory.EnumerateFiles(normalizedRoot, "*.csproj", SearchOption.AllDirectories)
            .Any(p =>
            {
                var content = File.ReadAllText(p);
                return content.Contains("PackageReference", StringComparison.Ordinal)
                    || content.Contains("ProjectReference", StringComparison.Ordinal);
            });

        var diagnostics = new List<WorktreeDiagnostic>();
        if (!hasExternalProjectRefs)
        {
            using var adhoc = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId(debugName: "WorktreeAnalysis");
            var projectInfo = ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    "WorktreeAnalysis",
                    "WorktreeAnalysis",
                    LanguageNames.CSharp)
                .WithParseOptions(ParseOptions)
                .WithCompilationOptions(new CSharpCompilationOptions(
                    OutputKind.DynamicallyLinkedLibrary,
                    nullableContextOptions: NullableContextOptions.Enable))
                .WithMetadataReferences(RuntimeReferences.Value);

            var project = adhoc.AddProject(projectInfo);
            var docIdsByRel = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rel, tree) in trees)
            {
                var sourceText = await tree.GetTextAsync(ct).ConfigureAwait(false);
                var doc = project.AddDocument(rel, sourceText, filePath: rel);
                project = doc.Project;
                docIdsByRel[rel] = doc.Id;
            }

            foreach (var (rel, _) in trees)
            {
                if (!targetRelSet.Contains(rel) || !docIdsByRel.TryGetValue(rel, out var docId))
                {
                    continue;
                }

                var doc = project.GetDocument(docId);
                var model = doc is not null
                    ? await doc.GetSemanticModelAsync(ct).ConfigureAwait(false)
                    : null;
                if (model is null)
                {
                    continue;
                }

                foreach (var diag in model.GetDiagnostics(cancellationToken: ct))
                {
                    if (diag.Severity < DiagnosticSeverity.Warning)
                    {
                        continue;
                    }

                    var line = diag.Location.GetLineSpan().StartLinePosition.Line + 1;
                    diagnostics.Add(new WorktreeDiagnostic(
                        diag.Id,
                        diag.Severity.ToString(),
                        diag.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                        rel,
                        line));
                    if (diagnostics.Count >= MaxDiagnostics)
                    {
                        return diagnostics;
                    }
                }
            }

            return diagnostics;
        }

        // When external NuGet / multi-project references exist on disk, run exact C# syntax + duplicate-member checks
        // without false-positive missing-assembly CS0246 errors (full MSBuild runs in the build-and-test stage).
        foreach (var (rel, tree) in trees)
        {
            if (!targetRelSet.Contains(rel))
            {
                continue;
            }

            foreach (var diag in tree.GetDiagnostics(ct))
            {
                if (diag.Severity < DiagnosticSeverity.Warning)
                {
                    continue;
                }

                var line = diag.Location.GetLineSpan().StartLinePosition.Line + 1;
                diagnostics.Add(new WorktreeDiagnostic(
                    diag.Id,
                    diag.Severity.ToString(),
                    diag.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                    rel,
                    line));
                if (diagnostics.Count >= MaxDiagnostics)
                {
                    return diagnostics;
                }
            }
        }

        return diagnostics;
    }

    public async Task<WorktreeEditSummary> ApplyEditsAsync(
        string worktreeRoot,
        IReadOnlyList<WorktreeCSharpEdit> edits,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var normalizedRoot = NormalizeRoot(worktreeRoot);
        if (edits.Count == 0)
        {
            return new WorktreeEditSummary(true, [], [], string.Empty, "No edits requested.");
        }

        var stagedContent = new Dictionary<string, (string FullPath, string Original, string Updated)>(StringComparer.Ordinal);

        for (var i = 0; i < edits.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var edit = edits[i];
            var (relPath, fullPath) = ResolveConfinedCSharpPath(normalizedRoot, edit.RelativePath);

            string beforeEdit;
            string originalOnDisk;
            if (stagedContent.TryGetValue(relPath, out var existingStaged))
            {
                originalOnDisk = existingStaged.Original;
                beforeEdit = existingStaged.Updated;
            }
            else
            {
                originalOnDisk = File.Exists(fullPath)
                    ? await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false)
                    : string.Empty;
                beforeEdit = originalOnDisk;
            }

            try
            {
                var updated = ApplySingleEdit(edit, relPath, beforeEdit, fileExists: File.Exists(fullPath) || stagedContent.ContainsKey(relPath));
                var syntaxCheck = CSharpSyntaxTree.ParseText(updated, ParseOptions, path: relPath, cancellationToken: ct);
                var syntaxErrors = syntaxCheck.GetDiagnostics(ct)
                    .Where(static d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => new WorktreeDiagnostic(
                        d.Id,
                        d.Severity.ToString(),
                        d.GetMessage(System.Globalization.CultureInfo.InvariantCulture),
                        relPath,
                        d.Location.GetLineSpan().StartLinePosition.Line + 1))
                    .ToArray();

                if (syntaxErrors.Length > 0)
                {
                    return new WorktreeEditSummary(
                        false,
                        [],
                        syntaxErrors,
                        string.Empty,
                        $"Edit {i + 1} ({edit.Kind} on '{relPath}') produced C# syntax error {syntaxErrors[0].Id} at line {syntaxErrors[0].Line}: {syntaxErrors[0].Message}");
                }

                stagedContent[relPath] = (fullPath, originalOnDisk, updated);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return new WorktreeEditSummary(false, [], [], string.Empty, $"Edit {i + 1} ({edit.Kind} on '{relPath}') failed: {ex.Message}");
            }
        }

        var utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var touched = new List<string>(stagedContent.Count);
        var diffBuilder = new StringBuilder();

        foreach (var (relPath, entry) in stagedContent.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            if (string.Equals(entry.Original, entry.Updated, StringComparison.Ordinal))
            {
                continue;
            }

            var dir = Path.GetDirectoryName(entry.FullPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await File.WriteAllTextAsync(entry.FullPath, entry.Updated, utf8NoBom, ct).ConfigureAwait(false);
            touched.Add(relPath);
            diffBuilder.Append(LineDiff.Render(relPath, entry.Original, entry.Updated));
        }

        var postDiagnostics = touched.Count > 0
            ? await AnalyzeAsync(normalizedRoot, touched, ct).ConfigureAwait(false)
            : [];
        var hasErrors = postDiagnostics.Any(static d => d.Severity == nameof(DiagnosticSeverity.Error));

        return new WorktreeEditSummary(
            Succeeded: !hasErrors,
            TouchedFiles: [.. touched],
            Diagnostics: [.. postDiagnostics],
            Diff: diffBuilder.ToString(),
            Detail: hasErrors ? $"Post-edit semantic analysis reported {postDiagnostics.Count(static d => d.Severity == nameof(DiagnosticSeverity.Error))} error(s)." : null);
    }

    private static string ApplySingleEdit(WorktreeCSharpEdit edit, string relPath, string currentSource, bool fileExists)
    {
        switch (edit.Kind)
        {
            case WorktreeCSharpEditKind.CreateTypeFile:
                {
                    var nsName = RequireNonBlank(edit.NamespaceName, nameof(edit.NamespaceName));
                    var typeName = RequireNonBlank(edit.TypeName, nameof(edit.TypeName));
                    if (!SyntaxFacts.IsValidIdentifier(typeName))
                    {
                        throw new ArgumentException($"'{typeName}' is not a valid C# type identifier.");
                    }

                    var typeDecl = BuildTypeDeclaration(edit.TypeKind ?? "class", typeName, edit.BaseTypes, edit.Source);
                    var nsDecl = SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName(nsName))
                        .AddMembers(typeDecl);
                    var unit = SyntaxFactory.CompilationUnit().AddMembers(nsDecl);
                    if (!string.IsNullOrWhiteSpace(edit.MemberName))
                    {
                        foreach (var u in edit.MemberName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            unit = unit.AddUsings(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)));
                        }
                    }

                    return FormatCleanCSharp(unit);
                }

            case WorktreeCSharpEditKind.AddUsing:
                {
                    if (!fileExists)
                    {
                        throw new InvalidOperationException($"C# file '{relPath}' does not exist.");
                    }

                    var nsName = RequireNonBlank(edit.NamespaceName, nameof(edit.NamespaceName));
                    var root = ParseCompilationUnit(currentSource, relPath);
                    if (root.Usings.Any(u => string.Equals(u.Name?.ToString(), nsName, StringComparison.Ordinal)))
                    {
                        return currentSource;
                    }

                    var updatedUsings = root.Usings
                        .Append(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(nsName)))
                        .OrderBy(static u => u.Name?.ToString(), StringComparer.Ordinal)
                        .ToArray();
                    return FormatCleanCSharp(root.WithUsings(SyntaxFactory.List(updatedUsings)));
                }

            case WorktreeCSharpEditKind.AddOrReplaceMember:
                {
                    if (!fileExists)
                    {
                        throw new InvalidOperationException($"C# file '{relPath}' does not exist.");
                    }

                    var typeName = RequireNonBlank(edit.TypeName, nameof(edit.TypeName));
                    var memberSource = RequireNonBlank(edit.Source, nameof(edit.Source));
                    var root = ParseCompilationUnit(currentSource, relPath);
                    var targetType = root.DescendantNodes()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault(t => string.Equals(t.Identifier.Text, typeName, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"Type '{typeName}' was not found in '{relPath}'.");

                    var parsedMember = ParseMemberCleanly(memberSource);
                    var memberId = edit.MemberName ?? TryGetMemberIdentifier(parsedMember);
                    var existing = memberId is null
                        ? null
                        : targetType.Members.FirstOrDefault(m => string.Equals(TryGetMemberIdentifier(m), memberId, StringComparison.Ordinal));

                    if (targetType is RecordDeclarationSyntax rec && rec.OpenBraceToken.IsKind(SyntaxKind.None))
                    {
                        var opened = rec
                            .WithSemicolonToken(default)
                            .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                            .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken))
                            .AddMembers(parsedMember);
                        return FormatCleanCSharp(root.ReplaceNode(targetType, opened));
                    }

                    var nextType = existing is not null
                        ? targetType.ReplaceNode(existing, parsedMember)
                        : targetType.AddMembers(parsedMember);
                    return FormatCleanCSharp(root.ReplaceNode(targetType, nextType));
                }

            case WorktreeCSharpEditKind.RemoveMember:
                {
                    if (!fileExists)
                    {
                        throw new InvalidOperationException($"C# file '{relPath}' does not exist.");
                    }

                    var typeName = RequireNonBlank(edit.TypeName, nameof(edit.TypeName));
                    var memberName = RequireNonBlank(edit.MemberName, nameof(edit.MemberName));
                    var root = ParseCompilationUnit(currentSource, relPath);
                    var targetType = root.DescendantNodes()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault(t => string.Equals(t.Identifier.Text, typeName, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"Type '{typeName}' was not found in '{relPath}'.");

                    var existing = targetType.Members.FirstOrDefault(m => string.Equals(TryGetMemberIdentifier(m), memberName, StringComparison.Ordinal))
                        ?? throw new InvalidOperationException($"Member '{memberName}' was not found on '{typeName}' in '{relPath}'.");

                    var nextType = targetType.RemoveNode(existing, SyntaxRemoveOptions.KeepNoTrivia)!;
                    return FormatCleanCSharp(root.ReplaceNode(targetType, nextType));
                }

            default:
                throw new InvalidOperationException($"Unsupported C# edit kind '{edit.Kind}'.");
        }
    }

    private static TypeDeclarationSyntax BuildTypeDeclaration(string typeKind, string typeName, string? baseTypes, string? bodyMembersSource)
    {
        var id = SyntaxFactory.Identifier(typeName);
        var pub = SyntaxFactory.Token(SyntaxKind.PublicKeyword);
        TypeDeclarationSyntax decl = typeKind.Trim().ToLowerInvariant() switch
        {
            "interface" => SyntaxFactory.InterfaceDeclaration(id).AddModifiers(pub),
            "record" => SyntaxFactory.RecordDeclaration(SyntaxFactory.Token(SyntaxKind.RecordKeyword), id)
                .AddModifiers(pub, SyntaxFactory.Token(SyntaxKind.SealedKeyword))
                .WithOpenBraceToken(SyntaxFactory.Token(SyntaxKind.OpenBraceToken))
                .WithCloseBraceToken(SyntaxFactory.Token(SyntaxKind.CloseBraceToken)),
            "struct" => SyntaxFactory.StructDeclaration(id).AddModifiers(pub, SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword)),
            _ => SyntaxFactory.ClassDeclaration(id).AddModifiers(pub, SyntaxFactory.Token(SyntaxKind.SealedKeyword)),
        };

        if (!string.IsNullOrWhiteSpace(baseTypes))
        {
            var list = SyntaxFactory.BaseList(
                SyntaxFactory.SeparatedList<BaseTypeSyntax>(
                    baseTypes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(static b => SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(b)))));
            decl = decl switch
            {
                ClassDeclarationSyntax c => c.WithBaseList(list),
                InterfaceDeclarationSyntax i => i.WithBaseList(list),
                RecordDeclarationSyntax r => r.WithBaseList(list),
                StructDeclarationSyntax s => s.WithBaseList(list),
                _ => decl,
            };
        }

        if (!string.IsNullOrWhiteSpace(bodyMembersSource))
        {
            var wrapped = CSharpSyntaxTree.ParseText($"class _Holder_ {{\n{bodyMembersSource}\n}}", ParseOptions)
                .GetCompilationUnitRoot();
            var holder = wrapped.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (holder is not null && holder.Members.Count > 0)
            {
                decl = decl.AddMembers([.. holder.Members]);
            }
        }

        return decl;
    }

    private static CompilationUnitSyntax ParseCompilationUnit(string source, string relPath)
        => CSharpSyntaxTree.ParseText(source, ParseOptions, path: relPath).GetCompilationUnitRoot();

    private static MemberDeclarationSyntax ParseMemberCleanly(string source)
    {
        var member = SyntaxFactory.ParseMemberDeclaration(source, 0, ParseOptions);
        if (member is null || member.ContainsDiagnostics)
        {
            throw new InvalidOperationException("Member source is not a syntactically valid C# member declaration.");
        }

        return member.WithAdditionalAnnotations(Formatter.Annotation);
    }

    private static string? TryGetMemberIdentifier(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        FieldDeclarationSyntax f => f.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventFieldDeclarationSyntax ef => ef.Declaration.Variables.FirstOrDefault()?.Identifier.Text,
        EventDeclarationSyntax e => e.Identifier.Text,
        BaseTypeDeclarationSyntax t => t.Identifier.Text,
        _ => null,
    };

    private static string[] ExtractMemberNames(BaseTypeDeclarationSyntax decl) => decl switch
    {
        TypeDeclarationSyntax td =>
        [
            .. td.Members
                .Select(TryGetMemberIdentifier)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ],
        EnumDeclarationSyntax ed => [.. ed.Members.Select(static m => m.Identifier.Text)],
        _ => [],
    };

    private static string FormatCleanCSharp(SyntaxNode node)
    {
        using var adhoc = new AdhocWorkspace();
#pragma warning disable RS0030
        var formatted = Formatter.Format(node.NormalizeWhitespace(), adhoc);
#pragma warning restore RS0030
        var raw = formatted.ToFullString().Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = raw.Split('\n').Select(static line => line.TrimEnd(' ', '\t'));
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }

    private static string NormalizeRoot(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var full = Path.GetFullPath(worktreeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"Worktree directory does not exist: {full}");
        }

        return full;
    }

    private static (string RelPath, string FullPath) ResolveConfinedCSharpPath(string normalizedRoot, string rawRelativePath)
    {
        if (string.IsNullOrWhiteSpace(rawRelativePath))
        {
            throw new SecurityException("C# relative path must not be blank.");
        }

        var clean = rawRelativePath.Trim().Replace('\\', '/');
        if (clean.StartsWith('/') || Path.IsPathRooted(clean))
        {
            throw new SecurityException($"Path gate rejected rooted C# path '{rawRelativePath}'.");
        }

        var segments = clean.Split('/');
        if (segments.Any(static s => s.Length == 0 || s.StartsWith('.')))
        {
            throw new SecurityException($"Path gate rejected dot-segment or traversal path '{rawRelativePath}'.");
        }

        if (!clean.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"WorktreeRoslynAnalyzer only accepts '.cs' files; got '{rawRelativePath}'.");
        }

        var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, clean.Replace('/', Path.DirectorySeparatorChar)));
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(rootPrefix, cmp))
        {
            throw new SecurityException($"Path gate rejected C# path escaping worktree root: '{rawRelativePath}'.");
        }

        return (clean, fullPath);
    }

    private static List<(string RelPath, string FullPath)> EnumerateConfinedCSharpFiles(string normalizedRoot, string? filter)
    {
        var results = new List<(string RelPath, string FullPath)>();
        var pending = new Stack<string>([normalizedRoot]);

        while (pending.Count > 0 && results.Count < MaxAnalyzedFiles)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !CodeGraphService.SkippedDirectories.Contains(name))
                {
                    pending.Push(sub);
                }
            }

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
            {
                var rel = Path.GetRelativePath(normalizedRoot, file).Replace('\\', '/');
                if (!string.IsNullOrWhiteSpace(filter) && !rel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add((rel, file));
                if (results.Count >= MaxAnalyzedFiles)
                {
                    break;
                }
            }
        }

        results.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.RelPath, b.RelPath));
        return results;
    }

    private static string RequireNonBlank(string? value, string paramName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{paramName} is required.", paramName)
            : value.Trim();

    private static MetadataReference[] LoadRuntimeReferences()
    {
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrWhiteSpace(tpa))
        {
            return [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        }

        var wantedPrefixes = new[]
        {
            "System.Private.CoreLib",
            "System.Runtime",
            "System.Collections",
            "System.Linq",
            "System.Console",
            "System.Text.",
            "System.IO",
            "System.Threading",
            "System.Net.",
            "netstandard",
        };

        var refs = new List<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fileName = Path.GetFileNameWithoutExtension(path);
            if (wantedPrefixes.Any(prefix => fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                && File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }

        return refs.Count == 0
            ? [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]
            : [.. refs];
    }
}
