using System.Reflection;
using System.Text.RegularExpressions;
using IntoChat.OS.Modules.Apps;

namespace IntoChat.OS.Architecture.Tests.Unit;

// IApp is a closed world: every instance member has a caller in production source, so a member nobody calls is deleted
// instead of kept for a lane that no longer exists. Test projects and the Testing tree do not count as callers.
public sealed class IAppSurfaceFacts
{
    [Fact]
    public void EveryIAppMethodHasANonTestCaller()
    {
        var members = typeof(IApp).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(method => method.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(members);

        var sources = Repository.SourceFiles(Repository.SourceRoot, "*.cs", path => !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !Repository.Relative(path).Split('/').Any(part => part.Contains(".Tests", StringComparison.Ordinal) || part is "tests" or "Testing"))
            .Select(File.ReadAllText).ToArray();
        Assert.NotEmpty(sources);

        var uncalled = members
            .Where(name => !sources.Any(text => Regex.IsMatch(text, $@"\.{name}\s*\(")))
            .ToArray();
        Assert.True(uncalled.Length == 0, "IApp members without a production caller:\n" + string.Join('\n', uncalled));
    }
}
