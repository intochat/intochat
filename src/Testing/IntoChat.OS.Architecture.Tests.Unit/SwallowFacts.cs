using System.Text.Json;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// QUAL-04: a failure is never swallowed silently. An empty catch is closed at zero. The broad
// catches (bare, or Exception) that neither log nor rethrow are held in SwallowBaseline.json,
// which only shrinks: a file above its baseline fails, and a file below it (or gone) fails as
// stale until the baseline is lowered.
public sealed class SwallowFacts
{
    internal sealed record Site(int Line, bool Empty, bool BroadUnlogged);

    internal static IReadOnlyList<Site> Walk(string source)
    {
        var sites = new List<Site>();
        foreach (var clause in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var empty = clause.Block.Statements.Count == 0;
            var broad = clause.Declaration is null || clause.Declaration.Type.ToString() is "Exception" or "System.Exception";
            var handled = clause.Block.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax
                || (node is InvocationExpressionSyntax call && MemberName(call).StartsWith("Log", StringComparison.Ordinal)));
            sites.Add(new Site(clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1, empty, broad && !empty && !handled));
        }
        return sites;
    }

    private static string MemberName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => "",
    };

    private static IEnumerable<string> ProductionFiles()
        => Repository.ProjectFiles("*.cs", projects: project =>
            !project.Name.Contains(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Testing", StringComparison.Ordinal));

    private static List<(string Key, IReadOnlyList<Site> Sites)> Scan()
        => ProductionFiles().Select(path => (Repository.ProjectKey(path), Walk(File.ReadAllText(path)))).ToList();

    [Fact]
    public void TheWalkerFindsKnownCatches()
    {
        var sites = Walk("class C { void M() { try { } catch { } try { } catch (System.Exception) { var x = 1; } try { } catch (System.Exception e) { logger.LogDebug(e, \"x\"); } try { } catch (System.Exception) { throw; } } }");
        Assert.Equal(4, sites.Count);
        Assert.True(sites[0].Empty);
        Assert.False(sites[0].BroadUnlogged);
        Assert.True(sites[1].BroadUnlogged);
        Assert.False(sites[2].BroadUnlogged);
        Assert.False(sites[3].BroadUnlogged);
    }

    [Fact]
    public void NoCatchIsEmpty()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.Empty).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            $"An empty catch hides a failure; log it with Swallow.LogSwallowed ({offenders.Length}): " + string.Join(", ", offenders));
    }

    [Fact]
    public void BroadUnloggedCatchesOnlyShrink()
    {
        var baseline = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SwallowBaseline.json")))!;
        var actual = Scan().Select(file => (file.Key, Count: file.Sites.Count(site => site.BroadUnlogged))).Where(file => file.Count > 0)
            .ToDictionary(file => file.Key, file => file.Count, StringComparer.Ordinal);
        var grown = actual.Where(file => file.Value > baseline.GetValueOrDefault(file.Key)).Select(file => $"{file.Key} {baseline.GetValueOrDefault(file.Key)} -> {file.Value}").ToArray();
        Assert.True(grown.Length == 0, "A broad catch must log (Swallow.LogSwallowed or a Log* call) or rethrow: " + string.Join("; ", grown));
        var stale = baseline.Where(file => actual.GetValueOrDefault(file.Key) < file.Value).Select(file => $"{file.Key} {file.Value} -> {actual.GetValueOrDefault(file.Key)}").ToArray();
        Assert.True(stale.Length == 0, "SwallowBaseline.json is stale; lower it: " + string.Join("; ", stale));
    }

    // Regenerate with: SWALLOW_WRITE_BASELINE=<path> (one-shot, run via this fact).
    [Fact]
    public void WriteBaselineWhenAsked()
    {
        var target = Environment.GetEnvironmentVariable("SWALLOW_WRITE_BASELINE");
        if (string.IsNullOrEmpty(target)) { return; }
        var actual = Scan().Select(file => (file.Key, Count: file.Sites.Count(site => site.BroadUnlogged))).Where(file => file.Count > 0)
            .OrderBy(file => file.Key, StringComparer.Ordinal).ToDictionary(file => file.Key, file => file.Count);
        File.WriteAllText(target, JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\r\n") + "\r\n");
    }
}
