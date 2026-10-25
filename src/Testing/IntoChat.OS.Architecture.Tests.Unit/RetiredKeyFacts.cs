using IntoChat.OS.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace IntoChat.OS.Architecture.Tests.Unit;

// A retired setting lives in exactly one place: the retired table of LegacyConfigurationGuard, which stops startup on it.
// Only the table's rows are scanned, so keys still live until a later phase are never touched by this fact (TEST-03).
public sealed class RetiredKeyFacts
{
    private const string Value = "retired-value-must-not-leak";
    private static readonly string[] Extensions = [".cs", ".json", ".ps1", ".sh", ".yml", ".yaml"];

    public static TheoryData<string> RetiredNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in LegacyConfigurationGuard.RetiredSettings) { data.Add(name); }
        return data;
    }

    [Fact]
    public void TheRetiredTableCoversTheRetiredStorageCompositionAndRunModeKeys()
    {
        var names = LegacyConfigurationGuard.RetiredSettings.Select(row => row.Name).ToArray();
        foreach (var expected in new[] { "Profile", "Preset", "Hosting:PersistentStorage", "Platform:Storage:Directory", "Hosting:" + "Mode" })
        { Assert.Contains("IntoChatOS:" + expected, names); }
    }

    // The memory/Azure switch is gone (03-12): every orleans integration field and the old vault key stop startup.
    [Fact]
    public void TheRetiredTableCoversTheOrleansIntegrationFieldsAndTheOldVaultKey()
    {
        var names = LegacyConfigurationGuard.RetiredSettings.Select(row => row.Name).ToArray();
        foreach (var field in new[] { "Clustering", "GrainState", "Reminders", "ClusteringUri", "GrainStateUri", "RemindersUri", "ManagedIdentityClientId", "GrainStateContainer" })
        { Assert.Contains("IntoChatOS:" + "Integrations:orleans:" + field, names); }
        Assert.Contains("IntoChatOS:" + "KeyVault:Uri", names);
    }

    [Theory]
    [MemberData(nameof(RetiredNames))]
    public void NoSourceSpellsARetiredKey(string name)
    {
        var forms = new[] { name, name.Replace(":", "__", StringComparison.Ordinal) };
        var offenders = Candidates()
            .Where(path => File.ReadAllText(path) is var text && forms.Any(form => ContainsKey(text, form)))
            .Select(Repository.Relative)
            .ToArray();
        Assert.True(offenders.Length == 0, $"{name} is retired; only LegacyConfigurationGuard may spell it: {string.Join(", ", offenders)}");
    }

    [Theory]
    [MemberData(nameof(RetiredNames))]
    public void TheGuardStopsStartupOnARetiredKeyInEveryEnvironment(string name)
    {
        var replacement = LegacyConfigurationGuard.RetiredSettings.Single(row => row.Name == name).Replacement;
        var variable = name.Replace(":", "__", StringComparison.Ordinal);
        foreach (var environment in new[] { Environments.Development, Environments.Production })
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [name] = Value }).Build();
            var byKey = Assert.Throws<InvalidOperationException>(() =>
                LegacyConfigurationGuard.Verify(configuration, new Host(environment), [], null));
            Assert.Contains(name, byKey.Message, StringComparison.Ordinal);
            Assert.Contains(replacement, byKey.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Value, byKey.Message, StringComparison.Ordinal);

            var byVariable = Assert.Throws<InvalidOperationException>(() =>
                LegacyConfigurationGuard.Verify(new ConfigurationBuilder().Build(), new Host(environment), [variable], null));
            Assert.Contains(variable, byVariable.Message, StringComparison.Ordinal);
        }
    }

    // A key literal ends where the key ends: IntoChatOS:Profile must not match IntoChatOS:ProfileName.
    private static bool ContainsKey(string text, string key)
    {
        for (var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + key.Length;
            if (end >= text.Length || !(char.IsLetterOrDigit(text[end]) || text[end] == '_')) { return true; }
        }
        return false;
    }

    private static IEnumerable<string> Candidates()
    {
        var skipped = new[] { $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", $"{Path.DirectorySeparatorChar}.dart_tool{Path.DirectorySeparatorChar}" };
        return Directory.EnumerateFiles(Repository.Root, "*", SearchOption.AllDirectories)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !skipped.Any(segment => path.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            .Where(path => !Path.GetFileName(path).StartsWith(nameof(LegacyConfigurationGuard), StringComparison.Ordinal)
                && Path.GetFileName(path) != nameof(RetiredKeyFacts) + ".cs"
                // The gitleaks baseline quotes historical commit messages; it is a record of history, not configuration.
                && Path.GetFileName(path) != ".gitleaks-baseline.json");
    }

    private sealed class Host(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "retired-key-facts";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
