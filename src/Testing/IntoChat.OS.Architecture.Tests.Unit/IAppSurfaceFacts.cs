using System.Reflection;
using IntoChat.OS.Modules.Apps;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace IntoChat.OS.Architecture.Tests.Unit;

// IApp is a closed world: every instance member has a caller in production source, so a member nobody calls is deleted
// instead of kept for a lane that no longer exists. Test projects and the Testing tree do not count as callers. A call counts
// only when its receiver is syntactically an IApp: a local, parameter, field or property declared IApp (or var-initialized from
// Get<IApp>), a method declared to return IApp, or Get<IApp>(...) itself. Comments and string literals never count.
public sealed class IAppSurfaceFacts
{
    [Fact]
    public void EveryIAppMethodHasANonTestCaller()
    {
        var members = typeof(IApp).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(members);

        var called = Repository.SourceFiles(Repository.SourceRoot, "*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !Repository.Relative(path).Split('/').Any(part => part.Contains(".Tests", StringComparison.Ordinal) || part is "tests" or "Testing"))
            .SelectMany(path => CalledOnIApp(File.ReadAllText(path)))
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(called);

        var uncalled = members.Where(name => !called.Contains(name)).ToArray();
        Assert.True(uncalled.Length == 0, "IApp members without a production caller:\n" + string.Join('\n', uncalled));
    }

    [Fact]
    public void ACallOnAnotherTypeIsNotACallerOfAnIAppMember()
    {
        Assert.DoesNotContain("Replace", CalledOnIApp("class C { string M() => \"x\".Replace(\"a\", \"b\"); }"));
        Assert.DoesNotContain("Delete", CalledOnIApp("class C { void M(File file) { file.Delete(); } }"));
        Assert.DoesNotContain("Replace", CalledOnIApp("class C { void M() { // app.Replace(b)\n var s = \"app.Replace(b)\"; } }"));
    }

    [Fact]
    public void ACallOnAnIAppReceiverIsACaller()
    {
        Assert.Contains("Replace", CalledOnIApp("class C { Task M(IApp app, AppBundle b) => app.Replace(b); }"));
        Assert.Contains("Delete", CalledOnIApp("class C { Task M() => Get<IApp>(id).Delete(); }"));
        Assert.Contains("Publish", CalledOnIApp("class C { IApp App(string k) => null!; Task M() => App(\"k\").Publish(); }"));
        Assert.Contains("Export", CalledOnIApp("class C { Task M() { var app = Get<IApp>(id); return app!.Export(); } }"));
        Assert.Contains("ExportVersion", CalledOnIApp("class C { IApp? Try(string n) => null; Task M() { if (Try(\"n\") is { } app) { return app.ExportVersion(\"1\"); } return Task.CompletedTask; } }"));
        Assert.Contains("Read", CalledOnIApp("class C { IApp? _app; Task M() => _app?.Read(); }"));
    }

    private static HashSet<string> CalledOnIApp(string source)
    {
        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclarationSyntax declaration:
                    foreach (var variable in declaration.Variables)
                    {
                        if (IsIApp(declaration.Type) || (declaration.Type.IsVar && variable.Initializer is { } init && ReturnsIApp(init.Value)))
                        { names.Add(variable.Identifier.Text); }
                    }
                    break;
                case ParameterSyntax { Type: { } type } parameter when IsIApp(type): names.Add(parameter.Identifier.Text); break;
                case PropertyDeclarationSyntax property when IsIApp(property.Type): names.Add(property.Identifier.Text); break;
                case MethodDeclarationSyntax method when IsIApp(method.ReturnType): names.Add(method.Identifier.Text); break;
                case LocalFunctionStatementSyntax local when IsIApp(local.ReturnType): names.Add(local.Identifier.Text); break;
            }
        }

        // `receiver is { } app` and `x is IApp app` bind a variable of the receiver's type.
        foreach (var test in root.DescendantNodes().OfType<IsPatternExpressionSyntax>())
        {
            switch (test.Pattern)
            {
                case RecursivePatternSyntax { Designation: SingleVariableDesignationSyntax variable } when IsReceiver(test.Expression, names):
                    names.Add(variable.Identifier.Text); break;
                case DeclarationPatternSyntax { Designation: SingleVariableDesignationSyntax variable, Type: var type } when IsIApp(type):
                    names.Add(variable.Identifier.Text); break;
            }
        }

        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Parent is InvocationExpressionSyntax invocation && invocation.Expression == access && IsReceiver(access.Expression, names))
            { called.Add(access.Name.Identifier.Text); }
        }
        foreach (var binding in root.DescendantNodes().OfType<MemberBindingExpressionSyntax>())
        {
            if (binding.Parent is InvocationExpressionSyntax invocation && invocation.Expression == binding
                && binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } conditional && IsReceiver(conditional.Expression, names))
            { called.Add(binding.Name.Identifier.Text); }
        }
        return called;
    }

    private static bool IsIApp(TypeSyntax type)
    {
        if (type is NullableTypeSyntax nullable) { type = nullable.ElementType; }
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text == "IApp",
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text == "IApp",
            _ => false,
        };
    }

    // Get<IApp>(...) or a call of a method declared to return IApp.
    private static bool ReturnsIApp(ExpressionSyntax expression)
    {
        expression = Strip(expression);
        return expression is InvocationExpressionSyntax { Expression: var callee } && callee switch
        {
            GenericNameSyntax generic => generic.TypeArgumentList.Arguments.Any(IsIApp),
            MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } => generic.TypeArgumentList.Arguments.Any(IsIApp),
            _ => false,
        };
    }

    private static bool IsReceiver(ExpressionSyntax receiver, HashSet<string> names)
    {
        receiver = Strip(receiver);
        if (ReturnsIApp(receiver)) { return true; }
        return receiver switch
        {
            IdentifierNameSyntax identifier => names.Contains(identifier.Identifier.Text),
            MemberAccessExpressionSyntax member => names.Contains(member.Name.Identifier.Text),
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax callee } => names.Contains(callee.Identifier.Text),
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax callee } => names.Contains(callee.Name.Identifier.Text),
            _ => false,
        };
    }

    private static ExpressionSyntax Strip(ExpressionSyntax expression)
    {
        while (true)
        {
            switch (expression)
            {
                case ParenthesizedExpressionSyntax parenthesized: expression = parenthesized.Expression; break;
                case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } postfix: expression = postfix.Operand; break;
                case AwaitExpressionSyntax await: expression = await.Expression; break;
                default: return expression;
            }
        }
    }
}
