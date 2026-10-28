using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Apps.Packaging;
using IntoChat.OS.Modules.Apps;
using IntoChat.OS.Modules.Flutter.Views;
using IntoChat.OS.Modules.Microsoft.CSharp.Runtime;
using IntoChat.OS.Testing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace IntoChat.OS.Modules.Microsoft.CSharp.Tests.Integration;

// A framework [App] class is a program too: the sandbox hosts it with one generic runner that maps requests onto its [On] methods and
// presents through the client. The app here is the framework's own template app, built, packed with the CLI's pack primitives and
// assigned from the package alone.
public sealed class AppClassProgramFacts
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static string TemplateSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!Directory.Exists(Path.Combine(directory.FullName, "framework", "templates"))) { directory = directory.Parent!; }
        var text = File.ReadAllText(Path.Combine(directory.FullName, "framework", "templates", "DigitalBrain.Templates", "content", "intochat-app", "AppName", "AppName.cs"));
        return text.Replace("APP_NAMESPACE", "TemplateApp").Replace("APP_ID", "template").Replace("APP_CLASS", "TemplateApp");
    }

    private static async Task<(byte[] Assembly, byte[] Symbols, string Digest)> Packaged()
    {
        var trusted = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("TemplateApp", [CSharpSyntaxTree.ParseText("global using System; global using System.Threading.Tasks; global using System.Collections.Generic;"), CSharpSyntaxTree.ParseText(TemplateSource())], trusted,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true, nullableContextOptions: NullableContextOptions.Enable)
                .WithSpecificDiagnosticOptions(new Dictionary<string, ReportDiagnostic>()));
        using var dll = new MemoryStream();
        using var pdb = new MemoryStream();
        var emitted = compilation.Emit(dll, pdb);
        Assert.True(emitted.Success, string.Join("; ", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        var build = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "app-class-" + Guid.NewGuid().ToString("N"))).FullName;
        await File.WriteAllBytesAsync(Path.Combine(build, "TemplateApp.dll"), dll.ToArray(), Ct);
        // The CLI's metadata step: the descriptor is read from the built [App] class, then written as the archive.
        var context = new AssemblyLoadContext("template-build", isCollectible: true);
        CompiledAppDescriptor descriptor;
        try
        {
            var assembly = context.LoadFromStream(new MemoryStream(dll.ToArray()));
            var type = assembly.GetTypes().Single(t => t.IsDefined(typeof(AppAttribute), false));
            var definition = new AppDefinition(type);
            descriptor = TrustedBuildMetadata.Extract(type, definition.Manifest);
        }
        finally { context.Unload(); }
        await using var archive = new MemoryStream();
        await new AppPackageWriter().Write(descriptor, build, archive, Ct);
        archive.Position = 0;
        var validated = await new AppPackageReader(sharedAssemblies: descriptor.SharedAssemblies).Read(archive, Path.Combine(build, "validated"), Ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(validated.DirectoryPath, validated.Descriptor.EntryAssembly), Ct);
        return (bytes, pdb.ToArray(), validated.Digest);
    }

    [Trait("host", "own")]
    [Fact]
    public async Task ThePackagedTemplateAppRunsFromItsAssemblyAndPresentsItsStartView()
    {
        var (assembly, symbols, _) = await Packaged();
        await using var rig = await PrebuiltRig.Start(Ct, operations: ["open"], views: true);
        using var caller = rig.Brain.Owner.AsCaller();
        var hash = Convert.ToHexStringLower(SHA256.HashData(assembly));

        await rig.Programs.AssignPrebuilt(rig.AppKey, new(StringComparer.Ordinal) { ["TemplateApp.dll"] = new(assembly, symbols, hash) },
            new(StringComparer.Ordinal) { ["App"] = rig.AppKey });
        await rig.Attach(Ct);
        await rig.App.Request("open", "");

        var view = rig.Brain.Get<IView>(rig.AppKey + "/view");
        var published = await TestWait.UntilAsync(_ => view.Read(), read => read is not null, TimeSpan.FromSeconds(30), Ct);
        Assert.Contains(published!.Root.Children[1].Children, node => node.Text == "Welcome to TemplateApp.");
    }
}
