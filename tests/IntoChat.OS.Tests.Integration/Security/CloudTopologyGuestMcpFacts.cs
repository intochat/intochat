using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.Cloud;
using IntoChat.Cloud.Blob;
using IntoChat.Cloud.Lobby;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.OS.Tests.Integration.Security;

// A /try guest reaches the shipped demo apps through three MCP tools on its own brain, and nothing else.
public sealed class CloudTopologyGuestMcpFacts(CloudTopologyGuestMcpFacts.CloudGuestHost host) : IClassFixture<CloudTopologyGuestMcpFacts.CloudGuestHost>
{
    private static readonly string[] GuestScreens = ["photocraft", "helloworld", "btcalert"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Guest_ListsOnlyTheShippedDemoApps()
    {
        var listed = await host.CallToolAsync("screen_list", new JsonObject(), Ct);
        Assert.False(listed.Refused, $"screen_list was refused: {listed.Text}");

        using var screens = JsonDocument.Parse(listed.Text);
        var appIds = screens.RootElement.EnumerateArray().Select(screen => screen.GetProperty("appId").GetString()!).ToArray();
        Assert.Contains("photocraft", appIds);
        Assert.DoesNotContain("creator", appIds);
        Assert.DoesNotContain("ino", appIds);
        Assert.DoesNotContain("settings", appIds);
        Assert.All(appIds, appId => Assert.Contains(appId, GuestScreens));
    }

    [Fact]
    public async Task Guest_ReadsAndPressesAShippedApp()
    {
        var read = await host.CallToolAsync("screen_read", new JsonObject { ["appId"] = "photocraft" }, Ct);
        Assert.False(read.Refused, $"screen_read photocraft was refused: {read.Text}");
        Assert.Contains("preset-warm", read.Text, StringComparison.Ordinal);

        var act = await host.CallToolAsync("screen_act", new JsonObject { ["appId"] = "photocraft", ["key"] = "preset-warm" }, Ct);
        Assert.False(act.Refused, $"screen_act photocraft was refused: {act.Text}");
        Assert.Contains("\"accepted\":true", act.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Guest_CannotReachAnotherBrain()
    {
        using var request = host.McpRequest("/brains/owner/mcp", "tools/list", new JsonObject());
        using var response = await host.Client.SendAsync(request, Ct);
        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403 for cross-brain MCP call, got {(int)response.StatusCode}.");
    }

    // Tool, then its arguments as JSON; {brain} is the guest's brain.
    public static TheoryData<string, string, string> ClosedMcpDoors => new()
    {
        {
            "ino makes an interactive app", "neurons_call",
            """{"contract":"IntoChat.OS.Modules.Apps.IInoStudio","key":"{brain}/ino/studio","method":"CreateInteractiveApp","arguments":"[\"hello.world\",\"Hello World\",\"Show a button that reveals Hello World when clicked\"]"}"""
        },
        {
            "ino makes a unit", "neurons_call",
            """{"contract":"IntoChat.OS.Modules.Apps.IInoStudio","key":"{brain}/ino/studio","method":"Make","arguments":"[\"Make a counter with one button\"]"}"""
        },
        {
            "studio deploys source", "neurons_call",
            """{"contract":"IntoChat.OS.Modules.Apps.ISynapseStudio","key":"{brain}/ino/synapses","method":"Deploy","arguments":"[\"guest.probe\",\"not a unit\"]"}"""
        },
        {
            "a build flow starts", "neurons_call",
            """{"contract":"IntoChat.OS.Modules.Apps.Flows.IFlows","key":"{brain}/flows/index","method":"Start","arguments":"[\"flow.build\",\"Guest probe\",null,\"{\\\"goal\\\":\\\"Say hello\\\"}\"]"}"""
        },
        { "neurons are listed", "neurons_list", "{}" },
        { "contracts are searched", "neurons_search", """{"query":"studio"}""" },
        { "a neuron is resolved", "neurons_get", """{"contract":"IntoChat.OS.Modules.Apps.IInoStudio","key":"{brain}/ino/studio"}""" },
        { "the caller context is read", "neurons_context", "{}" },
        { "an approval is answered", "screen_approve", """{"approvalId":"guest-probe","approved":false}""" },
        { "creator is read", "screen_read", """{"appId":"creator"}""" },
        { "settings is pressed", "screen_act", """{"appId":"settings","key":"guest-probe"}""" },
        { "ino is pressed", "screen_act", """{"appId":"ino","key":"guest-probe"}""" },
        { "a path is slipped past the screen list", "screen_read", """{"appId":"photocraft/../creator"}""" },
    };

    [Theory]
    [MemberData(nameof(ClosedMcpDoors))]
    public async Task Guest_IsRefused_AtEveryClosedMcpDoor(string door, string tool, string argumentsJson)
    {
        var arguments = JsonNode.Parse(argumentsJson.Replace("{brain}", host.BrainId, StringComparison.Ordinal))!.AsObject();
        // The tool exists on this server, so a misspelled name cannot pass as a refusal.
        Assert.Contains(tool, await host.ToolNamesAsync(Ct));

        var answer = await host.CallToolAsync(tool, arguments, Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"{door} ({tool}): refused={answer.Refused} {answer.Text}");
        Assert.True(answer.Refused, $"A guest reached a closed MCP door ({door}): {tool} answered {answer.Text}");
        Assert.DoesNotContain("\"ok\":true", answer.Text, StringComparison.OrdinalIgnoreCase);
        // A tool that answers with a result names the refusal, so a call that got through and failed later does not pass here.
        // The three that answer with data alone raise MCP's own tool error.
        if (tool is not ("neurons_list" or "neurons_search" or "neurons_context"))
        {
            Assert.Contains("A guest session cannot", answer.Text, StringComparison.Ordinal);
        }
    }

    // Method, then the route under /brains/{brain}/, then how the try token travels.
    public static TheoryData<string, string, string> ClosedHttpDoors
    {
        get
        {
            (string Method, string Route)[] doors =
            [
                ("POST", "apps/units/import-zip"),
                ("POST", "apps/packages/compile"),
                ("POST", "apps/import"),
                ("GET", "apps"),
                ("POST", "screens/approvals/guest-probe"),
                ("GET", "entries"),
                ("GET", "connections"),
                ("GET", "storage/local/roots"),
            ];
            var rows = new TheoryData<string, string, string>();
            foreach (var (method, route) in doors)
            {
                rows.Add(method, route, "header");
                rows.Add(method, route, "cookie");
            }
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(ClosedHttpDoors))]
    public async Task Guest_IsRefused_AtEveryClosedHttpDoor(string method, string route, string tokenForm)
    {
        using var request = host.GuestRequest(new HttpMethod(method), $"/brains/{host.BrainId}/{route}", cookie: tokenForm == "cookie");

        using var response = await host.Client.SendAsync(request, Ct);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"A guest reached {method} /brains/<try brain>/{route} ({tokenForm} token): {(int)response.StatusCode}.");
    }

    [Fact]
    public async Task NoBrainRoute_ExceptMcp_AdmitsAGuest()
    {
        var routes = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        var brainRoutes = routes.Where(UnderBrain).ToArray();
        Assert.NotEmpty(brainRoutes);

        // The marker first: nothing is sent while any route but MCP carries it, so a route opened by mistake is never exercised here.
        var doors = routes.Where(IsDoor).Select(route => route.RoutePattern.RawText).ToArray();
        var mcp = brainRoutes.Where(IsMcp).Select(route => route.RoutePattern.RawText).ToArray();
        Assert.NotEmpty(mcp);
        Assert.Equal(mcp.Order(StringComparer.Ordinal), doors.Order(StringComparer.Ordinal));

        // Refused means 401 or 403. Any other answer, a 4xx or 5xx from the route's own code included, came from behind the wall.
        var tried = 0;
        var notRefused = new List<string>();
        var answers = new SortedDictionary<int, int>();
        foreach (var route in brainRoutes.Where(route => !IsDoor(route)))
        {
            var path = PathOf(route.RoutePattern, host.BrainId);
            foreach (var method in route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["GET"])
            {
                tried++;
                using var request = host.GuestRequest(new HttpMethod(method), path, cookie: false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    answers[(int)response.StatusCode] = answers.GetValueOrDefault((int)response.StatusCode) + 1;
                    if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    {
                        notRefused.Add($"{method} {route.RoutePattern.RawText} -> {(int)response.StatusCode}");
                    }
                }
                catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
                {
                    notRefused.Add($"{method} {route.RoutePattern.RawText} -> no answer in 15s");
                }
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Tried {tried} requests on {brainRoutes.Length - mcp.Length} brain routes as a guest; {mcp.Length} MCP endpoints carry the door. "
            + "Answers: " + string.Join(", ", answers.Select(answer => $"{answer.Value} x {answer.Key}")));
        Assert.True(tried > 0);
        Assert.True(notRefused.Count == 0, "A guest was not refused on:\n" + string.Join('\n', notRefused));
    }

    // Cloud sign-in is the auth module's. The built-in password routes would hand any visitor an owner account beside the guest wall.
    [Fact]
    public async Task Visitor_CannotRegisterAnAccountByPassword()
    {
        var mapped = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(route => route.RoutePattern.RawText)
            .Where(route => route is "/identity/register" or "/identity/login")
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/identity/register")
        {
            Content = new StringContent("""{"principalId":"visitor-probe","password":"visitor-probe-password"}""", Encoding.UTF8, "application/json"),
        };
        using var response = await host.Client.SendAsync(request, Ct);

        TestContext.Current.TestOutputHelper?.WriteLine($"POST /identity/register, no token, no cookie: {(int)response.StatusCode}. Mapped password routes: {mapped.Length}.");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"), "An anonymous visitor was handed a session.");
        Assert.Empty(mapped);
    }

    private static bool IsDoor(RouteEndpoint route) => route.Metadata.GetMetadata<GuestSurface.Door>() is not null;

    private static bool UnderBrain(RouteEndpoint route)
        => route.RoutePattern.PathSegments is { Count: >= 2 } segments
           && segments[0].Parts is [RoutePatternLiteralPart { Content: "brains" }]
           && segments[1].Parts is [RoutePatternParameterPart];

    private static bool IsMcp(RouteEndpoint route)
        => route.RoutePattern.PathSegments is { Count: >= 3 } segments && segments[2].Parts is [RoutePatternLiteralPart { Content: "mcp" }];

    // The route with the guest's brain in its brain segment and "x" for every other route value.
    private static string PathOf(RoutePattern pattern, string brainId)
    {
        var path = new StringBuilder();
        for (var index = 0; index < pattern.PathSegments.Count; index++)
        {
            path.Append('/');
            foreach (var part in pattern.PathSegments[index].Parts)
            {
                path.Append(part switch
                {
                    RoutePatternLiteralPart literal => literal.Content,
                    RoutePatternSeparatorPart separator => separator.Content,
                    _ => index == 1 ? brainId : "x",
                });
            }
        }
        return path.ToString();
    }

    public sealed record ToolAnswer(bool Refused, string Text);

    // One cloud-registered host and one guest session for the class.
    public sealed class CloudGuestHost : IAsyncLifetime
    {
        private readonly string _homeDir = Path.Combine(Path.GetTempPath(), "intochat-cloud-try-it-" + Guid.NewGuid().ToString("N"));
        private IDisposable? _ports;
        private HttpClientHandler? _handler;
        private int _rpcId;

        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public string BrainId { get; private set; } = "";
        public string TryToken { get; private set; } = "";

        public async ValueTask InitializeAsync()
        {
            var ct = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(_homeDir);
            var ports = PortReservation.ReservePair();
            _ports = ports;

            var catalog = OperatingSystemModules.Discover(typeof(CloudHost).Assembly)
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
                [IntoChatOSNames.HomeKey] = _homeDir,
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

            App = builder.Build();
            App.MapIntoChatOSServer(beforeAccountSession: static web =>
            {
                if (web.Services.GetService<GuestTrySessionService>() is not null)
                {
                    web.UseGuestTrySessions();
                }
            });
            App.MapIntoChatOSMcp();
            await App.StartAsync(ct);

            _handler = new HttpClientHandler { UseCookies = false };
            Client = new HttpClient(_handler) { BaseAddress = new Uri(App.Urls.First(), UriKind.Absolute) };

            using var ready = await Client.GetAsync("/ready", ct);
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

            using var provision = await Client.PostAsync("/try/session", content: null, ct);
            Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
            var setCookie = provision.Headers.GetValues("Set-Cookie").FirstOrDefault(h => h.StartsWith(GuestTryToken.CookieName + "=", StringComparison.Ordinal));
            Assert.NotNull(setCookie);
            TryToken = setCookie[(GuestTryToken.CookieName.Length + 1)..].Split(';')[0];

            using var session = JsonDocument.Parse(await provision.Content.ReadAsStringAsync(ct));
            BrainId = session.RootElement.GetProperty("brainId").GetString()!;
            Assert.StartsWith("try-", BrainId, StringComparison.Ordinal);

            // The first admitted request gives the guest its brain.
            using var initialize = McpRequest($"/brains/{BrainId}/mcp", "initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-03-26",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "cloud-try-it", ["version"] = "1.0" },
            });
            using var initialized = await Client.SendAsync(initialize, ct);
            Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            _handler?.Dispose();
            if (App is not null)
            {
                try { await App.StopAsync(CancellationToken.None); }
                finally { await App.DisposeAsync(); }
            }
            _ports?.Dispose();
            try { Directory.Delete(_homeDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        public HttpRequestMessage GuestRequest(HttpMethod method, string path, bool cookie)
        {
            var request = new HttpRequestMessage(method, path);
            if (cookie) { request.Headers.Add("Cookie", $"{GuestTryToken.CookieName}={TryToken}"); }
            else { request.Headers.Add(GuestTryToken.HeaderName, TryToken); }
            if (method != HttpMethod.Get && method != HttpMethod.Delete && method != HttpMethod.Head)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }
            return request;
        }

        public HttpRequestMessage McpRequest(string path, string method, JsonObject parameters)
        {
            var request = GuestRequest(HttpMethod.Post, path, cookie: false);
            var rpc = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _rpcId), ["method"] = method, ["params"] = parameters };
            request.Content = new StringContent(rpc.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return request;
        }

        // The guest calls one tool on its own brain: the result text, and whether MCP answered with an error.
        public async Task<ToolAnswer> CallToolAsync(string tool, JsonObject arguments, CancellationToken ct)
        {
            using var request = McpRequest($"/brains/{BrainId}/mcp", "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments });
            using var response = await Client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var rpc = JsonDocument.Parse(RpcPayload(await response.Content.ReadAsStringAsync(ct)));
            if (rpc.RootElement.TryGetProperty("error", out var error)) { return new ToolAnswer(true, error.GetRawText()); }
            var result = rpc.RootElement.GetProperty("result");
            var text = string.Concat(result.GetProperty("content").EnumerateArray()
                .Select(block => block.TryGetProperty("text", out var value) ? value.GetString() : null));
            return new ToolAnswer(result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True, text);
        }

        // Every tool the server offers on the guest's brain.
        public async Task<string[]> ToolNamesAsync(CancellationToken ct)
        {
            using var request = McpRequest($"/brains/{BrainId}/mcp", "tools/list", new JsonObject());
            using var response = await Client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var rpc = JsonDocument.Parse(RpcPayload(await response.Content.ReadAsStringAsync(ct)));
            return [.. rpc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()!)];
        }

        // The JSON-RPC message of a streamable HTTP answer: the first data line of an event stream, else the body.
        private static string RpcPayload(string body)
        {
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("data:", StringComparison.Ordinal) && trimmed["data:".Length..].Trim() is { Length: > 0 } payload)
                {
                    return payload;
                }
            }
            return body;
        }
    }
}
