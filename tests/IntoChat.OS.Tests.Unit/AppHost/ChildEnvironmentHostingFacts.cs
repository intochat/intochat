using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Flutter;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Microsoft.Aspire;
using IntoChat.OS.Modules.Microsoft.Aspire.Candidates;
using Microsoft.Extensions.Configuration;

namespace IntoChat.OS.AppHost.Tests.Unit;

public sealed class ChildEnvironmentHostingFacts
{
    private const string PresenceTokenEnvVar = "INTOCHAT_PRESENCE_TOKEN";

    [Fact]
    public async Task SystemProcessRunner_ScrubsAmbientMasterKeyAndPresenceTokenFromChildProcess()
    {
        var previousMasterKey = Environment.GetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable);
        var previousPresence = Environment.GetEnvironmentVariable(PresenceTokenEnvVar);
        var tempDir = Directory.CreateTempSubdirectory("intochat-p05-runner-");
        try
        {
            Environment.SetEnvironmentVariable(IntoChatOSNames.MasterKeyEnvironmentVariable, "leaked-host-master-key");
            Environment.SetEnvironmentVariable(PresenceTokenEnvVar, "leaked-host-presence-token");

            var runner = new SystemProcessRunner();
            var file = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh";
            string[] args = OperatingSystem.IsWindows()
                ? ["/d", "/c", $"echo MK=%{IntoChatOSNames.MasterKeyEnvironmentVariable}% PR=%{PresenceTokenEnvVar}% SAFE=%CANDIDATE_SAFE%"]
                : ["-c", $"printf 'MK=%s PR=%s SAFE=%s' \"${IntoChatOSNames.MasterKeyEnvironmentVariable}\" \"${PresenceTokenEnvVar}\" \"$CANDIDATE_SAFE\""];

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
            Environment.SetEnvironmentVariable(PresenceTokenEnvVar, previousPresence);
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
                [PresenceProof.TokenHashConfigurationKey] = "host-presence-hash",
                [PresenceTokenEnvVar] = "host-presence-secret",
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
        Assert.False(mcpEnv.ContainsKey(PresenceTokenEnvVar));
        Assert.False(mcpEnv.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));

        var siloEnv = host.Environment("abc1234", new Dictionary<string, string>(), "pg-pw", "ch-pw");
        Assert.True(siloEnv.TryGetValue(IntoChatOSNames.MasterKeyEnvironmentVariable, out var candidateKey));
        Assert.NotEqual("host-master-secret", candidateKey);
        Assert.False(siloEnv.ContainsKey(PresenceTokenEnvVar));
        Assert.False(siloEnv.ContainsKey(PresenceProof.TokenHashEnvironmentVariable));
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
                var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
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

                Assert.False(siloEnv.ContainsKey(PresenceTokenEnvVar));
                Assert.True(siloEnv.TryGetValue(PresenceProof.TokenHashEnvironmentVariable, out var hashObj));
                var expectedHash = Assert.IsType<string>(hashObj);

                var shellResource = Assert.Single(builder.Resources.OfType<ExecutableResource>(), r => r.Name == ShellNames.DefaultFlutterResourceName);
                var shellArgs = new List<object>();
                var argsCtx = new CommandLineArgsCallbackContext(shellArgs, shellResource, TestContext.Current.CancellationToken);
                foreach (var cb in shellResource.Annotations.OfType<CommandLineArgsCallbackAnnotation>()) { await cb.Callback(argsCtx); }

                var definePrefix = ShellHostingExtensions.PresenceDartDefinePrefix;
                var defineArg = Assert.Single(shellArgs.OfType<string>(), a => a.StartsWith(definePrefix, StringComparison.Ordinal));
                var shellToken = defineArg[definePrefix.Length..];
                Assert.Equal(expectedHash, PresenceProof.ComputeTokenHash(shellToken));

                var shellEnv = new Dictionary<string, object> { [IntoChatOSNames.MasterKeyEnvironmentVariable] = "ambient-mk", [PresenceTokenEnvVar] = "ambient-pt" };
                var shellCtx = new EnvironmentCallbackContext(builder.ExecutionContext, shellResource, shellEnv, TestContext.Current.CancellationToken);
                foreach (var cb in shellResource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(shellCtx); }
                Assert.False(shellEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
                Assert.False(shellEnv.ContainsKey(PresenceTokenEnvVar));
            }

