using System.Collections.Concurrent;
using System.Collections.Frozen;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Modules.Microsoft.Roslyn;

internal sealed class CodeGraphService(IOptions<RoslynModuleOptions> options)
{
    private const int MaxCachedRepositories = 8;
    private const int MaxIndexedFiles = 4_000;
    private const long MaxFileSizeBytes = 1_024 * 1_024;

    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    private static readonly FrozenSet<string> SkippedDirectories = new[]
    {
        "bin",
        "obj",
        ".git",
        "node_modules",
        "artifacts",
        ".vs",
        ".idea",
        ".dart_tool",
        "build",
        "TestResults",
        "packages",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> SensitiveSegments = new[]
    {
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        ".kube",
        "vault",
        "secrets",
        "System32",
        "etc",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly object _cacheGate = new();
    private readonly List<(string Root, RepositoryIndex Index)> _indexes = new(MaxCachedRepositories);

    public Task<CodeSymbolSearchResult> SearchSymbolsAsync(
        string repositoryPathOrId,
        string query,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (repoRoot, repoDisplay) = ResolveRepository(repositoryPathOrId);
        var boundedLimit = Math.Clamp(limit, 1, 200);
        var snapshot = GetOrRefreshIndex(repoRoot, cancellationToken);
        var trimmedQuery = query?.Trim() ?? string.Empty;

        if (trimmedQuery.Length == 0)
        {
            var initial = snapshot.AllSymbols.Take(boundedLimit).ToArray();
            return Task.FromResult(new CodeSymbolSearchResult(
                repoDisplay,
                initial,
                snapshot.AllSymbols.Length,
                snapshot.AllSymbols.Length > boundedLimit));
        }

        var scored = new List<(int Score, CodeSymbolNode Symbol)>();
        foreach (var symbol in snapshot.AllSymbols)
        {
            var score = ScoreSymbol(symbol, trimmedQuery);
            if (score > 0)
            {
                scored.Add((score, symbol));
            }
        }

        scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) { return byScore; }
            var byPath = string.Compare(a.Symbol.RelativePath, b.Symbol.RelativePath, StringComparison.Ordinal);
            return byPath != 0 ? byPath : a.Symbol.Line.CompareTo(b.Symbol.Line);
        });

