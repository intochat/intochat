using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Apps;

public sealed record AppDeclarationContent(string Name, string Version, AppManifest Manifest, string Spec);

// app.cs: the spec as the file's leading comment, then a `static class App` holding the Name and
// Version constants and the Manifest field. It is read from syntax only, never run, and it compiles
// with the app's other files, so the type system checks it like any other program file:
//
//   /* spec */
//   using IntoChat.OS.Apps;
//   static class App
//   {
//       public const string Name = "calculator";
//       public const string Version = "1.1.0";
//       public static readonly AppManifest Manifest = new(Title: "Calculator", ...);
//   }
//
// Arguments are literals, collection expressions and `new(...)` of the manifest records; anything a
// reader would have to run is refused.
public static class AppDeclaration
{
    private const string Class = "App";
    private static readonly CSharpParseOptions Parse = new(LanguageVersion.Preview);

    public static AppDeclarationContent Read(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var root = CSharpSyntaxTree.ParseText(source.Replace("\r\n", "\n", StringComparison.Ordinal), Parse).GetCompilationUnitRoot();
        if (root.GetDiagnostics().FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) is { } error)
        { throw Refuse(error.Location, error.GetMessage(CultureInfo.InvariantCulture)); }
        var app = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(type => type.Identifier.ValueText == Class)
            ?? throw new InvalidDataException($"{AppFiles.DeclarationPath} declares no `static class {Class}`.");
        var name = Constant(app, "Name");
        var version = Constant(app, "Version");
        var manifest = app.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .FirstOrDefault(variable => variable.Identifier.ValueText == "Manifest")?.Initializer?.Value
            ?? throw new InvalidDataException($"{AppFiles.DeclarationPath} declares no `{Class}.Manifest`.");
        return new(name, version, ((AppManifest)Evaluate(manifest, typeof(AppManifest))!).Validated(), SpecOf(root));
    }

    public static string Write(AppDeclarationContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var manifest = content.Manifest;
        var text = new System.Text.StringBuilder();
        var spec = content.Spec.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n');
        if (spec.Length > 0)
        {
            if (spec.Contains("*/", StringComparison.Ordinal))
            { foreach (var line in spec.Split('\n')) { text.Append(line.Length == 0 ? "//" : "// " + line).Append('\n'); } }
            else { text.Append("/*\n").Append(spec).Append("\n*/\n"); }
            text.Append('\n');
        }
        text.Append("using IntoChat.OS.Apps;\n\n");
        text.Append("static class ").Append(Class).Append("\n{\n");
        text.Append("    public const string Name = ").Append(Literal(content.Name)).Append(";\n");
        text.Append("    public const string Version = ").Append(Literal(content.Version)).Append(";\n\n");
        text.Append("    public static readonly AppManifest Manifest = new(\n");
        List<string> arguments =
        [
            "Title: " + Literal(manifest.Title),
            "Description: " + Literal(manifest.Description),
            "Operations: " + List(manifest.Operations.Select(operation => $"new({Literal(operation.Name)}, {Literal(operation.Description)})")),
            "Settings: " + List(manifest.Settings.Select(setting => $"new({Literal(setting.Name)}, {Literal(setting.Description)}, {Literal(setting.Default)})")),
            "Slots: " + List(manifest.Slots.Select(slot => $"new({Literal(slot.Name)}, {Literal(slot.Connector)})")),
        ];
        if (manifest.Icon is not null) { arguments.Add("Icon: " + Literal(manifest.Icon)); }
        if (manifest.Toolbar is not null) { arguments.Add("Toolbar: " + List(manifest.Toolbar.Select(Entry))); }
        if (manifest.Requires is { } requires) { arguments.Add("Requires: " + Requirements(requires)); }
        text.Append(string.Join(",\n", arguments.Select(argument => "        " + argument.Replace(": \n", ":\n", StringComparison.Ordinal)))).Append(");\n}\n");
        return text.ToString();
    }

    private static string Entry(AppToolbarEntry entry)
    {
        List<string> parts = [Literal(entry.Operation)];
        if (entry.Kind != AppToolbarKinds.Button) { parts.Add("Kind: " + Literal(entry.Kind)); }
        if (entry.Icon is not null) { parts.Add("Icon: " + Literal(entry.Icon)); }
        if (entry.Label is not null) { parts.Add("Label: " + Literal(entry.Label)); }
        if (entry.Min is { } min) { parts.Add("Min: " + min.ToString("R", CultureInfo.InvariantCulture)); }
        if (entry.Max is { } max) { parts.Add("Max: " + max.ToString("R", CultureInfo.InvariantCulture)); }
        return $"new({string.Join(", ", parts)})";
    }

    private static string Requirements(AppRequirements requires)
    {
        List<string> parts = [];
        if (requires.SemanticTypes is { } types) { parts.Add("SemanticTypes: " + Inline(types)); }
        if (requires.Connectors is { } connectors) { parts.Add("Connectors: " + Inline(connectors)); }
        if (requires.Secrets is { } secrets) { parts.Add("Secrets: " + Inline(secrets)); }
        return $"new({string.Join(", ", parts)})";
    }

    private static string Inline(IEnumerable<string> items) => "[" + string.Join(", ", items.Select(Literal)) + "]";

    private static string List(IEnumerable<string> items)
    {
        var all = items.ToArray();
        return all.Length == 0 ? "[]" : "\n        [\n" + string.Concat(all.Select(item => "            " + item + ",\n")) + "        ]";
    }

    private static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    // The comments before the first token, without their delimiters.
    private static string SpecOf(CompilationUnitSyntax root)
    {
        var lines = new List<string>();
        foreach (var trivia in root.GetFirstToken(includeZeroWidth: true).LeadingTrivia)
        {
            var raw = trivia.ToString();
            if (trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) { lines.Add(raw[2..^2]); }
            else if (trivia.IsKind(SyntaxKind.SingleLineCommentTrivia))
            { lines.Add(raw.StartsWith("// ", StringComparison.Ordinal) ? raw[3..] : raw[2..]); }
        }
        return string.Join("\n", lines).Trim('\n');
    }

    private static string Constant(ClassDeclarationSyntax app, string name)
    {
        var value = app.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(field => field.Declaration.Variables)
            .FirstOrDefault(variable => variable.Identifier.ValueText == name)?.Initializer?.Value
            ?? throw new InvalidDataException($"{AppFiles.DeclarationPath} declares no `{Class}.{name}`.");
        return (string?)Evaluate(value, typeof(string)) ?? throw Refuse(value.GetLocation(), $"{Class}.{name} is null.");
    }

    private static object? Evaluate(ExpressionSyntax expression, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;
        return expression switch
        {
            ParenthesizedExpressionSyntax inner => Evaluate(inner.Expression, type),
            LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } when !type.IsValueType || target != type => null,
            LiteralExpressionSyntax literal when target == typeof(string) && literal.Token.Value is string text => text,
            LiteralExpressionSyntax literal when target == typeof(double) && literal.Token.Value is IConvertible number
                => number.ToDouble(CultureInfo.InvariantCulture),
            PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } negative when target == typeof(double)
                => -(double)Evaluate(negative.Operand, typeof(double))!,
            BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } sum when target == typeof(string)
                => (string?)Evaluate(sum.Left, typeof(string)) + (string?)Evaluate(sum.Right, typeof(string)),
            MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: nameof(AppToolbarKinds) } } kind
                when target == typeof(string)
                && typeof(AppToolbarKinds).GetField(kind.Name.Identifier.ValueText, BindingFlags.Public | BindingFlags.Static) is { IsLiteral: true } field
                => field.GetRawConstantValue(),
            CollectionExpressionSyntax collection when target.IsArray
                => Array(collection.Elements.Select(element => element is ExpressionElementSyntax item
                    ? item.Expression
                    : throw Refuse(element.GetLocation(), "A spread is not a declaration.")), target.GetElementType()!),
            ArrayCreationExpressionSyntax { Initializer: { } items } when target.IsArray
                => Array(items.Expressions, target.GetElementType()!),
            ImplicitArrayCreationExpressionSyntax creation when target.IsArray
                => Array(creation.Initializer.Expressions, target.GetElementType()!),
            ImplicitObjectCreationExpressionSyntax creation when target.IsClass && target != typeof(string)
                => Construct(creation.ArgumentList, creation.Initializer, target, creation),
            ObjectCreationExpressionSyntax creation when target.IsClass && Simple(creation.Type) == target.Name
                => Construct(creation.ArgumentList, creation.Initializer, target, creation),
            _ => throw Refuse(expression.GetLocation(), $"`{expression}` is not a {target.Name} literal a declaration can hold."),
        };
    }

    private static string Simple(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
        SimpleNameSyntax simple => simple.Identifier.ValueText,
        _ => type.ToString(),
    };

    private static Array Array(IEnumerable<ExpressionSyntax> items, Type element)
    {
        var values = items.Select(item => Evaluate(item, element)).ToArray();
        var array = System.Array.CreateInstance(element, values.Length);
        for (var index = 0; index < values.Length; index++) { array.SetValue(values[index], index); }
        return array;
    }

    // Binds positional and named arguments to the record's primary constructor, defaults filling the rest.
    private static object Construct(ArgumentListSyntax? list, InitializerExpressionSyntax? initializer, Type type, SyntaxNode at)
    {
        if (initializer is not null) { throw Refuse(initializer.GetLocation(), $"Declare {type.Name} with constructor arguments, not an initializer."); }
        var constructor = type.GetConstructors().OrderByDescending(candidate => candidate.GetParameters().Length).First();
        var parameters = constructor.GetParameters();
        var values = new object?[parameters.Length];
        var bound = new bool[parameters.Length];
        var position = 0;
        foreach (var argument in list?.Arguments ?? [])
        {
            var index = argument.NameColon is { } named
                ? System.Array.FindIndex(parameters, parameter => parameter.Name == named.Name.Identifier.ValueText)
                : position++;
            if (index < 0 || index >= parameters.Length)
            { throw Refuse(argument.GetLocation(), $"{type.Name} has no parameter for `{argument}`."); }
            values[index] = Evaluate(argument.Expression, parameters[index].ParameterType);
            bound[index] = true;
        }
        for (var index = 0; index < parameters.Length; index++)
        {
            if (bound[index]) { continue; }
            if (!parameters[index].HasDefaultValue) { throw Refuse(at.GetLocation(), $"{type.Name} needs {parameters[index].Name}."); }
            values[index] = parameters[index].DefaultValue;
        }
        return constructor.Invoke(values);
    }

    private static InvalidDataException Refuse(Location location, string message)
        => new($"{AppFiles.DeclarationPath}:{location.GetLineSpan().StartLinePosition.Line + 1} {message}");
}
