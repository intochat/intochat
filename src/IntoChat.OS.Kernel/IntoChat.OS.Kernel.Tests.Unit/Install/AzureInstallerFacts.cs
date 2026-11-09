using System.Net;
using System.Text.Json;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Modules;
using IntoChat.OS.Modules.Microsoft.Azure;
using IntoChat.OS.Modules.Microsoft.Azure.Install;

namespace IntoChat.OS.Kernel.Tests.Unit.Install;

public sealed class AzureInstallerFacts
{
    private const string BaseConfigJson = """
        {
          "subscription": "00000000-0000-0000-0000-000000000001",
          "location": "westeurope",
          "prefix": "intochat",
          "env": "test",
          "osImage": "intochat.azurecr.io/os:old",
          "registryServer": "intochat.azurecr.io",
          "installerPrincipalId": "principal-12345",
          "ghcrUsername": "intochat",
          "modules": {}
        }
        """;

    private const string ManifestJson = """
        {
          "version": "1.2.3",
          "commit": "abcdef1234567890",
          "osImage": "intochat.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111"
        }
        """;

    private sealed class TrackingAzureArm : IAzureArm
    {
        public List<ResourceSpec> EnsuredSpecs { get; } = [];
        public List<(string TargetGroup, string TargetRegistry, string SourceRegistryId, string SourceImage)> ImportedImages { get; } = [];
        public string PrincipalIdToReturn { get; set; } = "resolved-principal-oid-9999";
        public Dictionary<string, string> VaultSecrets { get; } = [];

        public ValueTask<string> CurrentPrincipalObjectId(CancellationToken ct) =>
            ValueTask.FromResult(PrincipalIdToReturn);

        public Task ImportImage(string subscription, string targetGroup, string targetRegistry, string sourceRegistryResourceId, string sourceImage, CancellationToken ct)
        {
            ImportedImages.Add((targetGroup, targetRegistry, sourceRegistryResourceId, sourceImage));
            return Task.CompletedTask;
        }

        public Task<ResourceRef> Ensure(string subscription, ResourceSpec spec, CancellationToken ct)
        {
            EnsuredSpecs.Add(spec);
            var endpoint = spec switch
            {
                ContainerAppSpec app => $"{app.Name}.westeurope.azurecontainerapps.io",
                RegistrySpec reg => $"{reg.Name}.azurecr.io",
                _ => "",
            };
            return Task.FromResult(new ResourceRef(ResourceIds.For(subscription, spec), spec.Kind, spec.Name, spec.ResourceGroup) { Endpoint = endpoint });
        }

        public Task<string?> ReadSecret(string vault, string name, CancellationToken ct)
        {
            VaultSecrets.TryGetValue(name, out var value);
            return Task.FromResult<string?>(value);
        }

        public Task<ResourceRef?> Read(string subscription, ResourceRef resource, CancellationToken ct) =>
            Task.FromResult<ResourceRef?>(resource);

        public Task Remove(string subscription, ResourceRef resource, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<ResourceRef[]> List(string subscription, string tagValue, CancellationToken ct) =>
            Task.FromResult<ResourceRef[]>([]);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Handler { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.OK);

        public List<string> RequestedUrls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrls.Add(request.RequestUri?.ToString() ?? "");
            return Task.FromResult(Handler(request));
        }
    }

    [Fact]
    public async Task InstallWithReleaseManifestOverridesTheOsImageDigestAndDeploysSingleOsApp()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var configPath = Path.Combine(tempDir.FullName, "test.json");
            var manifestPath = Path.Combine(tempDir.FullName, "release-manifest.json");
            await File.WriteAllTextAsync(configPath, BaseConfigJson, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(manifestPath, ManifestJson, TestContext.Current.CancellationToken);

            var arm = new TrackingAzureArm();
            var handler = new StubHttpMessageHandler();
            using var client = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath, "--manifest", manifestPath],
                arm,
                [],
                output,
                error,
                client,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            var apps = arm.EnsuredSpecs.OfType<ContainerAppSpec>().ToList();
            var osApp = Assert.Single(apps);
            Assert.Equal("intochat-test-os", osApp.Name);
            Assert.Equal("intochat.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111", osApp.Image);
            Assert.Contains("https://intochat-test-os.westeurope.azurecontainerapps.io/ready", handler.RequestedUrls);
            Assert.Contains("https://intochat-test-os.westeurope.azurecontainerapps.io/", handler.RequestedUrls);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task InstallResolvesInstallerPrincipalIdFromArmCredentialWhenConfigLeavesItEmpty()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var configWithoutPrincipal = """
                {
                  "subscription": "00000000-0000-0000-0000-000000000001",
                  "location": "westeurope",
                  "prefix": "intochat",
                  "env": "test",
                  "osImage": "intochat.azurecr.io/os:1.0.0",
                  "registryServer": "intochat.azurecr.io"
                }
                """;
            var configPath = Path.Combine(tempDir.FullName, "test.json");
            await File.WriteAllTextAsync(configPath, configWithoutPrincipal, TestContext.Current.CancellationToken);

