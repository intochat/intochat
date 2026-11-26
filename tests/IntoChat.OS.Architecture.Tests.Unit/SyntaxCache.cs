using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

internal sealed record CachedSource(string FullPath, string Text, SyntaxTree Tree, CompilationUnitSyntax Root);

/// <summary>
/// Process-wide cache of C# source texts and parsed Roslyn syntax trees shared across all architecture tests.
/// Eliminates redundant disk reads and repeated single-threaded Roslyn parses across HouseRules22Facts,
/// SwallowFacts, SourceTypes, IAppSurfaceFacts, NoRetryFacts, TestTierFacts, FrameworkBoundaryFacts, and UiChildApiFacts.
/// </summary>
internal static class SyntaxCache
{
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly ConcurrentDictionary<string, string> TextCache = new(PathComparer);
    private static readonly ConcurrentDictionary<string, CachedSource> ParsedCache = new(PathComparer);

    internal static string ReadText(string path)
    {
        var full = Path.GetFullPath(path);
        if (ParsedCache.TryGetValue(full, out var cached))
        {
            return cached.Text;
        }
        return TextCache.GetOrAdd(full, static p => File.ReadAllText(p));
    }

    internal static CachedSource Get(string path)
    {
        var full = Path.GetFullPath(path);
        return ParsedCache.GetOrAdd(full, Parse);
    }

    internal static CachedSource[] GetAll(IEnumerable<string> paths)
    {
        var unique = new List<string>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var path in paths)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                unique.Add(full);
            }
        }

        var missing = unique.Where(static p => !ParsedCache.ContainsKey(p)).ToArray();
        if (missing.Length > 1)
        {
            Parallel.ForEach(missing, static p => ParsedCache.GetOrAdd(p, Parse));
        }
        else if (missing.Length == 1)
        {
            ParsedCache.GetOrAdd(missing[0], Parse);
        }

        var result = new CachedSource[unique.Count];
        for (var i = 0; i < unique.Count; i++)
        {
            result[i] = ParsedCache[unique[i]];
        }
        return result;
    }

    private static CachedSource Parse(string fullPath)
    {
        var text = TextCache.GetOrAdd(fullPath, static p => File.ReadAllText(p));
        var tree = CSharpSyntaxTree.ParseText(text, path: fullPath);
        return new CachedSource(fullPath, text, tree, tree.GetCompilationUnitRoot());
    }
}
