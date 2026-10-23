using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Apps;

// The synapse shape (L0), read from syntax only: a synapse has exactly one top-level reaction, either one
// `await foreach` over `….On<T>(…)` (any overload, the operation overload `brain.On(app, "op", ct)` included)
// or one `await ….React<T>(…)`. Setup statements and local functions around it are free, its body may
// loop, and a reaction inside a local function or a block does not count. A file without top-level
// statements is shared code and has no shape. The compiler is the type gate; this is the gate in front of it.
public static class SynapseShape
{
    public const string NoReaction = "SYN0001";
    public const string SecondReaction = "SYN0002";

    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);
    private static readonly string[] Wrappers = ["ConfigureAwait", "WithCancellation"];

    public static bool IsShared(string source)
        => Shared(CSharpSyntaxTree.ParseText(source ?? "", Parse).GetCompilationUnitRoot());

    internal static bool Shared(CompilationUnitSyntax root)
        => !root.Members.OfType<GlobalStatementSyntax>().Any();

    // A layout class: shared code with a static `Render` that returns a `UiNode`. It is the example of how an app
    // composes its view, so the fast path shows it to the model, unlike other shared files.
    public static bool IsLayout(string source)
        => Layout(CSharpSyntaxTree.ParseText(source ?? "", Parse).GetCompilationUnitRoot());

    internal static bool Layout(CompilationUnitSyntax root)
        => Shared(root) && root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Any(method => method.Identifier.ValueText == "Render" && method.Modifiers.Any(SyntaxKind.StaticKeyword) && LastName(method.ReturnType) == "UiNode");

    // UiNode, UiNode? and Flutter.Views.UiNode all end in the identifier UiNode.
    private static string? LastName(TypeSyntax type) => type switch
    {
        NullableTypeSyntax nullable => LastName(nullable.ElementType),
        QualifiedNameSyntax qualified => LastName(qualified.Right),
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => null,
    };

    // Every program file of a bundle; app.cs and data files are not programs.
    public static ScriptCompilationDiagnostic[] Check(IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        return [.. files.Where(file => AppFiles.IsProgram(file.Key)).OrderBy(file => file.Key, StringComparer.Ordinal)
            .SelectMany(file => Check(file.Key, file.Value))];
    }

    public static ScriptCompilationDiagnostic[] Check(string path, string source)
    {
        ArgumentNullException.ThrowIfNull(path);
        var statements = CSharpSyntaxTree.ParseText(source ?? "", Parse).GetCompilationUnitRoot()
            .Members.OfType<GlobalStatementSyntax>().Select(global => global.Statement).ToArray();
        if (statements.Length == 0) { return []; }
        var reactions = statements.Where(IsReaction).ToArray();
        if (reactions.Length == 0)
        {
            return [new(path, NoReaction, Line(statements[0]),
                "A synapse is one reaction: one top-level `await foreach` over `brain.On<T>(source, brain.Stopping)` "
                + "(or `brain.On(app, \"operation\", brain.Stopping)`), or one `await brain.React<T>(…)`. This file has none at its "
                + "top level; a reaction inside a local function or a block does not count. A file of only type declarations is shared code.")];
        }
        var first = Line(reactions[0]);
        return [.. reactions.Skip(1).Select(reaction => new ScriptCompilationDiagnostic(path, SecondReaction, Line(reaction),
            $"A synapse is one reaction, and this is another top-level reaction (the first is on line {first}). Move it into its own synapse file."))];
    }

    // The refusal the gates throw, in the compile gate's "file:line message" format.
    public static string Describe(IEnumerable<ScriptCompilationDiagnostic> errors)
        => string.Join("; ", errors.Select(error => $"{error.File}:{error.Line} {error.Message}"));

    private static bool IsReaction(StatementSyntax statement) => statement switch
    {
        CommonForEachStatementSyntax loop => loop.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword) && Calls(loop.Expression, "On"),
        ExpressionStatementSyntax { Expression: AwaitExpressionSyntax awaited } => Calls(awaited.Expression, "React"),
        _ => false,
    };

    // The call itself, or the call under `.ConfigureAwait(…)`/`.WithCancellation(…)`.
    private static bool Calls(ExpressionSyntax expression, string name)
    {
        while (true)
        {
            if (expression is ParenthesizedExpressionSyntax parenthesized) { expression = parenthesized.Expression; continue; }
            if (expression is not InvocationExpressionSyntax invocation) { return false; }
            var (called, receiver) = invocation.Expression switch
            {
                MemberAccessExpressionSyntax member => (member.Name.Identifier.ValueText, member.Expression),
                SimpleNameSyntax simple => (simple.Identifier.ValueText, null),
                _ => ("", null),
            };
            if (called == name) { return true; }
            if (receiver is null || !Wrappers.Contains(called, StringComparer.Ordinal)) { return false; }
            expression = receiver;
        }
    }

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
