using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.Install;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Host.Execution;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Desktop.Tests.Unit;

public sealed class ChildEnvironmentAndLegacyMigrationFacts
{
    [Fact]
    public void ChildEnvironmentPolicy_ScrubsMasterKeyPresenceTokenAndSecretsFromProcessStartInfo()
    {
        var start = new ProcessStartInfo("dotnet");
        start.Environment[IntoChatOSNames.MasterKeyEnvironmentVariable] = "secret-master-key";
        start.Environment[AzureInstaller.MasterKeyVariable] = "secret-azure-master-key";
        start.Environment[DesktopBootstrap.PresenceTokenEnvVar] = "secret-presence-token";
        start.Environment[PresenceProof.TokenHashEnvironmentVariable] = "deadbeef";
        start.Environment["APP_SECRET"] = "top-secret";
        start.Environment["SERVICE_API_KEY"] = "key-value";
        start.Environment["DB_PASSWORD"] = "pw-value";
        start.Environment["AUTH_TOKEN"] = "tok-value";
        start.Environment["SAFE_PATH_MARKER"] = "kept-value";

        ChildEnvironmentPolicy.Scrub(start);

        Assert.False(start.Environment.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
        Assert.False(start.Environment.ContainsKey(AzureInstaller.MasterKeyVariable));
        Assert.False(start.Environment.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
        Assert.False(start.Environment.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));
        Assert.False(start.Environment.ContainsKey("APP_SECRET"));
        Assert.False(start.Environment.ContainsKey("SERVICE_API_KEY"));
        Assert.False(start.Environment.ContainsKey("DB_PASSWORD"));
        Assert.False(start.Environment.ContainsKey("AUTH_TOKEN"));
        Assert.Equal("kept-value", start.Environment["SAFE_PATH_MARKER"]);
    }

    [Fact]
    public void DesktopBootstrap_BuildKernelAndShellStartInfo_NeverLeakMasterKeyOrPresenceTokenEnvVars()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-p05-startinfo-");
        var previousMasterKey = Environment.GetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable);
        var previousPresence = Environment.GetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable, "ambient-master-key");
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, "ambient-presence-token");

            var layout = DesktopBootstrap.ResolveLayout(tempDir.FullName);
            var kernelUrl = new Uri("http://127.0.0.1:5080/");
            var presenceToken = DesktopBootstrap.GeneratePresenceToken();
            var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

            var kernelStart = DesktopBootstrap.BuildKernelStartInfo(
                Path.Combine(tempDir.FullName, "os"),
                kernelUrl,
                layout,
                masterKey,
                presenceToken,
                _ => null);

            Assert.False(kernelStart.Environment.ContainsKey(DesktopBootstrap.MasterKeyEnvVar));
            Assert.False(kernelStart.Environment.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
            Assert.False(kernelStart.Environment.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));

            var configArgPrefix = $"--{DesktopBootstrap.ConfigurationFileConfigKey}=";
            var configArg = Assert.Single(kernelStart.ArgumentList, a => a.StartsWith(configArgPrefix, StringComparison.Ordinal));
            var configPath = configArg[configArgPrefix.Length..];
            Assert.True(File.Exists(configPath));

            var kernelConfig = new ConfigurationBuilder()
                .AddJsonFile(configPath, optional: false, reloadOnChange: false)
                .Build();
            Assert.Equal(masterKey, kernelConfig[IntoChatOSNames.MasterKeyConfigurationKey]);
            Assert.Equal(layout.PresenceFilePath, kernelConfig[PresenceProof.TokenFileConfigurationKey]);
            Assert.Equal(
                PresenceProof.ComputeTokenHash(presenceToken),
                kernelConfig[PresenceProof.TokenHashConfigurationKey]);

            var shellStart = DesktopBootstrap.BuildShellStartInfo(
                Path.Combine(tempDir.FullName, "shell"),
                kernelUrl,
                presenceToken,
                createNoWindow: true);

            Assert.False(shellStart.Environment.ContainsKey(DesktopBootstrap.MasterKeyEnvVar));
            Assert.False(shellStart.Environment.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
            Assert.False(shellStart.Environment.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
            Assert.Contains($"--presence-token={presenceToken}", shellStart.ArgumentList);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable, previousMasterKey);
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, previousPresence);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void PresenceProofValidator_VerifiesByHashOnly_AndIgnoresEnvVarTokenReader()
    {
        var secret = RandomNumberGenerator.GetBytes(PresenceProof.SecretByteCount);
        var token = System.Buffers.Text.Base64Url.EncodeToString(secret);
        var hash = PresenceProof.ComputeTokenHash(secret);

        var previousEnv = Environment.GetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar);
        try
        {
            // Ambient env var MUST NOT be read by Kernel's PresenceProofValidator.
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, "env-spoof-token-0000000000000000000");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PresenceProof.TokenHashConfigurationKey] = hash,
                    [DesktopBootstrap.PresenceTokenEnvVar] = "env-spoof-token-0000000000000000000",
                })
                .Build();

            var validator = new PresenceProofValidator(config);

            Assert.True(validator.MatchesHeader(token));
            Assert.True(validator.MatchesHeader(Convert.ToBase64String(secret)));
            Assert.False(validator.MatchesHeader("env-spoof-token-0000000000000000000"));
            Assert.False(validator.MatchesHeader("forged-token-00000000000000000000000"));

            var cookie = validator.IssueCookieValue();
            Assert.True(validator.MatchesCookie(cookie));
            Assert.Throws<InvalidOperationException>(() => _ = validator.Token);

            // RuntimeShutdownEndpoints delegates exclusively to IPresenceProof without env token reader.
            Assert.Null(typeof(PresenceProofValidator).GetField("EnvironmentVariable"));
            Assert.Null(typeof(RuntimeShutdownEndpoints).GetMethod("ResolveExpectedPresenceToken"));

            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            Assert.False(RuntimeShutdownEndpoints.HasValidPresenceProof(ctx.Request, validator));

            ctx.Request.Headers[PresenceProof.HeaderName] = token;
            Assert.True(RuntimeShutdownEndpoints.HasValidPresenceProof(ctx.Request, validator));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, previousEnv);
        }
    }

    [Fact]
    public void DesktopBootstrap_MigrateLegacyHome_MigratesKeyAndStateBeforeMintingNewMasterKey()
    {
        var tempDir = Directory.CreateTempSubdirectory("intochat-p012-legacy-");
        try
        {
            var localAppData = Path.Combine(tempDir.FullName, "LocalAppData");
            var legacyIntoChatOs = Path.Combine(localAppData, "IntoChatOS");
            var legacyIntoChat = Path.Combine(localAppData, "IntoChat");
            Directory.CreateDirectory(Path.Combine(legacyIntoChatOs, "secrets"));
            Directory.CreateDirectory(Path.Combine(legacyIntoChatOs, "state"));
            Directory.CreateDirectory(Path.Combine(legacyIntoChat, "deploy-tools"));
            Directory.CreateDirectory(Path.Combine(legacyIntoChat, "assets"));

            var expectedKeyBytes = Encoding.UTF8.GetBytes("legacy-migrated-master-key-0123456789ABCDEF");
            File.WriteAllBytes(Path.Combine(legacyIntoChatOs, "secrets", "master.key"), expectedKeyBytes);
            File.WriteAllText(Path.Combine(legacyIntoChatOs, "machine-id"), "machine-123");
            File.WriteAllText(Path.Combine(legacyIntoChatOs, "state", "marker.txt"), "preserved-state");
            File.WriteAllText(Path.Combine(legacyIntoChat, "deploy-tools", "tool.bin"), "ignored");
            File.WriteAllText(Path.Combine(legacyIntoChat, "assets", "icon.png"), "ignored");

            var targetHome = Path.Combine(tempDir.FullName, "TargetHome");
            var layout = DesktopBootstrap.ResolveLayout(targetHome);

            var migrated = DesktopBootstrap.MigrateLegacyHome(layout, localAppData);

            Assert.True(migrated);
            Assert.Equal(expectedKeyBytes, File.ReadAllBytes(layout.MasterKeyPath));
            Assert.Equal("machine-123", File.ReadAllText(Path.Combine(layout.HomeDirectory, "machine-id")));
            Assert.Equal("preserved-state", File.ReadAllText(Path.Combine(layout.HomeDirectory, "state", "marker.txt")));
            Assert.False(Directory.Exists(Path.Combine(layout.HomeDirectory, "deploy-tools")));
            Assert.False(Directory.Exists(Path.Combine(layout.HomeDirectory, "assets")));

            // EnsureMasterKey reuses migrated key instead of overwriting or minting a new one.
            var loadedBytes = DesktopBootstrap.EnsureMasterKey(
                layout.MasterKeyPath,
                layout.LegacyMasterKeyPath,
                () => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                raw => raw,
                localAppData);
            Assert.Equal(expectedKeyBytes, loadedBytes);

            // Second run with existing master.key is idempotent and preserves existing target key.
            File.WriteAllText(Path.Combine(legacyIntoChatOs, "machine-id"), "should-not-overwrite");
            Assert.False(DesktopBootstrap.MigrateLegacyHome(layout, localAppData));
            Assert.Equal("machine-123", File.ReadAllText(Path.Combine(layout.HomeDirectory, "machine-id")));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}
