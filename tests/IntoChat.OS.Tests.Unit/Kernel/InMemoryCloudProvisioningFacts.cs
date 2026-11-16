using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Kernel.Install;

namespace IntoChat.OS.Kernel.Tests.Unit;

public sealed class InMemoryCloudProvisioningFacts
{
    private const string ConfigJson = """
        {
          "subscription": "00000000-0000-0000-0000-000000000001",
          "location": "westeurope",
          "prefix": "intochat",
          "env": "test",
          "osImage": "ghcr.io/intochat/os:1.0.0",
          "registryServer": "intochat.azurecr.io",
          "modules": {
            "auth": {
              "PublicBaseUrl": "https://os.test.intochat.io"
            }
          }
        }
        """;

    [Fact]
    public async Task FullOperatingSystemCompositionProvisionsAgainstInMemoryCloud()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var configPath = Path.Combine(directory.FullName, "test.json");
            await File.WriteAllTextAsync(configPath, ConfigJson, TestContext.Current.CancellationToken);
            var cloud = new InMemoryCloud(autoSeedVaultSecrets: true);
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath],
                cloud,
                OperatingSystemModules.Modules,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.True(code == 0, error.ToString());
            var app = Assert.IsType<ContainerAppSpec>(cloud.OsApp);
            Assert.Equal("intochat-test-os", app.Name);
            Assert.Contains(app.Variables, pair => pair.Key.EndsWith("PublicBaseUrl", StringComparison.Ordinal) && pair.Value == "https://os.test.intochat.io");
            Assert.Contains(app.Secrets, secret => secret.Name == ManifestEnvironment.SecretName("auth", "google-client-id"));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
