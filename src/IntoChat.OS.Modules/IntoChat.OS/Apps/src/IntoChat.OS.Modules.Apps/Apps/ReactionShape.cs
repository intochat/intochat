using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Modules.Apps;

// The reaction shape (L0), read from syntax only: a reaction file has exactly one top-level signal loop, either one
// `await foreach` over `….On<T>(…)` (any overload, the operation overload `brain.On(app, "op", ct)` included)
// or one `await ….React<T>(…)`. Setup statements and local functions around it are free, its body may
// loop, and a loop inside a local function or a block does not count. A file without top-level
// statements is shared code and has no shape. The compiler is the type gate; this is the gate in front of it.
public static class ReactionShape
{
    public const string NoReaction = "RXN0001";
    public const string SecondReaction = "RXN0002";
    public const string ModuleIdRequired = "RXN0003";

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

    // RXN0003, read from the app.cs syntax: an app asks for capabilities, not modules (D-05). A module id in
    // `Requires: new(Connectors: [...])` or a module-bound slot (`new("name", "module")`) is an error when that module
    // provides a capability (`provides` maps module id to its capability ids), and also when it is not one of the
    // modules that provide none yet (RequiredModuleIds.Allowed).
    public static ScriptCompilationDiagnostic[] CheckRequires(string path, string source, IReadOnlyDictionary<string, string[]>? provides = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        var root = CSharpSyntaxTree.ParseText(source ?? "", Parse).GetCompilationUnitRoot();
        List<ScriptCompilationDiagnostic> errors = [];
        foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>())
        {
            var named = argument.NameColon?.Name.Identifier.ValueText;
            IEnumerable<LiteralExpressionSyntax> ids = named switch
            {
                "Connectors" when Within(argument, "Requires") => Strings(argument.Expression),
                _ when named == "Slots" || IsPositionalSlots(argument) => argument.Expression.DescendantNodesAndSelf().OfType<BaseObjectCreationExpressionSyntax>()
                    .SelectMany(slot => SlotModule(slot) is { } module ? [module] : Array.Empty<LiteralExpressionSyntax>()),
                _ => [],
            };
            foreach (var literal in ids)
            {
                var id = literal.Token.ValueText;
                if (provides is not null && provides.TryGetValue(id, out var capabilities) && capabilities.Length > 0)
                {
                    errors.Add(new(path, ModuleIdRequired, Line(literal),
                        $"{id} provides {string.Join(", ", capabilities)}; require the capability instead (Requires: new(Capabilities: [\"{capabilities[0]}\"]))."));
                }
                else if (!RequiredModuleIds.Allowed.Contains(id, StringComparer.Ordinal))
                {
                    errors.Add(new(path, ModuleIdRequired, Line(literal),
                        $"'{id}' is a module id; declare a capability in Requires.Capabilities instead (such as storage@1)."));
                }
            }
        }
        return [.. errors];
    }

    // The fifth positional argument of the `Manifest = new(...)` initializer is AppManifest.Slots.
    private static bool IsPositionalSlots(ArgumentSyntax argument)
        => argument.NameColon is null && argument.Parent is ArgumentListSyntax list && list.Arguments.IndexOf(argument) == 4
            && list.Parent is BaseObjectCreationExpressionSyntax { Parent: EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator } }
            && declarator.Identifier.ValueText == "Manifest";

    private static bool Within(SyntaxNode node, string argumentName)
        => node.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault()?.NameColon?.Name.Identifier.ValueText == argumentName;

    private static IEnumerable<LiteralExpressionSyntax> Strings(ExpressionSyntax expression)
        => expression.DescendantNodesAndSelf().OfType<LiteralExpressionSyntax>().Where(literal => literal.IsKind(SyntaxKind.StringLiteralExpression));

    // A slot's module id: its second positional argument or a `Connector:` argument, when that is a string literal.
    private static LiteralExpressionSyntax? SlotModule(BaseObjectCreationExpressionSyntax slot)
    {
        var arguments = slot.ArgumentList?.Arguments.ToArray() ?? [];
        var module = arguments.FirstOrDefault(argument => argument.NameColon?.Name.Identifier.ValueText == "Connector")
            ?? (arguments.Length > 1 && arguments[1].NameColon is null ? arguments[1] : null);
        return module?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal : null;
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

// Module ids an app may still name in Requires.Connectors or a module-bound slot: modules that provide no 0.7.0
// capability. postgres stays until the data capability lands (data capability deferred, 05-CONTEXT deferred ideas).
public static class RequiredModuleIds
{
    public static readonly IReadOnlyList<string> Allowed = ["postgres"];
}
