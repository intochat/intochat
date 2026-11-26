using System.Text.RegularExpressions;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Install;
using IntoChat.OS.Modules.Microsoft.Azure;

namespace IntoChat.OS.Architecture.Tests.Unit;

// Every ContainerApp env[].secretRef synthesized by PulumiStackSynthesizer must be declared in
// configuration.secrets[].name, and ignoreChanges must never suppress configuration or configuration.secrets
// (otherwise Azure Container Apps rejects revision activation with ContainerAppSecretNotFound).
public sealed partial class PulumiContainerAppSecretsFacts
{
    [GeneratedRegex(@"^\s{10}- name: ""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex DeclaredSecretName();

    [GeneratedRegex(@"^\s{16}secretRef: ""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex EnvSecretRef();

    [Fact]
    public void EverySynthesizedContainerAppDeclaresAllReferencedEnvSecretsAndDoesNotIgnoreConfigurationSecrets()
    {
        var config = InstallConfig.Universal(
            env: "test",
            subscription: "2a985480-6bb9-44ff-959d-edd5efe2cdb2",
            includeCloudApp: true,
            includeSandboxApp: true) with
        {
            Postgres = new PostgresConfig { Enabled = true, Databases = ["intochat"] },
        };

        var providedSecrets = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["postgres-admin-password"] = "arch-test-password",
            ["openrouter-apikey"] = "sk-or-arch-test",
        };
        var existingVaultSecrets = new HashSet<string>(StringComparer.Ordinal)
        {
            "openai-apikey",
        };

        foreach (var modules in new[] { OperatingSystemModules.Modules, [] })
        {
            var yaml = PulumiStackSynthesizer.Synthesize(config, modules, providedSecrets, existingVaultSecrets).PulumiYaml;

            foreach (var appName in new[] { config.OsName, config.CloudName, config.SandboxName })
            {
                var logical = $"container-app-{appName}";
                var start = yaml.IndexOf("\n  " + logical + ":\n", StringComparison.Ordinal);
                Assert.True(start >= 0, $"ContainerApp resource '{logical}' missing from synthesized Pulumi YAML.");
                var next = Regex.Match(yaml[(start + 1)..], @"\n  \S|\n\S");
                var block = next.Success ? yaml[start..(start + 1 + next.Index)] : yaml[start..];

                var propertiesStart = block.IndexOf("    properties:\n", StringComparison.Ordinal);
                Assert.True(propertiesStart > 0, $"ContainerApp '{logical}' missing properties block.");
                var optionsBlock = block[..propertiesStart];
                Assert.Contains("        - configuration.ingress.customDomains\n", optionsBlock, StringComparison.Ordinal);
                Assert.DoesNotContain("- configuration.secrets", optionsBlock, StringComparison.Ordinal);
                Assert.DoesNotContain("- configuration\n", optionsBlock, StringComparison.Ordinal);

                var declared = DeclaredSecretName().Matches(block).Select(m => m.Groups[1].Value).ToList();
                var referenced = EnvSecretRef().Matches(block).Select(m => m.Groups[1].Value).ToList();

                Assert.NotEmpty(referenced);
                Assert.Equal(declared.Count, declared.Distinct(StringComparer.Ordinal).Count());

                var declaredSet = declared.ToHashSet(StringComparer.Ordinal);
                var missing = referenced.Where(name => !declaredSet.Contains(name)).Distinct(StringComparer.Ordinal).ToArray();
                Assert.True(
                    missing.Length == 0,
                    $"ContainerApp '{appName}' references env secretRef(s) missing from configuration.secrets: {string.Join(", ", missing)}");
            }
        }
    }
}
