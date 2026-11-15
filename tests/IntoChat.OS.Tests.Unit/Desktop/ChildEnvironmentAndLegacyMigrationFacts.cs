using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Execution;
using IntoChat.OS.Kernel;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using IntoChat.OS.Modules.Microsoft.Azure.Install;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Net;

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
        start.Environment[PresenceProofValidator.TokenHashEnvironmentVariable] = "deadbeef";
        start.Environment["APP_SECRET"] = "top-secret";
        start.Environment["SERVICE_API_KEY"] = "key-value";
        start.Environment["DB_PASSWORD"] = "pw-value";
        start.Environment["AUTH_TOKEN"] = "tok-value";
        start.Environment["SAFE_PATH_MARKER"] = "kept-value";

        ChildEnvironmentPolicy.Scrub(start);

        Assert.False(start.Environment.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
        Assert.False(start.Environment.ContainsKey(AzureInstaller.MasterKeyVariable));
        Assert.False(start.Environment.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
        Assert.False(start.Environment.ContainsKey(PresenceProofValidator.TokenHashEnvironmentVariable));
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
            Assert.Equal(layout.PresenceFilePath, kernelConfig[PresenceProofValidator.TokenFileConfigurationKey]);
            Assert.Equal(
                PresenceProofValidator.ComputeTokenHash(presenceToken),
                kernelConfig[PresenceProofValidator.TokenHashConfigurationKey]);

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
    public async Task SystemProcessRunner_ScrubsAmbientMasterKeyAndPresenceTokenFromChildProcess()
    {
        var previousMasterKey = Environment.GetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable);
        var previousPresence = Environment.GetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("intochat-p05-runner-");
        try
        {
            Environment.SetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable, "leaked-host-master-key");
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, "leaked-host-presence-token");

            var runner = new SystemProcessRunner();
            var file = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            string[] args = OperatingSystem.IsWindows()
                ? ["/d", "/c", $"echo MK=%{IntoChatOSNames.MasterKeyEnvironmentVariable}% PR=%{DesktopBootstrap.PresenceTokenEnvVar}% SAFE=%CANDIDATE_SAFE%"]
                : ["-c", $"printf 'MK=%s PR=%s SAFE=%s' \"${IntoChatOSNames.MasterKeyEnvironmentVariable}\" \"${DesktopBootstrap.PresenceTokenEnvVar}\" \"$CANDIDATE_SAFE\""];

            var result = await runner.RunAsync(
                file,
                args,
                tempDir.FullName,
                new Dictionary<string, string> { ["CANDIDATE_SAFE"] = "ok" },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("leaked-host-master-key", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked-host-presence-token", result.Output, StringComparison.Ordinal);
            Assert.Contains("SAFE=ok", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable, previousMasterKey);
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, previousPresence);
            tempDir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DockerCandidateHost_ScrubsLiveMasterKeyAndPresenceTokenFromCopiedAndMcpEnvironment()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [IntoChatOSNames.MasterKeyConfigurationKey] = "host-master-secret",
                [PresenceProofValidator.TokenHashConfigurationKey] = "host-presence-hash",
                [DesktopBootstrap.PresenceTokenEnvVar] = "host-presence-secret",
                ["IntoChatOS:Home"] = "/live/home",
            })
            .Build();

        var host = new DockerCandidateHost(
            new SystemProcessRunner(),
            config,
            _ => Task.FromResult(Path.GetTempPath()),
            _ => Task.FromResult(new CandidateSecrets(new Dictionary<string, string>())),
            _ => false,
            new HttpClient());

        var mcpEnv = host.McpEnvironment("abc1234");
        Assert.False(mcpEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
        Assert.False(mcpEnv.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
        Assert.False(mcpEnv.ContainsKey(PresenceProofValidator.TokenHashEnvironmentVariable));

        var siloEnv = host.Environment("abc1234", new Dictionary<string, string>(), "pg-pw", "ch-pw");
        Assert.True(siloEnv.TryGetValue(IntoChatOSNames.MasterKeyEnvironmentVariable, out var candidateKey));
        Assert.NotEqual("host-master-secret", candidateKey);
        Assert.False(siloEnv.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
        Assert.False(siloEnv.ContainsKey(PresenceProofValidator.TokenHashEnvironmentVariable));
    }

    [Fact]
    public void PresenceProofValidator_VerifiesByHashOnly_AndIgnoresEnvVarTokenReader()
    {
        var secret = RandomNumberGenerator.GetBytes(PresenceProofValidator.SecretByteCount);
        var token = System.Buffers.Text.Base64Url.EncodeToString(secret);
        var hash = PresenceProofValidator.ComputeTokenHash(secret);

        var previousEnv = Environment.GetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar);
        try
        {
            // Ambient env var MUST NOT be read by Kernel's PresenceProofValidator.
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, "env-spoof-token-0000000000000000000");

            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [PresenceProofValidator.TokenHashConfigurationKey] = hash,
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

            ctx.Request.Headers[PresenceProofValidator.HeaderName] = token;
            Assert.True(RuntimeShutdownEndpoints.HasValidPresenceProof(ctx.Request, validator));
        }
        finally
        {
            Environment.SetEnvironmentVariable(DesktopBootstrap.PresenceTokenEnvVar, previousEnv);
        }
    }

    [Fact]
    public async Task ShellHostingExtensions_WindowAndWeb_MintPresenceProofPassHashToKernelAndProofToShell()
    {
        var tempRoot = Directory.CreateTempSubdirectory("intochat-p06-shellhost-");
        try
        {
            var windowDir = Path.Combine(tempRoot.FullName, "shell-window");
            Directory.CreateDirectory(Path.Combine(windowDir, "lib"));
            Directory.CreateDirectory(Path.Combine(windowDir, "windows"));
            File.WriteAllText(Path.Combine(windowDir, "pubspec.yaml"), "name: test_window_shell\n");
            File.WriteAllText(Path.Combine(windowDir, "lib", "main.dart"), "void main() {}");

            var webDir = Path.Combine(tempRoot.FullName, "shell-web");
            Directory.CreateDirectory(Path.Combine(webDir, "lib"));
            Directory.CreateDirectory(Path.Combine(webDir, "web"));
            File.WriteAllText(Path.Combine(webDir, "pubspec.yaml"), "name: test_web_shell\n");
            File.WriteAllText(Path.Combine(webDir, "lib", "main.dart"), "void main() {}");
            File.WriteAllText(Path.Combine(webDir, "web", "index.html"), "<html></html>");
            File.WriteAllText(Path.Combine(webDir, "Dockerfile"), "FROM scratch\n");

            // Window mode check
            {
                var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
                var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
                    .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(o =>
                    {
                        o.RunDesktopApp();
                        o.Hosting = o.Hosting with { WorkingDirectory = windowDir };
                    });

                var silo = builder.AddExecutable("OperatingSystem-a", "unused", ".")
                    .WithHttpEndpoint(name: ShellNames.HttpEndpointName, targetPort: 5080)
                    .WithReference(brain);

                var siloEnv = new Dictionary<string, object>();
                var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, TestContext.Current.CancellationToken);
                foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(siloCtx); }

                Assert.False(siloEnv.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
                Assert.True(siloEnv.TryGetValue(PresenceProofValidator.TokenHashEnvironmentVariable, out var hashObj));
                var expectedHash = Assert.IsType<string>(hashObj);

                var shellResource = Assert.Single(builder.Resources.OfType<ExecutableResource>(), r => r.Name == ShellNames.DefaultFlutterResourceName);
                var shellArgs = new List<object>();
                var argsCtx = new CommandLineArgsCallbackContext(shellArgs, shellResource, TestContext.Current.CancellationToken);
                foreach (var cb in shellResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()) { await cb.Callback(argsCtx); }

                var definePrefix = ShellHostingExtensions.PresenceDartDefinePrefix;
                var defineArg = Assert.Single(shellArgs.OfType<string>(), a => a.StartsWith(definePrefix, StringComparison.Ordinal));
                var shellToken = defineArg[definePrefix.Length..];
                Assert.Equal(expectedHash, PresenceProofValidator.ComputeTokenHash(shellToken));

                var shellEnv = new Dictionary<string, object> { [IntoChatOSNames.MasterKeyEnvironmentVariable] = "ambient-mk", [DesktopBootstrap.PresenceTokenEnvVar] = "ambient-pt" };
                var shellCtx = new EnvironmentCallbackContext(builder.ExecutionContext, shellResource, shellEnv, TestContext.Current.CancellationToken);
                foreach (var cb in shellResource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(shellCtx); }
                Assert.False(shellEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
                Assert.False(shellEnv.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
            }

            // Web mode check
            {
                var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
                var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
                    .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(o =>
                    {
                        o.RunWebApp();
                        o.Hosting = o.Hosting with { WorkingDirectory = webDir };
                    });

                var silo = builder.AddExecutable("OperatingSystem-a", "unused", ".")
                    .WithHttpEndpoint(name: ShellNames.HttpEndpointName, targetPort: 5080)
                    .WithReference(brain);

                var siloEnv = new Dictionary<string, object>();
                var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, TestContext.Current.CancellationToken);
                foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(siloCtx); }

                Assert.True(siloEnv.TryGetValue(PresenceProofValidator.TokenHashEnvironmentVariable, out var hashObj));
                var expectedHash = Assert.IsType<string>(hashObj);

                var shellResource = Assert.Single(builder.Resources, r => r.Name == ShellNames.DefaultFlutterResourceName);
                var browser = Assert.Single(shellResource.Annotations.OfType<BrainBrowserAnnotation>());
                var fragmentIndex = browser.Path.IndexOf("#presence=", StringComparison.Ordinal);
                Assert.True(fragmentIndex > 0, $"Expected #presence= fragment in browser path '{browser.Path}'.");
                var fragmentToken = browser.Path[(fragmentIndex + "#presence=".Length)..];
                Assert.Equal(expectedHash, PresenceProofValidator.ComputeTokenHash(fragmentToken));
            }

            // WebContainer mode check
            {
                var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
                var brain = builder.AddIntoChatOS("modules", new IntoChatOSHostingOptions())
                    .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(o =>
                    {
                        o.RunWebApp();
                        o.Hosting = o.Hosting with { WorkingDirectory = webDir, WebContainer = true };
                    });

                var silo = builder.AddExecutable("OperatingSystem-a", "unused", ".")
                    .WithHttpEndpoint(name: ShellNames.HttpEndpointName, targetPort: 5080)
                    .WithReference(brain);

                var siloEnv = new Dictionary<string, object>();
                var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, TestContext.Current.CancellationToken);
                foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(siloCtx); }

                Assert.True(siloEnv.TryGetValue(PresenceProofValidator.TokenHashEnvironmentVariable, out var hashObj));
                var expectedHash = Assert.IsType<string>(hashObj);

                var shellContainer = Assert.Single(builder.Resources.OfType<ContainerResource>(), r => r.Name == ShellNames.DefaultFlutterResourceName);
                var browser = Assert.Single(shellContainer.Annotations.OfType<BrainBrowserAnnotation>());
                var fragmentIndex = browser.Path.IndexOf("#presence=", StringComparison.Ordinal);
                Assert.True(fragmentIndex > 0, $"Expected #presence= fragment in container browser path '{browser.Path}'.");
                var fragmentToken = browser.Path[(fragmentIndex + "#presence=".Length)..];
                Assert.Equal(expectedHash, PresenceProofValidator.ComputeTokenHash(fragmentToken));

                var containerEnv = new Dictionary<string, object> { [IntoChatOSNames.MasterKeyEnvironmentVariable] = "ambient-mk", [DesktopBootstrap.PresenceTokenEnvVar] = "ambient-pt" };
                var containerCtx = new EnvironmentCallbackContext(builder.ExecutionContext, shellContainer, containerEnv, TestContext.Current.CancellationToken);
                foreach (var cb in shellContainer.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(containerCtx); }
                Assert.False(containerEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
                Assert.False(containerEnv.ContainsKey(DesktopBootstrap.PresenceTokenEnvVar));
            }
        }
        finally
        {
            tempRoot.Delete(recursive: true);
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