            var arm = new TrackingAzureArm { PrincipalIdToReturn = "resolved-oid-from-jwt-12345" };
            var handler = new StubHttpMessageHandler();
            using var client = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath],
                arm,
                [],
                output,
                error,
                client,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            var roleGrants = arm.EnsuredSpecs.OfType<RoleGrantSpec>().ToList();
            Assert.Contains(roleGrants, r => r.Principal == "resolved-oid-from-jwt-12345");
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task InstallReadsSecretsFromOsDeploySecretsJsonEnvironmentVariable()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        var prevEnv = Environment.GetEnvironmentVariable(AzureInstaller.DeploySecretsJsonVariable);
        try
        {
            var configPath = Path.Combine(tempDir.FullName, "test.json");
            await File.WriteAllTextAsync(configPath, BaseConfigJson, TestContext.Current.CancellationToken);

            var secretsJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                [InstallPlan.MasterKeySecret] = "super-secret-master-key-from-env",
                [InstallPlan.GhcrTokenSecret] = "ghcr-token-from-env-json",
            });
            Environment.SetEnvironmentVariable(AzureInstaller.DeploySecretsJsonVariable, secretsJson);

            var arm = new TrackingAzureArm();
            var handler = new StubHttpMessageHandler();
            using var client = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath],
                arm,
                [],
                output,
                error,
                client,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            var secrets = arm.EnsuredSpecs.OfType<SecretSpec>().ToList();
            var masterKeySecret = Assert.Single(secrets, s => s.Name == InstallPlan.MasterKeySecret);
            Assert.Equal("super-secret-master-key-from-env", masterKeySecret.Value);
            var ghcrSecret = Assert.Single(secrets, s => s.Name == InstallPlan.GhcrTokenSecret);
            Assert.Equal("ghcr-token-from-env-json", ghcrSecret.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AzureInstaller.DeploySecretsJsonVariable, prevEnv);
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task InstallWithPromoteFromImportsTheOsDigestAfterFoundationAndBeforeContainerApps()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var configPath = Path.Combine(tempDir.FullName, "test.json");
            var manifestPath = Path.Combine(tempDir.FullName, "release-manifest.json");
            await File.WriteAllTextAsync(configPath, BaseConfigJson, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(manifestPath, ManifestJson, TestContext.Current.CancellationToken);

            var arm = new TrackingAzureArm();
            var handler = new StubHttpMessageHandler();
            using var client = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var sourceRegistryResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-intochat-test/providers/Microsoft.ContainerRegistry/registries/acrintochattest";

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath, "--manifest", manifestPath, "--promote-from", sourceRegistryResourceId],
                arm,
                [],
                output,
                error,
                client,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, code);
            Assert.Single(arm.ImportedImages);
            Assert.All(arm.ImportedImages, item => Assert.Equal(sourceRegistryResourceId, item.SourceRegistryId));
            Assert.Contains(arm.ImportedImages, item => item.SourceImage == "intochat.azurecr.io/os@sha256:1111111111111111111111111111111111111111111111111111111111111111");
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Fact]
    public async Task InstallVerifiesOsReadyAndUiRootEndpointsAndFailsWhenUnhealthy()
    {
        var tempDir = Directory.CreateTempSubdirectory();
        try
        {
            var configPath = Path.Combine(tempDir.FullName, "test.json");
            await File.WriteAllTextAsync(configPath, BaseConfigJson, TestContext.Current.CancellationToken);

            var arm = new TrackingAzureArm();
            var handler = new StubHttpMessageHandler
            {
                Handler = req =>
                {
                    if (req.RequestUri?.AbsolutePath == "/ready")
                    {
                        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    }
                    return new HttpResponseMessage(HttpStatusCode.OK);
                },
            };
            using var client = new HttpClient(handler);
            var output = new StringWriter();
            var error = new StringWriter();

            var code = await AzureInstaller.Run(
                ["install", "--env", "test", "--config", configPath],
                arm,
                [],
                output,
                error,
                client,
                TestContext.Current.CancellationToken);

            Assert.Equal(1, code);
            Assert.Contains("smoke verification failed", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }
}