        var page = scored.Take(boundedLimit).Select(static entry => entry.Symbol).ToArray();
        return Task.FromResult(new CodeSymbolSearchResult(
            repoDisplay,
            page,
            scored.Count,
            scored.Count > boundedLimit));
    }

    public async Task<CodeFileOutline> FileOutlineAsync(
        string repositoryPathOrId,
        string relativeFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (repoRoot, repoDisplay) = ResolveRepository(repositoryPathOrId);
        if (string.IsNullOrWhiteSpace(relativeFilePath))
        {
            throw new ArgumentException("Relative file path must not be empty.", nameof(relativeFilePath));
        }

        var trimmedRel = relativeFilePath.Trim();
        if (Path.IsPathRooted(trimmedRel))
        {
            throw new UnauthorizedAccessException("File outline requires a repository-relative file path.");
        }

        var combined = Path.GetFullPath(Path.Combine(repoRoot, trimmedRel));
        EnsureUnderRoot(combined, repoRoot);
        EnsureNotSensitive(combined, repoRoot);

        if (!combined.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("FileOutline supports C# (.cs) source files.", nameof(relativeFilePath));
        }

        if (!File.Exists(combined))
        {
            throw new FileNotFoundException($"Source file '{trimmedRel}' was not found in repository '{repoDisplay}'.", trimmedRel);
        }

        var normalizedRel = Path.GetRelativePath(repoRoot, combined).Replace('\\', '/');
        var source = await File.ReadAllTextAsync(combined, cancellationToken).ConfigureAwait(false);
        var parsed = ParseSourceFile(normalizedRel, source, cancellationToken);
        return new CodeFileOutline(repoDisplay, normalizedRel, parsed.Namespace, parsed.Outline);
    }

    public Task<CodeCallersResult> FindCallersAsync(
        string repositoryPathOrId,
        string symbolName,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (repoRoot, repoDisplay) = ResolveRepository(repositoryPathOrId);
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            throw new ArgumentException("Symbol name must not be empty.", nameof(symbolName));
        }

        var boundedLimit = Math.Clamp(limit, 1, 200);
        var snapshot = GetOrRefreshIndex(repoRoot, cancellationToken);

        var raw = symbolName.Trim();
        string? receiverFilter = null;
        var targetName = raw;
        var dotIndex = raw.LastIndexOf('.');
        if (dotIndex > 0 && dotIndex + 1 < raw.Length)
        {
            receiverFilter = raw[..dotIndex].Trim();
            targetName = raw[(dotIndex + 1)..].Trim();
        }

        // Handle interface-prefixed type names when looking up constructor callers or qualified calls.
        var strippedReceiver = receiverFilter is { Length: > 1 } r && r[0] == 'I' && char.IsUpper(r[1])
            ? r[1..]
            : receiverFilter;

        var matches = new List<(int Priority, CodeCallerNode Caller)>();
        foreach (var call in snapshot.AllCalls)
        {
            if (!string.Equals(call.CalleeName, targetName, StringComparison.Ordinal)
                && !string.Equals(call.CalleeName, targetName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var exactCase = string.Equals(call.CalleeName, targetName, StringComparison.Ordinal);
            if (receiverFilter is not null)
            {
                var exprHasReceiver = call.Node.CalleeExpression.Contains(receiverFilter, StringComparison.OrdinalIgnoreCase)
                    || (strippedReceiver is not null && call.Node.CalleeExpression.Contains(strippedReceiver, StringComparison.OrdinalIgnoreCase))
                    || call.Node.CallerSymbol.StartsWith(receiverFilter + ".", StringComparison.OrdinalIgnoreCase);
                matches.Add(((exactCase ? 10 : 5) + (exprHasReceiver ? 20 : 0), call.Node));
            }
            else
            {
                matches.Add((exactCase ? 10 : 5, call.Node));
            }
        }

        // If qualified "Type.Member" had hits with matching receiver context, prefer those when exact receiver matches exist.
        if (receiverFilter is not null && matches.Any(static m => m.Priority >= 25))
        {
            matches.RemoveAll(static m => m.Priority < 20);
        }

        matches.Sort(static (a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority);
            if (byPriority != 0) { return byPriority; }
            var byPath = string.Compare(a.Caller.RelativePath, b.Caller.RelativePath, StringComparison.Ordinal);
            return byPath != 0 ? byPath : a.Caller.Line.CompareTo(b.Caller.Line);
        });

        var page = matches.Take(boundedLimit).Select(static m => m.Caller).ToArray();
        return Task.FromResult(new CodeCallersResult(
            repoDisplay,
            raw,
            page,
            matches.Count,
            matches.Count > boundedLimit));
    }

    private (string RootPath, string DisplayName) ResolveRepository(string repositoryPathOrId)
    {
        if (string.IsNullOrWhiteSpace(repositoryPathOrId))
        {
            throw new ArgumentException("Repository path or identifier is required.", nameof(repositoryPathOrId));
        }

        var current = options.Value;
        var allowedRoots = GetAllowedRoots(current);
        var token = repositoryPathOrId.Trim();

        if (current.Repositories.TryGetValue(token, out var mappedPath) && !string.IsNullOrWhiteSpace(mappedPath))
        {
            var fullMapped = NormalizeDirectory(mappedPath);
            EnsureDirectoryExists(fullMapped, token);
            EnsureNotSensitive(fullMapped, fullMapped);
            return (fullMapped, token);
        }

        if (token is "." or "workspace" or "default" or "current")
        {
            var defaultRoot = allowedRoots[0];
            EnsureDirectoryExists(defaultRoot, token);
            return (defaultRoot, Path.GetFileName(defaultRoot));
        }

        // Match by directory basename among allowlisted roots (e.g. "os" or "w3-visuals-models").
        var byFolderName = allowedRoots.FirstOrDefault(root =>
            string.Equals(Path.GetFileName(root), token, StringComparison.OrdinalIgnoreCase));
        if (byFolderName is not null)
        {
            EnsureDirectoryExists(byFolderName, token);
            return (byFolderName, Path.GetFileName(byFolderName));
        }

        // Candidate directory path (absolute or relative to the primary allowlisted root).
        var candidate = Path.IsPathRooted(token)
            ? NormalizeDirectory(token)
            : NormalizeDirectory(Path.Combine(allowedRoots[0], token));

        if (!allowedRoots.Any(allowed => IsSameOrUnder(candidate, allowed)))
        {
            throw new UnauthorizedAccessException(
                $"Repository '{repositoryPathOrId}' is outside the allowlisted workspace roots.");
        }

        var matchedRoot = allowedRoots.First(allowed => IsSameOrUnder(candidate, allowed));
        EnsureNotSensitive(candidate, matchedRoot);
        EnsureDirectoryExists(candidate, repositoryPathOrId);
        return (candidate, Path.GetFileName(candidate));
    }

    private static string[] GetAllowedRoots(RoslynModuleOptions current)
    {
        var roots = new List<string>();
        foreach (var configured in current.AllowedRoots)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                roots.Add(NormalizeDirectory(configured));
            }
        }

        foreach (var repoPath in current.Repositories.Values)
        {
            if (!string.IsNullOrWhiteSpace(repoPath))
            {
                roots.Add(NormalizeDirectory(repoPath));
            }
        }

        if (!string.IsNullOrWhiteSpace(current.SolutionPath))
        {
            var slnDir = Path.GetDirectoryName(Path.GetFullPath(current.SolutionPath));
            if (!string.IsNullOrWhiteSpace(slnDir))
            {
                roots.Add(NormalizeDirectory(slnDir));
            }
        }

        if (roots.Count == 0)
        {
            roots.Add(FindGitRootOrCurrent(Directory.GetCurrentDirectory()));
        }

        return [.. roots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    private static string FindGitRootOrCurrent(string startDirectory)
    {
        var normalizedStart = NormalizeDirectory(startDirectory);
        var current = new DirectoryInfo(normalizedStart);
        while (current is not null)
        {
            var gitEntry = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
            {
                return NormalizeDirectory(current.FullName);
            }

            current = current.Parent;
        }

        return normalizedStart;
    }

    private static string NormalizeDirectory(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrUnder(string candidate, string root)
        => string.Equals(candidate, root, PathComparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);

    private static void EnsureUnderRoot(string fullFilePath, string root)
    {
        if (!fullFilePath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
        {
            throw new UnauthorizedAccessException("Requested path escapes the repository root.");
        }
    }

    private static void EnsureNotSensitive(string targetPath, string rootPath)
    {
        var checkSegment = IsSameOrUnder(targetPath, rootPath) && !string.Equals(targetPath, rootPath, PathComparison)
            ? Path.GetRelativePath(rootPath, targetPath)
            : targetPath;

        foreach (var part in checkSegment.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (SensitiveSegments.Contains(part))
            {
                throw new UnauthorizedAccessException($"Access to sensitive directory '{part}' is blocked.");
            }
        }
    }

    private static void EnsureDirectoryExists(string fullPath, string label)
    {
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Repository directory '{label}' does not exist.");
        }
    }

    private RepositorySnapshot GetOrRefreshIndex(string repoRoot, CancellationToken cancellationToken)
    {
        RepositoryIndex index;
        lock (_cacheGate)
        {
            var existingIdx = _indexes.FindIndex(entry => string.Equals(entry.Root, repoRoot, PathComparison));
            if (existingIdx >= 0)
            {
                var found = _indexes[existingIdx];
                _indexes.RemoveAt(existingIdx);
                _indexes.Add(found);
                index = found.Index;
            }
            else
            {
                if (_indexes.Count >= MaxCachedRepositories)
                {
                    _indexes.RemoveAt(0);
                }

                index = new RepositoryIndex();
                _indexes.Add((repoRoot, index));
            }
        }

        return index.Refresh(repoRoot, cancellationToken);
    }

    private static int ScoreSymbol(CodeSymbolNode symbol, string query)
    {
        if (string.Equals(symbol.Name, query, StringComparison.Ordinal))
        {
            return 100;
        }

        if (string.Equals(symbol.Name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (symbol.Name.StartsWith(query, StringComparison.Ordinal))
        {
            return 85;
        }

        if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 65;
        }

        if (!string.IsNullOrEmpty(symbol.Container)
            && $"{symbol.Container}.{symbol.Name}".Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 55;
        }

        if (MatchesCamelHumps(symbol.Name, query))
        {
            return 50;
        }

        if (symbol.Signature.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 30;
        }

        return 0;
    }

    private static bool MatchesCamelHumps(string symbolName, string query)
    {
        if (query.Length < 2 || query.Length > symbolName.Length)
        {
            return false;
        }

        var q = 0;
        for (var i = 0; i < symbolName.Length && q < query.Length; i++)
        {
            var c = symbolName[i];
            if ((i == 0 || char.IsUpper(c) || char.IsDigit(c))
                && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[q]))
            {
                q++;
            }
        }

        return q == query.Length;
    }

    internal static ParsedSourceFile ParseSourceFile(string relativePath, string source, CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: relativePath, cancellationToken: cancellationToken);
        var root = tree.GetCompilationUnitRoot(cancellationToken);

        var fileNamespace = root.Members.OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var symbols = new List<CodeSymbolNode>();
        var outline = new List<CodeOutlineMember>();
        var calls = new List<IndexedCallSite>();

        foreach (var member in root.Members)
        {
            WalkMember(member, fileNamespace ?? string.Empty, 0, relativePath, tree, symbols, outline, calls);
        }

        foreach (var globalStmt in root.Members.OfType<GlobalStatementSyntax>())
        {
            CollectCalls(globalStmt.Statement, "<top-level>", "top-level", "<top-level>", relativePath, tree, calls);
        }

        return new ParsedSourceFile(fileNamespace, [.. symbols], [.. outline], [.. calls]);
    }

    private static void WalkMember(
        MemberDeclarationSyntax member,
        string container,
        int depth,
        string relativePath,
        SyntaxTree tree,
        List<CodeSymbolNode> symbols,
        List<CodeOutlineMember> outline,
        List<IndexedCallSite> calls)
    {
        switch (member)
        {
            case BaseNamespaceDeclarationSyntax ns:
                {
                    var nsName = ns.Name.ToString();
                    var nextContainer = string.IsNullOrEmpty(container) || member is FileScopedNamespaceDeclarationSyntax
                        ? nsName
                        : $"{container}.{nsName}";
                    foreach (var child in ns.Members)
                    {
                        WalkMember(child, nextContainer, depth, relativePath, tree, symbols, outline, calls);
                    }
                    break;
                }

            case TypeDeclarationSyntax typeDecl:
                {
                    var kind = typeDecl switch
                    {
                        InterfaceDeclarationSyntax => "interface",
                        StructDeclarationSyntax => "struct",
                        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
                        _ => "class",
                    };
                    var name = typeDecl.Identifier.ValueText + typeDecl.TypeParameterList;
                    var sig = BuildTypeSignature(typeDecl, kind);
                    var (startLine, endLine) = LineRange(tree, typeDecl.Span);
                    symbols.Add(new CodeSymbolNode(typeDecl.Identifier.ValueText, kind, container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, kind, sig, startLine, endLine, depth));

                    var fullType = string.IsNullOrEmpty(container)
                        ? typeDecl.Identifier.ValueText
                        : $"{container}.{typeDecl.Identifier.ValueText}";
                    if (typeDecl.ParameterList is { } primaryCtorParams)
                    {
                        CollectCalls(primaryCtorParams, typeDecl.Identifier.ValueText, kind, sig, relativePath, tree, calls);
                    }
                    if (typeDecl.BaseList is { } baseList)
                    {
                        CollectCalls(baseList, typeDecl.Identifier.ValueText, kind, sig, relativePath, tree, calls);
                    }

                    foreach (var child in typeDecl.Members)
                    {
                        WalkMember(child, fullType, depth + 1, relativePath, tree, symbols, outline, calls);
                    }
                    break;
                }

            case EnumDeclarationSyntax enumDecl:
                {
                    var name = enumDecl.Identifier.ValueText;
                    var sig = NormalizeWhitespace($"{ModifiersOf(enumDecl.Modifiers)}enum {name}");
                    var (startLine, endLine) = LineRange(tree, enumDecl.Span);
                    symbols.Add(new CodeSymbolNode(name, "enum", container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, "enum", sig, startLine, endLine, depth));
                    break;
                }

            case DelegateDeclarationSyntax delDecl:
                {
                    var name = delDecl.Identifier.ValueText;
                    var sig = NormalizeWhitespace($"{ModifiersOf(delDecl.Modifiers)}delegate {delDecl.ReturnType} {name}{delDecl.TypeParameterList}{delDecl.ParameterList}");
                    var (startLine, endLine) = LineRange(tree, delDecl.Span);
                    symbols.Add(new CodeSymbolNode(name, "delegate", container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, "delegate", sig, startLine, endLine, depth));
                    break;
                }

            case MethodDeclarationSyntax methodDecl:
                {
                    var name = methodDecl.Identifier.ValueText;
                    var sig = NormalizeWhitespace($"{ModifiersOf(methodDecl.Modifiers)}{methodDecl.ReturnType} {name}{methodDecl.TypeParameterList}{methodDecl.ParameterList}");
                    var (startLine, endLine) = LineRange(tree, methodDecl.Span);
                    symbols.Add(new CodeSymbolNode(name, "method", container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, "method", sig, startLine, endLine, depth));
                    var callerSymbol = ShortContainer(container) is { Length: > 0 } shortType ? $"{shortType}.{name}" : name;
                    if (methodDecl.Body is not null)
                    {
                        CollectCalls(methodDecl.Body, callerSymbol, "method", sig, relativePath, tree, calls);
                    }
                    if (methodDecl.ExpressionBody is not null)
                    {
                        CollectCalls(methodDecl.ExpressionBody, callerSymbol, "method", sig, relativePath, tree, calls);
                    }
                    break;
                }

            case ConstructorDeclarationSyntax ctorDecl:
                {
                    var name = ctorDecl.Identifier.ValueText;
                    var sig = NormalizeWhitespace($"{ModifiersOf(ctorDecl.Modifiers)}{name}{ctorDecl.ParameterList}");
                    var (startLine, endLine) = LineRange(tree, ctorDecl.Span);
                    symbols.Add(new CodeSymbolNode(name, "constructor", container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, "constructor", sig, startLine, endLine, depth));
                    var callerSymbol = $"{name}..ctor";
                    if (ctorDecl.Initializer is not null)
                    {
                        CollectCalls(ctorDecl.Initializer, callerSymbol, "constructor", sig, relativePath, tree, calls);
                    }
                    if (ctorDecl.Body is not null)
                    {
                        CollectCalls(ctorDecl.Body, callerSymbol, "constructor", sig, relativePath, tree, calls);
                    }
                    if (ctorDecl.ExpressionBody is not null)
                    {
                        CollectCalls(ctorDecl.ExpressionBody, callerSymbol, "constructor", sig, relativePath, tree, calls);
                    }
                    break;
                }

            case PropertyDeclarationSyntax propDecl:
                {
                    var name = propDecl.Identifier.ValueText;
                    var sig = NormalizeWhitespace($"{ModifiersOf(propDecl.Modifiers)}{propDecl.Type} {name}");
                    var (startLine, endLine) = LineRange(tree, propDecl.Span);
                    symbols.Add(new CodeSymbolNode(name, "property", container, sig, relativePath, startLine, endLine));
                    outline.Add(new CodeOutlineMember(name, "property", sig, startLine, endLine, depth));
                    var callerSymbol = ShortContainer(container) is { Length: > 0 } shortType ? $"{shortType}.{name}" : name;
                    if (propDecl.AccessorList is not null)
                    {
                        CollectCalls(propDecl.AccessorList, callerSymbol, "property", sig, relativePath, tree, calls);
                    }
                    if (propDecl.ExpressionBody is not null)
                    {
                        CollectCalls(propDecl.ExpressionBody, callerSymbol, "property", sig, relativePath, tree, calls);
                    }
                    if (propDecl.Initializer is not null)
                    {
                        CollectCalls(propDecl.Initializer, callerSymbol, "property", sig, relativePath, tree, calls);
                    }
                    break;
                }

            case FieldDeclarationSyntax fieldDecl:
                {
                    var isConst = fieldDecl.Modifiers.Any(SyntaxKind.ConstKeyword);
                    var shortType = ShortContainer(container);
                    foreach (var variable in fieldDecl.Declaration.Variables)
                    {
                        var name = variable.Identifier.ValueText;
                        if (isConst)
                        {
                            var sig = NormalizeWhitespace($"{ModifiersOf(fieldDecl.Modifiers)}{fieldDecl.Declaration.Type} {name}");
                            var (startLine, endLine) = LineRange(tree, variable.Span);
                            symbols.Add(new CodeSymbolNode(name, "const", container, sig, relativePath, startLine, endLine));
                            outline.Add(new CodeOutlineMember(name, "const", sig, startLine, endLine, depth));
                        }

                        if (variable.Initializer is not null)
                        {
                            var callerSymbol = shortType.Length > 0 ? $"{shortType}.{name}" : name;
                            CollectCalls(variable.Initializer, callerSymbol, "field", $"{fieldDecl.Declaration.Type} {name}", relativePath, tree, calls);
                        }
                    }
                    break;
                }
        }
    }

    private static void CollectCalls(
        SyntaxNode rootNode,
        string callerSymbol,
        string callerKind,
        string callerSignature,
        string relativePath,
        SyntaxTree tree,
        List<IndexedCallSite> calls)
    {
        foreach (var node in rootNode.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax invocation)
            {
                var calleeName = ExtractInvokedName(invocation.Expression);
                if (!string.IsNullOrEmpty(calleeName))
                {
                    var (line, _) = LineRange(tree, invocation.Span);
                    var expr = CompactExpression(invocation.ToString());
                    calls.Add(new IndexedCallSite(
                        calleeName,
                        new CodeCallerNode(callerSymbol, callerKind, callerSignature, expr, relativePath, line)));
                }
            }
            else if (node is ObjectCreationExpressionSyntax creation)
            {
                var typeName = ExtractCreatedTypeName(creation.Type);
                if (!string.IsNullOrEmpty(typeName))
                {
                    var (line, _) = LineRange(tree, creation.Span);
                    var expr = CompactExpression(creation.ToString());
                    calls.Add(new IndexedCallSite(
                        typeName,
                        new CodeCallerNode(callerSymbol, callerKind, callerSignature, expr, relativePath, line)));
                }
            }
        }
    }

    private static string? ExtractInvokedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax gen => gen.Identifier.ValueText,
        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.ValueText,
        _ => null,
    };

    private static string? ExtractCreatedTypeName(TypeSyntax typeSyntax) => typeSyntax switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax gen => gen.Identifier.ValueText,
        QualifiedNameSyntax qual => ExtractCreatedTypeName(qual.Right),
        AliasQualifiedNameSyntax alias => ExtractCreatedTypeName(alias.Name),
        _ => null,
    };

    private static string BuildTypeSignature(TypeDeclarationSyntax typeDecl, string kind)
    {
        var basePart = typeDecl.BaseList is { Types.Count: > 0 } bases
            ? " : " + string.Join(", ", bases.Types.Select(static t => t.Type.ToString()))
            : string.Empty;
        var primaryParams = typeDecl.ParameterList?.ToString() ?? string.Empty;
        return NormalizeWhitespace($"{ModifiersOf(typeDecl.Modifiers)}{kind} {typeDecl.Identifier.ValueText}{typeDecl.TypeParameterList}{primaryParams}{basePart}");
    }

    private static string ModifiersOf(SyntaxTokenList modifiers)
        => modifiers.Count == 0 ? string.Empty : string.Join(' ', modifiers.Select(static m => m.ValueText)) + " ";

    private static string ShortContainer(string container)
    {
        var dot = container.LastIndexOf('.');
        return dot >= 0 && dot + 1 < container.Length ? container[(dot + 1)..] : container;
    }

    private static string NormalizeWhitespace(string value)
    {
        var parts = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    private static string CompactExpression(string raw)
    {
        var single = NormalizeWhitespace(raw);
        return single.Length <= 120 ? single : single[..117] + "...";
    }

    private static (int StartLine, int EndLine) LineRange(SyntaxTree tree, TextSpan span)
    {
        var mapped = tree.GetLineSpan(span);
        return (mapped.StartLinePosition.Line + 1, mapped.EndLinePosition.Line + 1);
    }

    internal sealed record IndexedCallSite(string CalleeName, CodeCallerNode Node);

    internal sealed record ParsedSourceFile(
        string? Namespace,
        CodeSymbolNode[] Symbols,
        CodeOutlineMember[] Outline,
        IndexedCallSite[] Calls);

    private sealed record RepositorySnapshot(
        CodeSymbolNode[] AllSymbols,
        IndexedCallSite[] AllCalls);

    private sealed class RepositoryIndex
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, (long LastWriteUtcTicks, long Length, ParsedSourceFile Parsed)> _files =
            new(StringComparer.Ordinal);
        private RepositorySnapshot _snapshot = new([], []);

        public RepositorySnapshot Refresh(string repoRoot, CancellationToken cancellationToken)
        {
            var discovered = EnumerateCSharpFiles(repoRoot, cancellationToken);
            lock (_sync)
            {
                var seen = new HashSet<string>(discovered.Count, StringComparer.Ordinal);
                var toParse = new List<(string RelativePath, string FullPath, long Ticks, long Length)>();

                foreach (var item in discovered)
                {
                    seen.Add(item.RelativePath);
                    if (!_files.TryGetValue(item.RelativePath, out var cached)
                        || cached.LastWriteUtcTicks != item.Ticks
                        || cached.Length != item.Length)
                    {
                        toParse.Add(item);
                    }
                }

                var removed = _files.Keys.Where(key => !seen.Contains(key)).ToArray();
                if (toParse.Count == 0 && removed.Length == 0)
                {
                    return _snapshot;
                }

                foreach (var gone in removed)
                {
                    _files.Remove(gone);
                }

                if (toParse.Count > 0)
                {
                    var parsedBatch = new ConcurrentBag<(string RelativePath, long Ticks, long Length, ParsedSourceFile Parsed)>();
                    Parallel.ForEach(toParse, new ParallelOptions { CancellationToken = cancellationToken }, file =>
                    {
                        var text = File.ReadAllText(file.FullPath);
                        var parsed = ParseSourceFile(file.RelativePath, text, cancellationToken);
                        parsedBatch.Add((file.RelativePath, file.Ticks, file.Length, parsed));
                    });

                    foreach (var entry in parsedBatch)
                    {
                        _files[entry.RelativePath] = (entry.Ticks, entry.Length, entry.Parsed);
                    }
                }

                var orderedFiles = _files
                    .OrderBy(static kv => kv.Key, StringComparer.Ordinal)
                    .Select(static kv => kv.Value.Parsed)
                    .ToArray();

                _snapshot = new RepositorySnapshot(
                    [.. orderedFiles.SelectMany(static f => f.Symbols)],
                    [.. orderedFiles.SelectMany(static f => f.Calls)]);
                return _snapshot;
            }
        }

        private static List<(string RelativePath, string FullPath, long Ticks, long Length)> EnumerateCSharpFiles(
            string repoRoot,
            CancellationToken cancellationToken)
        {
            var results = new List<(string RelativePath, string FullPath, long Ticks, long Length)>();
            var stack = new Stack<string>();
            stack.Push(repoRoot);

            while (stack.Count > 0 && results.Count < MaxIndexedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var currentDir = stack.Pop();

                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(currentDir);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var subDir in subDirs)
                {
                    var dirName = Path.GetFileName(subDir);
                    if (!SkippedDirectories.Contains(dirName) && !SensitiveSegments.Contains(dirName))
                    {
                        stack.Push(subDir);
                    }
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDir, "*.cs");
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var filePath in files)
                {
                    if (results.Count >= MaxIndexedFiles)
                    {
                        break;
                    }

                    if (IsGeneratedFileName(filePath))
                    {
                        continue;
                    }

                    var info = new FileInfo(filePath);
                    if (!info.Exists || info.Length > MaxFileSizeBytes)
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');
                    results.Add((relative, filePath, info.LastWriteTimeUtc.Ticks, info.Length));
                }
            }

            return results;
        }

        private static bool IsGeneratedFileName(string path)
            => path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase);
    }
}
