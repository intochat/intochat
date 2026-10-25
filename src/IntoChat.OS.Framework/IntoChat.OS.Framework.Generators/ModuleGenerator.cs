using System.Collections.Immutable;
using IntoChat.OS.Framework.Generators.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace IntoChat.OS.Framework.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class ModuleGenerator : IIncrementalGenerator
{
    private const string ModuleAttribute = "IntoChat.OS.Framework.Modules.ModuleAttribute";
    private const string HostingAttribute = "IntoChat.OS.Framework.Modules.ModuleHostingAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var modules = context.SyntaxProvider.ForAttributeWithMetadataName(
                ModuleAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => ModuleParser.Module(ctx, ct))
            .WithTrackingName("ModuleModel");
        var conflicts = modules.Collect().Select(static (models, _) => FindConflictingOwners(models));
        var validatedModules = modules.Combine(conflicts).Select(static (pair, _) => WithOwnershipDiagnostics(pair.Left, pair.Right))
            .WithTrackingName("ValidatedModuleModel");
        context.RegisterSourceOutput(validatedModules, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) { Report(spc, diagnostic); }
            if (model.Diagnostics.Length == 0) { spc.AddSource(HintName(model.Namespace, model.ClassName), SourceText.From(ModuleEmitter.Emit(model), System.Text.Encoding.UTF8)); }
        });

        var hosting = context.SyntaxProvider.ForAttributeWithMetadataName(
                HostingAttribute,
                static (node, _) => node is ClassDeclarationSyntax,
                static (ctx, ct) => ModuleParser.Hosting(ctx))
            .WithTrackingName("HostingModel");
        context.RegisterSourceOutput(hosting, static (spc, model) =>
        {
            foreach (var diagnostic in model.Diagnostics) { Report(spc, diagnostic); }
            if (model.Diagnostics.Length == 0) { spc.AddSource(HintName(model.Namespace, model.ClassName), SourceText.From(HostingEmitter.Emit(model), System.Text.Encoding.UTF8)); }
        });
    }

    private static string HintName(string? ns, string className) => (ns is null ? className : ns + "." + className) + ".g.cs";

    private static EquatableArray<string> FindConflictingOwners(ImmutableArray<ModuleModel> models)
        => new([.. models.Where(model => model.Diagnostics.Length == 0)
            .SelectMany(model => model.ExternalOperations.Select(operation => (operation.Name, Owner: HintName(model.Namespace, model.ClassName))))
            .GroupBy(operation => operation.Name, StringComparer.Ordinal)
            .Where(group => group.Select(operation => operation.Owner).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .SelectMany(group => group.Select(operation => operation.Owner))
            .Distinct(StringComparer.Ordinal).OrderBy(owner => owner, StringComparer.Ordinal)]);

    private static ModuleModel WithOwnershipDiagnostics(ModuleModel model, EquatableArray<string> conflicts)
    {
        if (!conflicts.Contains(HintName(model.Namespace, model.ClassName))) { return model; }
        var declaration = model.Declaration;
        return model with
        {
            Diagnostics = new EquatableArray<DiagnosticModel>([.. model.Diagnostics, new DiagnosticModel("IOS0009",
                "An external operation is owned by more than one module in this compilation.", declaration.FilePath,
                declaration.Start, declaration.Length, declaration.Line, declaration.Character)]),
        };
    }

    private static void Report(SourceProductionContext context, DiagnosticModel model)
    {
        var location = Location.Create(model.FilePath, new TextSpan(model.Start, model.Length),
            new LinePositionSpan(new LinePosition(model.Line, model.Character), new LinePosition(model.Line, model.Character + model.Length)));
        context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ById(model.Id), location, model.Argument));
    }
}
