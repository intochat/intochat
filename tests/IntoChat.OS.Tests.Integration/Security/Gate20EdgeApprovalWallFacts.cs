using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities;
using DigitalBrain.Confirmation;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using DigitalBrain.Runtime.Orleans.Testing;
using IntoChat.OS.Kernel;
using IntoChat.OS.Kernel.AspireServer;
using IntoChat.OS.Kernel.Mcp;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using IntoChat.OS.Host.Configuration;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class Gate20EdgeApprovalWallFacts(Gate20EdgeApprovalWallFacts.EdgeKernelFixture fixture)
    : IClassFixture<Gate20EdgeApprovalWallFacts.EdgeKernelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalHost_BootsWithClosedUnstampedEdge_DisablesDashboardByDefault_AndSignsInWithout500()
    {
        var host = fixture.Host;

        // B04: an unstamped edge is never the kernel; AccessOptions is gone and the gate refuses a call with no caller (task 1).

        // B04: Orleans Dashboard is gated off by default unless explicitly enabled
        var serverBuilder = host.Services.GetRequiredService<IntoChatOSServerBuilder>();
        Assert.False(serverBuilder.Dashboard);

        // B04: Direct unstamped grain/neuron call with cleared caller provenance is rejected
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var inbox = proxies.Get<IConfirmationInbox>(IConfirmationInbox.Key(host.Scope));
        Orleans.Runtime.RequestContext.Clear();
        Assert.False(Edge(host.Services).IsKernelActive);
        Assert.Null(Edge(host.Services).Current);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(inbox.Read);

        // Unproved loopback calls fail closed at the edge with 401 (no "local" fallback)
        using (var unprovedRes = await host.Client.GetAsync($"/brains/{host.Scope}/marketplace", Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unprovedRes.StatusCode);
        }

        // Internal SiloNeurons hops (e.g. MarketplaceView.List) remain healthy while UnstampedEdgeIsKernel is false
        using var marketplaceReq = new HttpRequestMessage(HttpMethod.Get, $"/brains/{host.Scope}/marketplace");
        marketplaceReq.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
        using var marketplaceRes = await host.Client.SendAsync(marketplaceReq, Ct);
        Assert.Equal(HttpStatusCode.OK, marketplaceRes.StatusCode);

        // B02: POST /identity/register and POST /identity/login succeed with 200 OK (never 500 PrivilegeDenied)
        using var aliceClient = new HttpClient { BaseAddress = host.BaseAddress };
        using var registerRes = await aliceClient.PostAsJsonAsync(
            "/identity/register",
            new { principalId = "alice", password = "correct-horse-battery-staple", displayName = "Alice" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, registerRes.StatusCode);

        using var loginRes = await aliceClient.PostAsJsonAsync(
            "/identity/login",
            new { principalId = "alice", password = "correct-horse-battery-staple" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);

        // Cross-brain isolation holds after alice signs in
        using var crossBrainRes = await aliceClient.GetAsync($"/brains/{host.Scope}/marketplace", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, crossBrainRes.StatusCode);
    }

    // The brain MCP endpoint is also the one door a /try guest has. Anyone who is not a guest keeps every tool and every screen.
    [Fact]
    public async Task LocalOwner_OverBrainMcp_KeepsEveryToolAndEveryScreen()
    {
        var host = fixture.Host;
        using var mcpHttp = new HttpClient { BaseAddress = host.BaseAddress };
        mcpHttp.DefaultRequestHeaders.Add(PresenceProof.HeaderName, host.PresenceToken);
        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.BaseAddress, $"/brains/{host.Scope}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }, mcpHttp, ownsHttpClient: false),
            cancellationToken: Ct);

        var listed = await mcpClient.CallToolAsync("screen_list", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.NotEqual(true, listed.IsError);
        Assert.Contains("\"creator\"", ExtractText(listed), StringComparison.Ordinal);
        Assert.Contains("\"settings\"", ExtractText(listed), StringComparison.Ordinal);

        var read = await mcpClient.CallToolAsync("screen_read", new Dictionary<string, object?> { ["appId"] = "creator" }, cancellationToken: Ct);
        Assert.NotEqual(true, read.IsError);
        Assert.Contains("\"appId\":\"creator\"", ExtractText(read), StringComparison.Ordinal);

        var context = await mcpClient.CallToolAsync("neurons_context", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.NotEqual(true, context.IsError);
        // MCP describes its caller by actor now: the agent acting for the session's person (task 6, McpCaller.Describe).
        Assert.Contains("\"actor\":\"Agent\"", ExtractText(context), StringComparison.Ordinal);

        var neurons = await mcpClient.CallToolAsync("neurons_list", new Dictionary<string, object?>(), cancellationToken: Ct);
        Assert.NotEqual(true, neurons.IsError);
    }

    [Fact]
    public async Task Gate20_RequiresPersonConfirmationPlan_ResistsMcpLoopbackForgedPresenceAndAgent_ThenApprovesWithPresenceProof()
    {
        var host = fixture.Host;
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var scope = host.Scope;
        var planId = $"gate20:{Guid.NewGuid():N}";

        var plan = new ConfirmationPlan(
            planId,
            ConfirmationAction.Other,
            Destructive: false,
            Title: "Gate 2.0 PR approval",
            Body: "Promote verified branch to draft PR",
            ApproveLabel: "Promote PR",
            DenyLabel: "Reject",
            TargetLabel: "intochat/os")
        {
            Expiry = TimeSpan.FromMinutes(4),
            RequiresPerson = true,
        };

        // Raise a waiting RequiresPerson ConfirmationPlan from an app/flow context (non-person raiser so Request parks it Pending!)
        var requestsId = IConfirmationRequests.Of(new BrainId(scope));
        await RaisePendingPlanAsync(host.Services, scope, requestsId, plan);

        var inboxKey = IConfirmationInbox.Key(scope);
        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 1: Forged X-IntoChat-Presence header -> rejected at HTTP edge with 401 Unauthorized
        // -----------------------------------------------------------------------------------------
        using (var forgedReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/confirmations/{Uri.EscapeDataString(planId)}")
        {
            Content = JsonContent.Create(new { approve = true }),
        })
        {
            forgedReq.Headers.Add(PresenceProof.HeaderName, "forged-token-value-12345678901234567890");
            using var forgedRes = await host.Client.SendAsync(forgedReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedRes.StatusCode);
        }

        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 2: Unproved loopback HTTP caller (no X-IntoChat-Presence -> rejected 401 at edge)
        // -----------------------------------------------------------------------------------------
        using (var unprovedEventRes = await host.Client.PostAsJsonAsync(
            $"/brains/{scope}/confirmations/{Uri.EscapeDataString(planId)}",
            new { approve = true },
            Ct))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unprovedEventRes.StatusCode);
        }

        // -----------------------------------------------------------------------------------------
        // Attack 2b: Another brain's person, signed in, answering this brain's plan -> 403 at the membership filter
        // -----------------------------------------------------------------------------------------
        var stranger = $"gate20-stranger-{Guid.NewGuid():N}";
        using (var strangerClient = new HttpClient { BaseAddress = host.BaseAddress })
        {
            using (var registered = await strangerClient.PostAsJsonAsync(
                "/identity/register",
                new { principalId = stranger, password = "correct-horse-battery-staple", displayName = "Stranger" },
                Ct))
            {
                Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            }
            using (var signedIn = await strangerClient.PostAsJsonAsync(
                "/identity/login",
                new { principalId = stranger, password = "correct-horse-battery-staple" },
                Ct))
            {
                Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
            }
            using var strangerRes = await strangerClient.PostAsJsonAsync(
                $"/brains/{scope}/confirmations/{Uri.EscapeDataString(planId)}",
                new { approve = true },
                Ct);
            Assert.Equal(HttpStatusCode.Forbidden, strangerRes.StatusCode);
        }

        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 3: /mcp caller (EVEN WHEN HTTP transport carries valid X-IntoChat-Presence!)
        // McpCaller ALWAYS stamps Actor.Agent (AppId = "assistant", ConversationId = "mcp").
        // No MCP tool answers a confirmation; neurons_call on Resolve + Withdraw is refused.
        // -----------------------------------------------------------------------------------------
        using var mcpHttp = new HttpClient { BaseAddress = host.BaseAddress };
        mcpHttp.DefaultRequestHeaders.Add(PresenceProof.HeaderName, host.PresenceToken);
        await using (var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }, mcpHttp, ownsHttpClient: false),
            cancellationToken: Ct))
        {
            var tools = await mcpClient.ListToolsAsync(cancellationToken: Ct);
            Assert.DoesNotContain(tools, tool => tool.Name.Contains("approve", StringComparison.OrdinalIgnoreCase));

            var mcpDirectResolve = await mcpClient.CallToolAsync(
                "neurons_call",
                new Dictionary<string, object?>
                {
                    ["contract"] = typeof(IConfirmationInbox).FullName!,
                    ["key"] = inboxKey,
                    ["method"] = nameof(IConfirmationInbox.Resolve),
                    ["arguments"] = $"[\"{planId}\", true]",
                },
                cancellationToken: Ct);
            Assert.True(mcpDirectResolve.IsError == true, $"Expected MCP neurons_call Resolve to fail, got: {ExtractText(mcpDirectResolve)}");

            var mcpDirectWithdraw = await mcpClient.CallToolAsync(
                "neurons_call",
                new Dictionary<string, object?>
                {
                    ["contract"] = typeof(IConfirmationRequests).FullName!,
                    ["key"] = $"{scope}/{IConfirmations.ConfirmationsNeuronKey}",
                    ["method"] = nameof(IConfirmationRequests.Withdraw),
                    ["arguments"] = $"[\"{planId}\"]",
                },
                cancellationToken: Ct);
            Assert.True(mcpDirectWithdraw.IsError == true, $"Expected MCP neurons_call Withdraw to fail, got: {ExtractText(mcpDirectWithdraw)}");
        }

        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 4: In-process Actor.Agent caller attempting Resolve(true), Resolve(false), Withdraw
        // -----------------------------------------------------------------------------------------
        var agentCaller = Edge(host.Services).Agent(new Actor.Person(AccountSession.DefaultLogin, AccountSession.DefaultLogin), new BrainId(scope), conversation: "ino-turn");

        using (Edge(host.Services).EnterInProcess())
        using (Edge(host.Services).Enter(agentCaller))
        {
            var inbox = proxies.Get<IConfirmationInbox>(inboxKey);
            var requests = proxies.Get<IConfirmationRequests>(requestsId);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.Resolve(planId, true));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.Resolve(planId, false));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => requests.Withdraw(planId));
        }

        // Verify the plan is STILL Pending and Outcome is STILL null!
        Assert.True(await IsPendingAsync(host.Services, scope, planId));
        using (Edge(host.Services).EnterKernel(new BrainId(scope)))
        {
            Assert.Null(await proxies.Get<IConfirmationRequests>(requestsId).Outcome(planId));
        }

        // -----------------------------------------------------------------------------------------
        // Legitimate Person Approval: HTTP caller with valid X-IntoChat-Presence approves in 1 call!
        // -----------------------------------------------------------------------------------------
        using (var personReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/confirmations/{Uri.EscapeDataString(planId)}")
        {
            Content = JsonContent.Create(new { approve = true }),
        })
        {
            personReq.Headers.Add(PresenceProof.HeaderName, host.PresenceToken);
            using var personRes = await host.Client.SendAsync(personReq, Ct);
            Assert.Equal(HttpStatusCode.OK, personRes.StatusCode);
        }

        using (Edge(host.Services).EnterKernel(new BrainId(scope)))
        {
            var resolved = await proxies.Get<IConfirmationRequests>(requestsId).Outcome(planId);
            Assert.NotNull(resolved);
            Assert.True(resolved.Approved);
            Assert.Equal(ConfirmationOutcome.Approved, resolved.Outcome);
        }

        Assert.False(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Local Web (#presence= -> POST /auth/local-presence -> IntoChatPresence SameSite=Strict cookie):
        // Cross-origin cookie attempt fails with 401; same-origin cookie approves in 1 call!
        // -----------------------------------------------------------------------------------------
        var webPlanId = $"gate20-web:{Guid.NewGuid():N}";
        var webPlan = new ConfirmationPlan(
            webPlanId,
            ConfirmationAction.Other,
            Destructive: false,
            Title: "Gate 2.0 Web Cookie PR approval",
            Body: "Promote verified branch from local web shell",
            ApproveLabel: "Promote PR",
            DenyLabel: "Reject",
            TargetLabel: "intochat/os")
        {
            Expiry = TimeSpan.FromMinutes(4),
            RequiresPerson = true,
        };

        await RaisePendingPlanAsync(host.Services, scope, requestsId, webPlan);
        Assert.True(await IsPendingAsync(host.Services, scope, webPlanId));

        using var rawNoCookiesHandler = new HttpClientHandler { UseCookies = false };
        using var rawClient = new HttpClient(rawNoCookiesHandler) { BaseAddress = host.BaseAddress };

        string cookiePair;
        using (var exchangeReq = new HttpRequestMessage(HttpMethod.Post, "/auth/local-presence")
        {
            Content = JsonContent.Create(new { token = host.PresenceToken }),
        })
        {
            exchangeReq.Headers.Add("Origin", host.BaseAddress.GetLeftPart(UriPartial.Authority));
            using var exchangeRes = await rawClient.SendAsync(exchangeReq, Ct);
            Assert.Equal(HttpStatusCode.OK, exchangeRes.StatusCode);
            var setCookie = Assert.Single(exchangeRes.Headers.GetValues("Set-Cookie"));
            Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
            cookiePair = setCookie.Split(';', 2)[0];
        }

        using (var crossOriginCookieReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/confirmations/{Uri.EscapeDataString(webPlanId)}")
        {
            Content = JsonContent.Create(new { approve = true }),
        })
        {
            crossOriginCookieReq.Headers.Add("Cookie", cookiePair);
            crossOriginCookieReq.Headers.Add("Origin", "https://evil.example");
            using var crossOriginRes = await rawClient.SendAsync(crossOriginCookieReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, crossOriginRes.StatusCode);
        }

        Assert.True(await IsPendingAsync(host.Services, scope, webPlanId));

        using (var sameOriginCookieReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/confirmations/{Uri.EscapeDataString(webPlanId)}")
        {
            Content = JsonContent.Create(new { approve = true }),
        })
        {
            sameOriginCookieReq.Headers.Add("Cookie", cookiePair);
            sameOriginCookieReq.Headers.Add("Origin", host.BaseAddress.GetLeftPart(UriPartial.Authority));
            using var sameOriginRes = await rawClient.SendAsync(sameOriginCookieReq, Ct);
            Assert.Equal(HttpStatusCode.OK, sameOriginRes.StatusCode);
        }

        using (Edge(host.Services).EnterKernel(new BrainId(scope)))
        {
            var webResolved = await proxies.Get<IConfirmationRequests>(requestsId).Outcome(webPlanId);
            Assert.NotNull(webResolved);
            Assert.True(webResolved.Approved);
            Assert.Equal(ConfirmationOutcome.Approved, webResolved.Outcome);
        }
    }

    private static EdgeAuthority Edge(IServiceProvider services) => services.GetRequiredService<EdgeAuthority>();

    private static async Task RaisePendingPlanAsync(
        IServiceProvider services,
        string scope,
        NeuronId requestsId,
        ConfirmationPlan plan)
    {
        var raiserCaller = Edge(services).Scheduler(new BrainId(scope));

        using var _ = Edge(services).EnterInProcess();
        using var __ = Edge(services).Enter(raiserCaller);
        var requests = services.GetRequiredService<INeuronProxies>().Get<IConfirmationRequests>(requestsId);
        var immediate = await requests.Request(plan);
        Assert.Null(immediate);
    }

    private static string ExtractText(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<bool> IsPendingAsync(IServiceProvider services, string scope, string planId)
    {
        using var _ = Edge(services).EnterKernel(new BrainId(scope));
        var inbox = services.GetRequiredService<INeuronProxies>()
            .Get<IConfirmationInbox>(IConfirmationInbox.Key(scope));
        var state = await inbox.Read();
        return state.Pending.Any(p => string.Equals(p.PlanId, planId, StringComparison.Ordinal));
    }

    internal sealed class EdgeKernelHarness : IAsyncDisposable
    {
        // How long a started host may take to answer /ready. Generous: a whole OS host starts here, often beside other test
        // hosts on a loaded runner, and nothing here measures how fast it starts.
        private static readonly TimeSpan ReadyBudget = TimeSpan.FromSeconds(120);

        private readonly WebApplication _app;
        private readonly IDisposable _ports;
        private readonly string _homeDir;

        private EdgeKernelHarness(WebApplication app, HttpClient client, IDisposable ports, string homeDir, string scope, string presenceToken, int gatewayPort)
        {
            GatewayPort = gatewayPort;
            _app = app;
            Client = client;
            _ports = ports;
            _homeDir = homeDir;
            Scope = scope;
            PresenceToken = presenceToken;
        }

        public HttpClient Client { get; }
        public Uri BaseAddress => Client.BaseAddress!;
        public IServiceProvider Services => _app.Services;
        public string Scope { get; }
        public string PresenceToken { get; }

        // The loopback port reserved for the silo's gateway. The gateway listens there only when the harness was started with
        // openGateway (a fact that plays a cluster client connects here, to this harness only); otherwise nothing does, as on
        // every OS host.
        public int GatewayPort { get; }

        // configure runs last on the host builder: a fact replaces a host service there (the model, for one).
        public static async Task<EdgeKernelHarness> StartAsync(CancellationToken ct, Action<WebApplicationBuilder>? configure = null, bool openGateway = false)
        {
            var homeDir = Path.Combine(Path.GetTempPath(), "intochat-gate20-it-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(homeDir, "run"));
            var rawPresence = RandomNumberGenerator.GetBytes(32);
            await File.WriteAllBytesAsync(Path.Combine(homeDir, "run", "presence"), rawPresence, ct);

            var ports = PortReservation.ReservePair();
            WebApplication? app = null;
            HttpClient? client = null;
            try
            {
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions
                {
                    EnvironmentName = "Development",
                    ApplicationName = typeof(OperatingSystemModules).Assembly.GetName().Name,
                    ContentRootPath = Path.GetTempPath(),
                });
                builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
                builder.WebHost.UseUrls("http://127.0.0.1:0");

                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    [IntoChatOSNames.TopologyKey] = IntoChatOSNames.TopologyLocal,
                    [IntoChatOSNames.HomeKey] = homeDir,
                    [IntoChatOSNames.ShellEnabledKey] = "false",
                    [IntoChatOSNames.FrontEnabledKey] = "false",
                    [AuthKeys.Posture] = "Open",
                    [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
                });
                if (openGateway)
                {
                    builder.Configuration[TopologyContext.GatewayPortKey] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture);
                }

                LocalRunDefaults.Apply(builder.Configuration);
                builder.AddIntoChatOSServer(server => server.AddOperatingSystem(OperatingSystemModules.InProcessModules));
                builder.Services.AddIntoChatOSMcp();
                configure?.Invoke(builder);

                app = builder.Build();
                app.MapIntoChatOSServer();
                app.MapIntoChatOSMcp();
                await app.StartAsync(ct);

                var baseUrl = app.Urls.First();
                client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
                var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
                var proof = app.Services.GetRequiredService<IPresenceProof>();

                var deadline = DateTimeOffset.UtcNow + ReadyBudget;
                var ready = false;
                while (DateTimeOffset.UtcNow < deadline)
                {
                    try
                    {
                        using var res = await client.GetAsync("/ready", ct);
                        if (res.IsSuccessStatusCode)
                        {
                            ready = true;
                            break;
                        }
                    }
                    catch (HttpRequestException)
                    {
                    }

                    await Task.Delay(100, ct);
                }

                if (!ready)
                {
                    throw new TimeoutException($"EdgeKernelHarness at '{baseUrl}' did not become ready within {ReadyBudget}.");
                }

                return new EdgeKernelHarness(app, client, ports, homeDir, scope, proof.Token, ports.GatewayPort);
            }
            catch
            {
                client?.Dispose();
                if (app is not null)
                {
                    try
                    {
                        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await app.StopAsync(stopCts.Token);
                    }
                    catch
                    {
                        // Startup failed partway; best-effort stop before disposal so Orleans marks the silo Dead in membership.
                    }

                    await app.DisposeAsync();
                }

                ports.Dispose();
                TryDeleteDirectory(homeDir);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Client.Dispose();
                try
                {
                    await _app.StopAsync();
                }
                finally
                {
                    await _app.DisposeAsync();
                }
            }
            finally
            {
                _ports.Dispose();
                TryDeleteDirectory(_homeDir);
            }
        }

        private static void TryDeleteDirectory(string path)
            => DigitalBrain.Testing.TempDirectory.DeleteIfPresent(path);
    }

    public sealed class EdgeKernelFixture : IAsyncLifetime
    {
        internal EdgeKernelHarness Host { get; private set; } = null!;

        public async ValueTask InitializeAsync()
            => Host = await EdgeKernelHarness.StartAsync(CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            if (Host is not null)
            {
                await Host.DisposeAsync();
            }
        }
    }
}
