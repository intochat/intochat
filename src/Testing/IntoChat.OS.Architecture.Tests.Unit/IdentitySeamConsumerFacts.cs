using System.Text.Json;

namespace IntoChat.OS.Architecture.Tests.Unit;

// The privileged identity seams (session issuance, principal provisioning) live in the transitional Framework under
// IntoChat.OS.Framework.Modules.Identity since the Sdk deletion (Phase 3 R4-07). They no longer have an assembly of
// their own, so the fence is on the namespace: only the pinned production projects may use it; widening the list is a
// reviewed change to IdentitySeamConsumers.json. The Framework hosts the seams and tests are exempt.
public sealed class IdentitySeamConsumerFacts
{
    private const string Namespace = "IntoChat.OS.Framework.Modules.Identity";

    private sealed record Consumer(string Project, string Reason);

    [Fact]
    public void OnlyThePinnedProjectsUseThePrivilegedIdentitySeams()
    {
        var pinned = JsonSerializer.Deserialize<Consumer[]>(
            File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "IdentitySeamConsumers.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.All(pinned, consumer => Assert.False(string.IsNullOrWhiteSpace(consumer.Reason), consumer.Project + " needs a reason"));

        var tests = Repository.TestProjects().Select(project => project.Name).ToHashSet(StringComparer.Ordinal);
        var actual = Repository.Projects.Values
            .Where(project => project.Name != "IntoChat.OS.Framework" && !tests.Contains(project.Name))
            .Where(project => Repository.SourceFiles(project.Directory, "*.cs").Any(path => File.ReadAllText(path).Contains(Namespace, StringComparison.Ordinal)))
            .Select(project => project.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(pinned.Select(consumer => consumer.Project).Order(StringComparer.Ordinal), actual);
    }
}
