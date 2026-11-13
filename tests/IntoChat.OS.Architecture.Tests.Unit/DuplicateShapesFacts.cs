using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

public sealed partial class DuplicateShapesFacts
{
    [GeneratedRegex(@"\bstring\s+FullKey\b", RegexOptions.CultureInvariant)]
    private static partial Regex FullKeyMember();

    [GeneratedRegex(@"private\s+(?:static\s+)?void\s+Require\w*\([^)]*\)\s*\{[^}]*CallerKind[^}]*UnauthorizedAccessException", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex PrivateRequireCallerKind();

    [Fact]
    public void ModulesDeclareNoFullKeyMember()
    {
        var offenders = Repository.SourceFiles(Repository.ModulesRoot, "*.cs")
            .Where(path => Path.GetFileName(path) is not ("AiNeuron.cs" or "UiNeuron.cs" or "RoslynNeuron.cs" or "TableNeuron.cs") && FullKeyMember().IsMatch(File.ReadAllText(path)))
            .Select(Repository.Relative)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Modules use NeuronId.FullKey() from IntoChat.OS instead of declaring a FullKey member:\n"
            + string.Join('\n', offenders));
    }

    [Fact]
    public void ModulesDeclareNoPrivateCallerKindAdmission()
    {
        var offenders = Repository.SourceFiles(Repository.ModulesRoot, "*.cs")
            .Where(path => Path.GetFileName(path) is not "GitHubRepositoryNeuron.cs" && PrivateRequireCallerKind().IsMatch(File.ReadAllText(path)))
            .Select(Repository.Relative)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Modules use CallerAdmission in IntoChat.OS.Framework instead of private Require* methods checking CallerKind:\n"
            + string.Join('\n', offenders));
    }
}
