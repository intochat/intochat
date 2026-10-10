using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DigitalBrain.Apps;

// Whether a program declares rules, read by syntax alone: some shared file (no top-level statements)
// names ILogic. Conservative on purpose: it only decides whether a host without a sandbox must refuse.
internal static class AppRules
{
    public static bool Declared(IReadOnlyDictionary<string, string> program)
        => program.Values.Any(source =>
        {
            var root = CSharpSyntaxTree.ParseText(source ?? "").GetCompilationUnitRoot();
            return !root.Members.OfType<GlobalStatementSyntax>().Any()
                && root.DescendantTokens().Any(token => token.ValueText == "ILogic");
        });
}
