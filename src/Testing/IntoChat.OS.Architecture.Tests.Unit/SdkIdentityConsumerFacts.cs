using System.Text.Json;

namespace IntoChat.OS.Architecture.Tests.Unit;

// IntoChat.OS.Sdk.Identity carries the privileged seams (session issuance, principal provisioning). Only the
// pinned projects may reference it; widening the list is a reviewed change to SdkIdentityConsumers.json.
public sealed class SdkIdentityConsumerFacts
{
    private const string Package = "IntoChat.OS.Sdk.Identity";

    private sealed record Consumer(string Project, string Reason);

    [Fact]
    public void OnlyThePinnedProjectsReferenceTheIdentityPackage()
    {
        var pinned = JsonSerializer.Deserialize<Consumer[]>(
            File.ReadAllText(Path.Combine(Repository.ProjectNamed("IntoChat.OS.Architecture.Tests.Unit").Directory, "SdkIdentityConsumers.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.All(pinned, consumer => Assert.False(string.IsNullOrWhiteSpace(consumer.Reason), consumer.Project + " needs a reason"));

        var actual = Repository.Projects.Values
            .Where(project => project.Name != Package)
            .Where(project => Repository.References(project.Path).Contains(Package, StringComparer.Ordinal)
                || Repository.PackageReferences(project.Path).Contains(Package, StringComparer.Ordinal))
            .Select(project => project.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(pinned.Select(consumer => consumer.Project).Order(StringComparer.Ordinal), actual);
    }
}
