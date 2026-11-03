using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// QUAL-04: a failure is never swallowed silently. An empty catch is closed at zero. A broad
// catch (bare, or Exception) that neither logs nor rethrows is closed at zero. There is no baseline.
public sealed class SwallowFacts
{
    internal sealed record Site(int Line, bool Empty, bool BroadUnlogged);

    internal static IReadOnlyList<Site> Walk(string source)
    {
        var sites = new List<Site>();
        foreach (var clause in CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var empty = clause.Block.Statements.Count == 0 && clause.Filter is null && !IsCancellation(clause);
            var broad = clause.Declaration is null || clause.Declaration.Type.ToString() is "Exception" or "System.Exception";
            var name = clause.Declaration?.Identifier.Text;
            // A filter that names the exception has classified it (the body then returns the result of that class).
            var observed = name is { Length: > 0 } && (clause.Block.DescendantTokens().Any(token => token.Text == name)
                || clause.Filter?.DescendantTokens().Any(token => token.Text == name) == true);
            var handled = observed || clause.Block.DescendantNodes().Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax
                || (node is InvocationExpressionSyntax call && MemberName(call).StartsWith("Log", StringComparison.Ordinal)));
            sites.Add(new Site(clause.GetLocation().GetLineSpan().StartLinePosition.Line + 1, empty, broad && !empty && !handled));
        }
        return sites;
    }

    // Cancellation is control flow, not a failure: a catch for OperationCanceledException alone may stay empty.
    private static bool IsCancellation(CatchClauseSyntax clause)
        => clause.Declaration?.Type.ToString() is "OperationCanceledException" or "System.OperationCanceledException" or "TaskCanceledException" or "System.Threading.Tasks.TaskCanceledException";

    private static string MemberName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
        MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
        IdentifierNameSyntax name => name.Identifier.Text,
        _ => "",
    };

    // Shell programs are embedded script apps, not the module's compiled C#.
    private static IEnumerable<string> ProductionFiles()
        => Repository.ProjectFiles("*.cs",
            path => !path.Contains($"{Path.DirectorySeparatorChar}Shell{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            project => !project.Name.Contains(".Tests", StringComparison.Ordinal) && !project.Name.Contains(".Testing", StringComparison.Ordinal));

    private static List<(string Key, IReadOnlyList<Site> Sites)> Scan()
        => ProductionFiles().Select(path => (Repository.ProjectKey(path), Walk(File.ReadAllText(path)))).ToList();

    [Fact]
    public void TheWalkerFindsKnownCatches()
    {
        var sites = Walk("class C { void M() { try { } catch { } try { } catch (System.Exception) { var x = 1; } try { } catch (System.Exception e) { logger.LogDebug(e, \"x\"); } try { } catch (System.Exception) { throw; } try { } catch (System.Exception error) { sink.TrySetException(error); } } }");
        Assert.Equal(5, sites.Count);
        Assert.True(sites[0].Empty);
        Assert.False(sites[0].BroadUnlogged);
        Assert.True(sites[1].BroadUnlogged);
        Assert.False(sites[2].BroadUnlogged);
        Assert.False(sites[3].BroadUnlogged);
        Assert.False(sites[4].BroadUnlogged);
    }

    [Fact]
    public void NoCatchIsEmpty()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.Empty).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            $"An empty catch hides a failure; log it with Swallow.LogSwallowed ({offenders.Length}): " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoBroadCatchIsUnlogged()
    {
        var offenders = Scan().SelectMany(file => file.Sites.Where(site => site.BroadUnlogged).Select(site => $"{file.Key}:{site.Line}")).ToArray();
        Assert.True(offenders.Length == 0,
            "A broad catch must log (Swallow.LogSwallowed or a Log* call) or rethrow: " + string.Join(", ", offenders));
    }
}
