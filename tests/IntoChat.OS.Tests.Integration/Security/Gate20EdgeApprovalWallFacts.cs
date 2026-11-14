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
using IntoChat.OS.Aspire.Server;
using IntoChat.OS.Authority.Identity;
using IntoChat.OS.Connections.Identity.Configuration;
using IntoChat.OS.Contracts.Configuration;
using IntoChat.OS.Framework;
using IntoChat.OS.Framework.Configuration;
using IntoChat.OS.Kernel;
using IntoChat.OS.Mcp;
using IntoChat.OS.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IntoChat.OS.Tests.Integration.Security;

public sealed class Gate20EdgeApprovalWallFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LocalHost_BootsWithClosedUnstampedEdge_DisablesDashboardByDefault_AndSignsInWithout500()
    {
        await using var host = await EdgeKernelHarness.StartAsync(Ct);

        // B04: UnstampedEdgeIsKernel is false on the composed product silo
        var accessOptions = host.Services.GetRequiredService<AccessOptions>();
        Assert.False(accessOptions.UnstampedEdgeIsKernel);

        // B04: Orleans Dashboard is gated off by default unless explicitly enabled
        var serverBuilder = host.Services.GetRequiredService<IntoChatOSServerBuilder>();
        Assert.False(serverBuilder.Dashboard);

        // B04: Direct unstamped grain/neuron call with cleared caller provenance is rejected
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var inbox = proxies.Get<IConfirmationInbox>(IConfirmationInbox.Key(host.Scope));
        using (InProcessCallerScope.ClearCaller())
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(inbox.Read);
        }

        // B02: POST /identity/register and POST /identity/login succeed with 200 OK (never 500 PrivilegeDenied)
        using var registerRes = await host.Client.PostAsJsonAsync(
            "/identity/register",
            new { principalId = "alice", password = "correct-horse-battery-staple", displayName = "Alice" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, registerRes.StatusCode);

        using var loginRes = await host.Client.PostAsJsonAsync(
            "/identity/login",
            new { principalId = "alice", password = "correct-horse-battery-staple" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, loginRes.StatusCode);
    }

    [Fact]
    public async Task Gate20_RequiresPersonConfirmationPlan_ResistsMcpLoopbackForgedPresenceAndAgent_ThenApprovesWithPresenceProof()
    {
        await using var host = await EdgeKernelHarness.StartAsync(Ct);
        var proxies = host.Services.GetRequiredService<INeuronProxies>();
        var scope = host.Scope;
        var planId = $"app:change:gate20:{Guid.NewGuid():N}";

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
            Expiry = TimeSpan.FromHours(48),
            RequiresPerson = true,
        };

        // Raise a waiting RequiresPerson ConfirmationPlan from an app/flow context (non-person raiser so Request parks it Pending!)
        var requestsId = IConfirmationRequests.Of(new BrainId(scope));
        using (InProcessCallerScope.EnterApp(scope, "ino.studio", AccountSession.DefaultLogin))
        {
            var requests = proxies.Get<IConfirmationRequests>(requestsId);
            _ = requests.Request(plan);
        }

        var inboxKey = IConfirmationInbox.Key(scope);
        await WaitUntilPendingAsync(host.Services, scope, planId, Ct);

        // -----------------------------------------------------------------------------------------
        // Attack 1: Forged X-IntoChat-Presence header -> rejected at HTTP edge with 401 Unauthorized
        // -----------------------------------------------------------------------------------------
        using (var forgedReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/apps/event")
        {
            Content = JsonContent.Create(new
            {
                kind = "confirmation",
                name = inboxKey,
                planId,
                approve = true,
            }),
        })
        {
            forgedReq.Headers.Add(PresenceProofValidator.HeaderName, "forged-token-value-12345678901234567890");
            using var forgedRes = await host.Client.SendAsync(forgedReq, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, forgedRes.StatusCode);
        }

        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 2: Unproved loopback HTTP caller (no X-IntoChat-Presence -> stamped App("local"))
        // Attempt via both /apps/event and /screens/approvals/{id}
        // -----------------------------------------------------------------------------------------
        using (var unprovedEventRes = await host.Client.PostAsJsonAsync(
            $"/brains/{scope}/apps/event",
            new
            {
                kind = "confirmation",
                name = inboxKey,
                planId,
                approve = true,
            },
            Ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unprovedEventRes.StatusCode);
        }

        using (var unprovedScreenRes = await host.Client.PostAsJsonAsync(
            $"/brains/{scope}/screens/approvals/{Uri.EscapeDataString(planId)}",
            new { approved = true, reason = "unproved loopback attempt" },
            Ct))
        {
            Assert.False(unprovedScreenRes.IsSuccessStatusCode);
        }

        Assert.True(await IsPendingAsync(host.Services, scope, planId));

        // -----------------------------------------------------------------------------------------
        // Attack 3: /mcp caller (EVEN WHEN HTTP transport carries valid X-IntoChat-Presence!)
        // McpCaller ALWAYS stamps Actor.Agent (AppId = "assistant", ConversationId = "mcp").
        // Try screen_approve (approve + deny) AND neurons_call on Resolve + Withdraw!
        // -----------------------------------------------------------------------------------------
        using var mcpHttp = new HttpClient { BaseAddress = host.BaseAddress };
        mcpHttp.DefaultRequestHeaders.Add(PresenceProofValidator.HeaderName, host.PresenceToken);
        await using (var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(host.BaseAddress, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            }, mcpHttp, ownsHttpClient: false),
            cancellationToken: Ct))
        {
            var mcpApprove = await mcpClient.CallToolAsync(
                "screen_approve",
                new Dictionary<string, object?> { ["approvalId"] = planId, ["approved"] = true },
                cancellationToken: Ct);
            Assert.True(
                mcpApprove.IsError == true
                || ExtractText(mcpApprove).Contains("\"accepted\":false", StringComparison.OrdinalIgnoreCase)
                || ExtractText(mcpApprove).Contains("UnauthorizedAccessException", StringComparison.Ordinal),
                $"Expected MCP screen_approve(true) to fail, got: {ExtractText(mcpApprove)}");

            var mcpDenyCancelAttempt = await mcpClient.CallToolAsync(
                "screen_approve",
                new Dictionary<string, object?> { ["approvalId"] = planId, ["approved"] = false },
                cancellationToken: Ct);
            Assert.True(
                mcpDenyCancelAttempt.IsError == true
                || ExtractText(mcpDenyCancelAttempt).Contains("\"accepted\":false", StringComparison.OrdinalIgnoreCase)
                || ExtractText(mcpDenyCancelAttempt).Contains("UnauthorizedAccessException", StringComparison.Ordinal),
                $"Expected MCP screen_approve(false) to fail without cancelling plan, got: {ExtractText(mcpDenyCancelAttempt)}");

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
        var agentCaller = CapabilityCallers.Of(
            AccountSession.DefaultLogin,
            AccountSession.DefaultLogin,
            scope,
            CallerKind.Agent,
            CapabilityCallers.AssistantApp) with { ConversationId = "ino-turn" };

        using (InProcessCallerScope.Enter())
        using (DigitalBrain.Runtime.Orleans.Capabilities.RuntimeCallerContext.Stamp(agentCaller))
        {
            var inbox = proxies.Get<IConfirmationInbox>(inboxKey);
            var requests = proxies.Get<IConfirmationRequests>(requestsId);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.Resolve(planId, true));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inbox.Resolve(planId, false));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => requests.Withdraw(planId));
        }

        // Verify the plan is STILL Pending and Outcome is STILL null!
        Assert.True(await IsPendingAsync(host.Services, scope, planId));
        using (InProcessCallerScope.EnterKernel(scope))
        {
            Assert.Null(await proxies.Get<IConfirmationRequests>(requestsId).Outcome(planId));
        }

        // -----------------------------------------------------------------------------------------
        // Legitimate Person Approval: HTTP caller with valid X-IntoChat-Presence approves in 1 call!
        // -----------------------------------------------------------------------------------------
        using (var personReq = new HttpRequestMessage(HttpMethod.Post, $"/brains/{scope}/apps/event")
        {
            Content = JsonContent.Create(new
            {
                kind = "confirmation",
                name = inboxKey,
                planId,
                approve = true,
            }),
        })
        {
            personReq.Headers.Add(PresenceProofValidator.HeaderName, host.PresenceToken);
            using var personRes = await host.Client.SendAsync(personReq, Ct);
            Assert.Equal(HttpStatusCode.OK, personRes.StatusCode);
        }

        using (InProcessCallerScope.EnterKernel(scope))
        {
            var resolved = await proxies.Get<IConfirmationRequests>(requestsId).Outcome(planId);
            Assert.NotNull(resolved);
            Assert.True(resolved.Approved);
            Assert.Equal(ConfirmationOutcome.Approved, resolved.Outcome);
        }

        Assert.False(await IsPendingAsync(host.Services, scope, planId));
    }

    private static string ExtractText(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task WaitUntilPendingAsync(IServiceProvider services, string scope, string planId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsPendingAsync(services, scope, planId))
            {
                return;
            }

            await Task.Delay(50, ct);
        }

        throw new TimeoutException($"Confirmation plan '{planId}' did not appear in Pending within 10s.");
    }

    private static async Task<bool> IsPendingAsync(IServiceProvider services, string scope, string planId)
    {
        using var _ = InProcessCallerScope.EnterKernel(scope);
        var inbox = services.GetRequiredService<INeuronProxies>()
            .Get<IConfirmationInbox>(IConfirmationInbox.Key(scope));
        var state = await inbox.Read();
        return state.Pending.Any(p => string.Equals(p.PlanId, planId, StringComparison.Ordinal));
    }

    private sealed class EdgeKernelHarness : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly IDisposable _ports;
        private readonly string _homeDir;

        private EdgeKernelHarness(WebApplication app, HttpClient client, IDisposable ports, string homeDir, string scope, string presenceToken)
        {
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

        public static async Task<EdgeKernelHarness> StartAsync(CancellationToken ct)
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
                    [AuthOptions.PostureKey] = "Open",
                    [TopologyContext.SiloPortKey] = ports.SiloPort.ToString(CultureInfo.InvariantCulture),
                    [TopologyContext.GatewayPortKey] = ports.GatewayPort.ToString(CultureInfo.InvariantCulture),
                    [PresenceProofValidator.EnvironmentVariable] = null,
                });

                LocalRunDefaults.Apply(builder.Configuration);
                builder.AddIntoChatOSServer(server => server.AddOperatingSystem(OperatingSystemModules.InProcessModules));
                builder.Services.AddIntoChatOSMcp();

                app = builder.Build();
                app.MapIntoChatOSServer();
                app.MapIntoChatOSMcp();
                await app.StartAsync(ct);

                var baseUrl = app.Urls.First();
                client = new HttpClient { BaseAddress = new Uri(baseUrl, UriKind.Absolute) };
                var scope = BrainScope.Create(AccountSession.DefaultLogin, AccountSession.DefaultLogin).Id;
                var proof = app.Services.GetRequiredService<IPresenceProof>();

                var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
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
                    throw new TimeoutException($"EdgeKernelHarness at '{baseUrl}' did not become ready within 30s.");
                }

                return new EdgeKernelHarness(app, client, ports, homeDir, scope, proof.Token);
            }
            catch
            {
                client?.Dispose();
                if (app is not null)
                {
                    await app.DisposeAsync();
                }

                ports.Dispose();
                TryDeleteDirectory(homeDir);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _ports.Dispose();
            TryDeleteDirectory(_homeDir);
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
