using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.Cloud.Blob;
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class CloudTopologyGuestMcpFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CloudTopology_StartsCleanly_ProvisionsGuestTrySession_AndConfinesMcpToGuestBrain()
    {
        var homeDir = Path.Combine(Path.GetTempPath(), "intochat-cloud-try-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(homeDir);
        using var ports = PortReservation.ReservePair();

        var catalog = OperatingSystemModules.Modules
            .Where(module => !module.RequiresContainer && (module.LoadsUnder("local") || module.LoadsUnder("cloud")) && module.Type != typeof(BlobStorageModule))
            .ToArray();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var masterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
            [IntoChatOSNames.HomeKey] = homeDir,
            [IntoChatOSNames.ShellEnabledKey] = "false",
            [IntoChatOSNames.FrontEnabledKey] = "false",
            [AuthOptions.PostureKey] = "Secured",
            ["IntoChatOS:Modules:auth:Options:Posture"] = "Secured",
            ["IntoChatOS:Modules:auth:Options:PublicBaseUrl"] = "https://os.test.intochat.io",
            ["IntoChatOS:MasterKey"] = masterKey,
            ["IntoChatOS:Platform:Storage:ConfiguredCustodyAcknowledged"] = "true",
            ["IntoChatOS:Connections:PublicOrigin"] = "https://os.test.intochat.io",
            ["IntoChatOS:Modules:auth:Secrets:auth-hashing"] = "0123456789abcdef0123456789abcdef0123456789abcdef",
            ["IntoChatOS:Modules:auth:Secrets:signin-issuer"] = "abcdef0123456789abcdef0123456789abcdef0123456789",
            ["IntoChatOS:Modules:lobby:Secrets:lobby-token-key"] = "0123456789abcdef0123456789abcdef0123456789abcdef",
            ["IntoChatOS:Modules:mail:Secrets:recipient-hashing"] = "abcdef0123456789abcdef0123456789abcdef0123456789",
            ["IntoChatOS:Modules:circle-server:Secrets:circle-deployment-key"] = "0123456789abcdef0123456789abcdef0123456789abcdef",
            ["IntoChatOS:Modules:web:Secrets:allowed-hosts"] = "api.github.com",
            ["IntoChatOS:Modules:artcraft:Enabled"] = "false",
            ["IntoChatOS:Modules:clickhouse:Enabled"] = "false",
            ["IntoChatOS:Modules:qdrant:Enabled"] = "false",
            ["IntoChatOS:Modules:supabase:Enabled"] = "false",
            [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
            [TopologyContext.GatewayPortKey] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture),
        });

        LocalRunDefaults.Apply(builder.Configuration);

        builder.AddIntoChatOSServer(server =>
        {
            server.AddOperatingSystem(catalog);
            server.UseLicenseCheck(new CloudLicenseCheck()).ConfigureServices(CloudHost.Register);
        });
        builder.Services.AddIntoChatOSMcp();

        await using var app = builder.Build();
        app.MapIntoChatOSServer();
        app.MapIntoChatOSMcp();
        await app.StartAsync(Ct);

        try
        {
            using var handler = new HttpClientHandler { UseCookies = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.First(), UriKind.Absolute) };

            using var ready = await client.GetAsync("/ready", Ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            using var provision = await client.PostAsync("/try/session", content: null, Ct);
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

            var setCookie = provision.Headers.GetValues("Set-Cookie").FirstOrDefault(h => h.StartsWith("intochat_try=", StringComparison.Ordinal));
            Assert.NotNull(setCookie);
            var tryToken = setCookie!["intochat_try=".Length..].Split(';')[0];

            using var sessionDoc = JsonDocument.Parse(await provision.Content.ReadAsStringAsync(Ct));
            var brainId = sessionDoc.RootElement.GetProperty("brainId").GetString()!;
            Assert.StartsWith("try-", brainId, StringComparison.Ordinal);

            // Cross-brain access to owner must fail closed under Secured posture
            using (var ownerReq = BuildMcpRequest("/brains/owner/mcp", tryToken, 1, "tools/list", new { }))
            using (var ownerResp = await client.SendAsync(ownerReq, Ct))
            {
                Assert.True(
                    ownerResp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"Expected 401 or 403 for cross-brain MCP call, got {(int)ownerResp.StatusCode}.");
            }

            // Guest brain MCP initialize + screen_list + screen_read must succeed
            using (var initReq = BuildMcpRequest($"/brains/{brainId}/mcp", tryToken, 2, "initialize", new
            {
                protocolVersion = "2025-03-26",
                capabilities = new { },
                clientInfo = new { name = "cloud-try-it", version = "1.0" },
            }))
            using (var initResp = await client.SendAsync(initReq, Ct))
            {
                Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
            }

            using var listReq = BuildMcpRequest($"/brains/{brainId}/mcp", tryToken, 3, "tools/call", new
            {
                name = "screen_list",
                arguments = new { },
            });
            using var listResp = await client.SendAsync(listReq, Ct);
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var listText = UnwrapMcpToolText(await ReadMcpJsonPayloadAsync(listResp, Ct));
            Assert.Contains("\"photocraft\"", listText, StringComparison.Ordinal);
            Assert.Contains("\"creator\"", listText, StringComparison.Ordinal);

            using var photoReq = BuildMcpRequest($"/brains/{brainId}/mcp", tryToken, 4, "tools/call", new
            {
                name = "screen_read",
                arguments = new { appId = "photocraft" },
            });
            using var photoResp = await client.SendAsync(photoReq, Ct);
            Assert.Equal(HttpStatusCode.OK, photoResp.StatusCode);
            var photoText = UnwrapMcpToolText(await ReadMcpJsonPayloadAsync(photoResp, Ct));
            Assert.Contains("preset-warm", photoText, StringComparison.Ordinal);

            using var actReq = BuildMcpRequest($"/brains/{brainId}/mcp", tryToken, 5, "tools/call", new
            {
                name = "screen_act",
                arguments = new { appId = "photocraft", key = "preset-warm" },
            });
            using var actResp = await client.SendAsync(actReq, Ct);
            Assert.Equal(HttpStatusCode.OK, actResp.StatusCode);
            var actText = UnwrapMcpToolText(await ReadMcpJsonPayloadAsync(actResp, Ct));
            Assert.Contains("\"accepted\":true", actText, StringComparison.OrdinalIgnoreCase);

            using var studioReq = BuildMcpRequest($"/brains/{brainId}/mcp", tryToken, 6, "tools/call", new
            {
                name = "neurons_call",
                arguments = new
                {
                    contract = "IntoChat.OS.Modules.Apps.IInoStudio",
                    key = $"{brainId}/ino/studio",
                    method = "CreateInteractiveApp",
                    arguments = "[\"hello.world\",\"Hello World\",\"Show a button that reveals Hello World when clicked\"]",
                },
            });
            using var studioResp = await client.SendAsync(studioReq, Ct);
            Assert.Equal(HttpStatusCode.OK, studioResp.StatusCode);
            var studioText = UnwrapMcpToolText(await ReadMcpJsonPayloadAsync(studioResp, Ct));
            Assert.Contains("\"ok\":true", studioText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"{brainId}/flow.build/", studioText, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(Ct);
            try { Directory.Delete(homeDir, recursive: true); } catch { }
        }
    }

    private static HttpRequestMessage BuildMcpRequest(string path, string tryToken, int id, string method, object @params)
    {
        var json = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }, JsonSerializerOptions.Web);
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-Try-Token", tryToken);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return req;
    }

    private static async Task<string> ReadMcpJsonPayloadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("data:", StringComparison.Ordinal))
            {
                var payload = trimmed["data:".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(payload))
                {
                    return payload;
                }
            }
        }

        return raw;
    }

    private static string UnwrapMcpToolText(string rpcJson)
    {
        using var doc = JsonDocument.Parse(rpcJson);
        Assert.False(doc.RootElement.TryGetProperty("error", out var err), $"MCP RPC error: {(err.ValueKind != JsonValueKind.Undefined ? err.GetRawText() : "")}");
        var result = doc.RootElement.GetProperty("result");
        if (result.TryGetProperty("isError", out var isErr) && isErr.ValueKind == JsonValueKind.True)
        {
            var errText = result.GetProperty("content")[0].GetProperty("text").GetString();
            Assert.Fail($"MCP tool returned isError=true: {errText}");
        }

        return result.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
    }
}
