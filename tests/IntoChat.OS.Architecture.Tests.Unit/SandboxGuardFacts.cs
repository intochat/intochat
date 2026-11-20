using System.Text.RegularExpressions;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The sandbox refuses host assemblies by name. A name that no project builds any more refuses nothing, and the code
// that moved out from under it loads freely: that is how IntoChat.OS.Framework stayed on the list after the project
// became IntoChat.OS.Modules.Identity, which the IntoChat.OS.Modules.* allowance then let in.
public sealed partial class SandboxGuardFacts
{
    [Fact]
    public void EveryRefusedIntoChatAssemblyIsARealProject()
    {
        var source = File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Sandbox").Directory, "AppLoadContext.cs"));
        var list = ForbiddenList().Match(source);
        Assert.True(list.Success, "AppLoadContext.ForbiddenPrefixes was not found.");

        var names = Quoted().Matches(list.Groups["body"].Value).Select(match => match.Groups["name"].Value)
            .Where(name => name.StartsWith("IntoChat.", StringComparison.Ordinal)).ToArray();

        Assert.Contains("IntoChat.OS.Modules.Identity", names);
        Assert.All(names, name => Assert.True(Repository.Projects.ContainsKey(name), $"'{name}' is refused by the sandbox but no project builds it."));
    }

    // A module reaches sandboxed code through its Contracts assembly, never its implementation: the old blanket
    // IntoChat.OS.Modules.* allowance must not come back. AppLoadContextAdmitsModuleContractsButNoModuleImplementation in the
    // unit tier checks the rule against every module assembly the kernel ships.
    [Fact]
    public void TheSandboxHasNoBlanketModuleAllowance()
    {
        var source = File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Sandbox").Directory, "AppLoadContext.cs"));
        Assert.DoesNotContain("StartsWith(\"IntoChat.OS.Modules.\"", source, StringComparison.Ordinal);
        Assert.Contains("IsModuleContracts(assemblyName)", source, StringComparison.Ordinal);
    }

    [GeneratedRegex(@"ForbiddenPrefixes\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex ForbiddenList();

    [GeneratedRegex("\"(?<name>[^\"]+)\"")]
    private static partial Regex Quoted();
}