            // Web mode check
            {
                var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
                var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
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

                Assert.True(siloEnv.TryGetValue(PresenceProof.TokenHashEnvironmentVariable, out var hashObj));
                var expectedHash = Assert.IsType<string>(hashObj);

                var shellResource = Assert.Single(builder.Resources, r => r.Name == ShellNames.DefaultFlutterResourceName);
                var browser = Assert.Single(shellResource.Annotations.OfType<BrainBrowserAnnotation>());
                var fragmentIndex = browser.Path.IndexOf("#presence=", StringComparison.Ordinal);
                Assert.True(fragmentIndex > 0, $"Expected #presence= fragment in browser path '{browser.Path}'.");
                var fragmentToken = browser.Path[(fragmentIndex + "#presence=".Length)..];
                Assert.Equal(expectedHash, PresenceProof.ComputeTokenHash(fragmentToken));
            }

            // WebContainer mode check
            {
                var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
                var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
                    .WithHosting<FlutterModuleHosting, FlutterModuleOptions>(o =>
                    {
                        o.RunWebApp();
                        o.Hosting = o.Hosting with { WorkingDirectory = webDir, WebContainer = true };
                    });

                var shellContainer = Assert.Single(builder.Resources.OfType<ContainerResource>(), r => r.Name == ShellNames.DefaultFlutterResourceName);
                try
                {
                    var silo = builder.AddExecutable("OperatingSystem-a", "unused", ".")
                        .WithHttpEndpoint(name: ShellNames.HttpEndpointName, targetPort: 5080)
                        .WithReference(brain);

                    var siloEnv = new Dictionary<string, object>();
                    var siloCtx = new EnvironmentCallbackContext(builder.ExecutionContext, silo.Resource, siloEnv, TestContext.Current.CancellationToken);
                    foreach (var cb in silo.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(siloCtx); }

                    Assert.True(siloEnv.TryGetValue(PresenceProof.TokenHashEnvironmentVariable, out var hashObj));
                    var expectedHash = Assert.IsType<string>(hashObj);

                    var browser = Assert.Single(shellContainer.Annotations.OfType<BrainBrowserAnnotation>());
                    var fragmentIndex = browser.Path.IndexOf("#presence=", StringComparison.Ordinal);
                    Assert.True(fragmentIndex > 0, $"Expected #presence= fragment in container browser path '{browser.Path}'.");
                    var fragmentToken = browser.Path[(fragmentIndex + "#presence=".Length)..];
                    Assert.Equal(expectedHash, PresenceProof.ComputeTokenHash(fragmentToken));

                    var containerEnv = new Dictionary<string, object> { [IntoChatOSNames.MasterKeyEnvironmentVariable] = "ambient-mk", [PresenceTokenEnvVar] = "ambient-pt" };
                    var containerCtx = new EnvironmentCallbackContext(builder.ExecutionContext, shellContainer, containerEnv, TestContext.Current.CancellationToken);
                    foreach (var cb in shellContainer.Annotations.OfType<EnvironmentCallbackAnnotation>()) { await cb.Callback(containerCtx); }
                    Assert.False(containerEnv.ContainsKey(IntoChatOSNames.MasterKeyEnvironmentVariable));
                    Assert.False(containerEnv.ContainsKey(PresenceTokenEnvVar));
                }
                finally
                {
                    foreach (var mount in shellContainer.Annotations.OfType<ContainerMountAnnotation>())
                    {
                        if (!string.IsNullOrWhiteSpace(mount.Source) && Path.GetDirectoryName(mount.Source) is { } mountDir)
                        {
                            DigitalBrain.Testing.TempDirectory.DeleteIfPresent(mountDir);
                        }
                    }
                }
            }
        }
        finally
        {
            DigitalBrain.Testing.TempDirectory.DeleteIfPresent(tempRoot.FullName);
        }
    }
}
