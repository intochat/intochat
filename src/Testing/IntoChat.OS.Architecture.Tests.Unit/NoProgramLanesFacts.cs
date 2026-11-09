using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Units all the way: runtime-authored code is a synapse unit and build-time code is a compiled module app. The sandbox
// script lane and the prebuilt-assembly lane are gone, and nothing brings them back under another door.
public sealed partial class NoProgramLanesFacts
{
    private static readonly string[] DeletedTypes = ["AppRunner", "ViewPresenter", "IAppRuntime", "IScriptSandbox", "CSharpAppRuntime", "IInProcessPrograms"];

    [Fact]
    public void NoTypeNamedAppRunnerOrViewPresenter()
        => Assert.Empty(ProductionArchitecture.Types.Where(type => type.Name is "AppRunner" or "ViewPresenter").Select(type => type.FullName));

    [Fact]
    public void NoIAppRuntime()
        => Assert.Empty(ProductionArchitecture.Types.Where(type => type.Name == "IAppRuntime").Select(type => type.FullName));

    [Fact]
    public void NoTypeNamedIScriptSandbox()
        => Assert.Empty(ProductionArchitecture.Types.Where(type => type.Name == "IScriptSandbox").Select(type => type.FullName));

    [Fact]
    public void NoModuleNamedMicrosoftCSharp()
    {
        Assert.DoesNotContain(Repository.Projects.Keys, name => name.StartsWith("IntoChat.OS.Modules.Microsoft.CSharp", StringComparison.Ordinal));
        Assert.DoesNotContain(ProductionArchitecture.Assemblies, assembly => assembly.GetName().Name!.StartsWith("IntoChat.OS.Modules.Microsoft.CSharp", StringComparison.Ordinal));
    }

    // A "__" setting is the kernel's own. The only one is the compiled type, named once by AppTrust (CompiledTypeSetting);
    // every other source reads it through that constant, so no other production file spells a "__" key literal.
    [Fact]
    public void NoSettingKeyStartingWithDoubleUnderscoreIsRead()
    {
        var offenders = ProductionSources()
            .Where(path => !path.EndsWith("AppTrust.cs", StringComparison.Ordinal))
            .SelectMany(path => ReservedKeyLiterals(File.ReadAllText(path)).Select(literal => Repository.Relative(path) + ": " + literal))
            .ToArray();
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheReservedKeyScanSeesALiteralAndIgnoresAnEnvironmentSeparator()
    {
        Assert.Equal(["\"__PrebuiltBehaviorsJson\""], ReservedKeyLiterals("var json = settings[\"__PrebuiltBehaviorsJson\"];"));
        Assert.Empty(ReservedKeyLiterals("var name = key.Replace(\":\", \"__\") + \"__\";"));
    }

    private static string[] ReservedKeyLiterals(string source)
        => [.. ReservedKey().Matches(source).Select(match => match.Value)];

    [Fact]
    public void NoProductionSourceNamesADeletedLaneType()
    {
        var offenders = ProductionSources()
            .SelectMany(path => DeletedTypes.Where(name => Regex.IsMatch(File.ReadAllText(path), $@"\b{name}\b"))
                .Select(name => Repository.Relative(path) + ": " + name))
            .ToArray();
        Assert.Empty(offenders);
    }

    internal static IEnumerable<string> ProductionSources()
        => Repository.SourceFiles(Repository.SourceRoot, "*.cs", path => !Repository.Relative(path).Split('/')
            .Any(part => part.Contains(".Tests", StringComparison.Ordinal) || part is "tests" or "Testing"));

    [GeneratedRegex(@"""__[A-Za-z]\w*""", RegexOptions.CultureInvariant)]
    private static partial Regex ReservedKey();
}
