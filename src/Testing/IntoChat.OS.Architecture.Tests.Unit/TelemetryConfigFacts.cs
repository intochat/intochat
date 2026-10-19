using System.Text.Json;
using IntoChat.OS.Authority.Storage;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Shipped settings never turn on sensitive AI telemetry (prompts and tool output leave the process); only a
// Development settings file opts in. The brain log is written Framed by every shipped host.
public sealed class TelemetryConfigFacts
{
    [Fact]
    public void NoShippedAppsettingsEnablesSensitiveTelemetry()
    {
        var offenders = Shipped().Where(file => Values(file, "EnableSensitiveData").Any(IsTrue)).Select(Repository.Relative).ToArray();
        Assert.True(offenders.Length == 0, "Telemetry:EnableSensitiveData belongs in appsettings.Development.json only: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAppHostOptsInToSensitiveTelemetryInDevelopmentOnly()
    {
        var development = Directory.EnumerateFiles(Repository.ProjectNamed("IntoChat.OS.AppHost").Directory, "appsettings.Development.json").Single();
        Assert.Contains(Values(development, "EnableSensitiveData"), IsTrue);
    }

    [Fact]
    public void KernelAppHostAndTheOptionsDefaultWriteTheSameFormat()
    {
        var expected = nameof(BrainLogFormat.Framed);
        Assert.Equal(expected, new BrainStoreOptions().WriteFormat.ToString());
        foreach (var name in new[] { "IntoChat.OS.Kernel", "IntoChat.OS.AppHost" })
        {
            var settings = Path.Combine(Repository.ProjectNamed(name).Directory, "appsettings.json");
            Assert.Equal([expected], Values(settings, "WriteFormat").ToArray());
        }
    }

    private static IEnumerable<string> Shipped()
        => Repository.ProjectFiles("appsettings*.json", path => !path.EndsWith(".Development.json", StringComparison.Ordinal));

    private static bool IsTrue(string value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    // Every value of the named property anywhere in the document, nested or as the tail of a flat key.
    private static IEnumerable<string> Values(string file, string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        var found = new List<string>();
        Walk(document.RootElement);
        return found;

        void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                    {
                        if (property.Name == name || property.Name.EndsWith(":" + name, StringComparison.Ordinal))
                        {
                            found.Add(property.Value.ToString());
                        }
                        Walk(property.Value);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) { Walk(item); }
                    break;
            }
        }
    }
}
