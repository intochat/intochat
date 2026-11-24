using System.Buffers.Text;
using System.Net;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using IntoChat.OS.Aspire.Hosting;
using IntoChat.OS.Modules.AI;
using IntoChat.OS.Modules.AI.Aspire.Hosting;
using IntoChat.OS.Modules.AI.Models;
using IntoChat.OS.Modules.Devices.Aspire.Hosting;
using IntoChat.OS.Modules.Flutter.Aspire.Hosting;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Aspire.Hosting.Tests.Unit;

public sealed class DevicesHostingFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Dictionary<string, object>> EvaluateEnvironmentAsync(
        IDistributedApplicationBuilder builder,
        IResource resource)
    {
        var environment = new Dictionary<string, object>(StringComparer.Ordinal);
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource, environment, Ct);
        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }
        return environment;
    }

    [Fact]
    public void TheKernelHasAnInstallModelCommand()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions());
        var kernel = builder.AddExecutable("OperatingSystem", "unused", ".").WithReference(brain);

        var command = Assert.Single(
            kernel.Resource.Annotations.OfType<ResourceCommandAnnotation>(),
            static a => a.Name == DevicesHostingExtensions.InstallModelCommandName);
        Assert.Equal("Install model", command.DisplayName);
        Assert.Equal("ArrowDownload", command.IconName);
    }

    [Fact]
    public async Task AHearCardBecomesAPrefetchEntry()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var ai = new AIOptions().WithRole(ModelRole.Hear, KnownModels.WhisperLargeV3Turbo);
        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
            .WithHosting<AIModuleHosting, AIOptions>(ai);
        var kernel = builder.AddExecutable("OperatingSystem", "unused", ".").WithReference(brain);

        var env = await EvaluateEnvironmentAsync(builder, kernel.Resource);

        Assert.True(env.TryGetValue("IntoChatOS__Modules__devices__Options__Prefetch__0", out var value));
        Assert.Equal(KnownModels.WhisperLargeV3Turbo.Id.Value, value?.ToString());
    }

    [Fact]
    public void NoFoundryDownloadResourceRemains()
    {
        Assert.Null(typeof(AIModuleHosting).Assembly.GetType("IntoChat.OS.Modules.AI.Aspire.Hosting.FoundryLocalModelResource"));
        Assert.DoesNotContain(
            typeof(AIModuleHosting).Assembly.GetReferencedAssemblies(),
            static a => a.Name!.StartsWith("Microsoft.AI.Foundry", StringComparison.Ordinal));

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var ai = new AIOptions().WithRole(ModelRole.Hear, KnownModels.WhisperLargeV3Turbo);
        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
            .WithHosting<AIModuleHosting, AIOptions>(ai);
        _ = builder.AddExecutable("OperatingSystem", "unused", ".").WithReference(brain);

        Assert.DoesNotContain(
            builder.Resources,
            static r => r.GetType().Name.Contains("Foundry", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OllamaEndpointGoesToDevices()
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var ai = new AIOptions().WithModel(KnownModels.Qwen35);
        var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
            .WithHosting<AIModuleHosting, AIOptions>(ai);
        var kernel = builder.AddExecutable("OperatingSystem", "unused", ".").WithReference(brain);

        var env = await EvaluateEnvironmentAsync(builder, kernel.Resource);

        Assert.True(env.ContainsKey("IntoChatOS__Modules__devices__Options__Ollama__Endpoint"));
        Assert.False(env.ContainsKey("IntoChatOS__Modules__ai__Options__Ollama__Endpoint"));
    }

    [Fact]
    public async Task ForwardInstallQueriesThisDeviceAndPostsModelWithPresenceToken()
    {
        var requests = new List<(HttpMethod Method, string Path, string? Presence, string? Body)>();
        using var handler = new StubHandler(async req =>
        {
            var presence = req.Headers.TryGetValues(PresenceProof.HeaderName, out var vals) ? vals.FirstOrDefault() : null;
            var body = req.Content is not null ? await req.Content.ReadAsStringAsync(Ct) : null;
            requests.Add((req.Method, req.RequestUri!.AbsolutePath, presence, body));

            if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath == "/brains/owner/devices/this")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"machineId":"machine-42"}""", Encoding.UTF8, "application/json"),
                };
            }

            if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath == "/brains/owner/devices/machine-42/install")
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5080") };
        var result = await DevicesHostingExtensions.ForwardInstallAsync(http, "gemma4:e2b", "presence-secret-token", Ct);

        Assert.True(result.Success);
        Assert.Equal(2, requests.Count);
        Assert.Equal((HttpMethod.Get, "/brains/owner/devices/this", "presence-secret-token"), (requests[0].Method, requests[0].Path, requests[0].Presence));
        Assert.Equal((HttpMethod.Post, "/brains/owner/devices/machine-42/install", "presence-secret-token"), (requests[1].Method, requests[1].Path, requests[1].Presence));
        Assert.Equal("""{"model":"gemma4:e2b"}""", requests[1].Body);
    }

    [Fact]
    public void WithShellFrontWithoutShellHostReturnsNullPresenceToken()
    {
        var tempHome = Directory.CreateTempSubdirectory("intochat-devices-presence-").FullName;
        try
        {
            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
            builder.Configuration["IntoChatOS:Home"] = tempHome;
            var front = builder.AddExecutable("front", "unused", ".").WithHttpEndpoint(name: "http");
            var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
                .WithShellFront(front);

            Assert.Null(ShellHostingExtensions.TryGetPresenceToken(brain));
            Assert.Null(DevicesHostingExtensions.ResolvePresenceToken(brain));
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void ResolvePresenceTokenReadsRaw32BytePresenceFileAsBase64Url()
    {
        var tempHome = Directory.CreateTempSubdirectory("intochat-devices-presence-").FullName;
        try
        {
            var runDir = Path.Combine(tempHome, "run");
            Directory.CreateDirectory(runDir);
            var rawBytes = new byte[PresenceProof.SecretByteCount];
            for (var i = 0; i < rawBytes.Length; i++)
            {
                rawBytes[i] = (byte)(i * 7 + 0x80);
            }
            File.WriteAllBytes(Path.Combine(runDir, "presence"), rawBytes);

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
            builder.Configuration["IntoChatOS:Home"] = tempHome;
            var front = builder.AddExecutable("front", "unused", ".").WithHttpEndpoint(name: "http");
            var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions())
                .WithShellFront(front);

            Assert.Null(ShellHostingExtensions.TryGetPresenceToken(brain));
            var token = DevicesHostingExtensions.ResolvePresenceToken(brain);
            Assert.Equal(Base64Url.EncodeToString(rawBytes), token);
            Assert.Equal(PresenceProof.ComputeTokenHash(rawBytes), PresenceProof.ComputeTokenHash(token!));
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    [Fact]
    public void ResolvePresenceTokenReadsPrintableAsciiPresenceFileTrimmed()
    {
        var tempHome = Directory.CreateTempSubdirectory("intochat-devices-presence-").FullName;
        try
        {
            var runDir = Path.Combine(tempHome, "run");
            Directory.CreateDirectory(runDir);
            const string expected = "local-ascii-presence-secret-0123456789";
            File.WriteAllText(Path.Combine(runDir, "presence"), $"{expected}\r\n", Encoding.ASCII);

            var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
            builder.Configuration["IntoChatOS:Home"] = tempHome;
            var brain = builder.AddIntoChatOSBrain("modules", new IntoChatOSHostingOptions());

            Assert.Equal(expected, DevicesHostingExtensions.ResolvePresenceToken(brain));
        }
        finally
        {
            Directory.Delete(tempHome, recursive: true);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request);
    }
}
