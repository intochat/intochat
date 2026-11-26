using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Access;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Neurons;
using DigitalBrain.Runtime.Orleans;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using IntoChat.OS.Modules.Identity.Directory;
using IntoChat.OS.Modules.Time.Reminders;
using IntoChat.OS.Runtime;
using IntoChat.OS.Tests.Integration.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Integration.Modules.Identity;

/// <summary>
/// End-to-end integration tests for Slice OSS-2:
/// - Day-0 instant boot (GET / auto-mints an HttpOnly, SameSite=Lax ephemeral session cookie in RAM)
/// - Residency metadata surface on /identity/session and /identity/residency
/// - Zero orphan try-* disk files or durable directory entries while ephemeral
/// - Zero AppNeuron warnings during BornEphemeral + OsDefaults shipped app installation
/// - Structured HTTP 428 CLAIM_REQUIRED upgrade prompts on external OAuth connectors & durable secrets
/// - Background schedule/automation gating via IBrainCapabilityPolicy
/// - In-place promotion via POST /identity/claim preserving the exact BrainId and state into durable disk storage
/// </summary>
public sealed class BrainResidencyIntegrationFacts(BrainResidencyIntegrationFacts.HostFixture fixture)
    : IClassFixture<BrainResidencyIntegrationFacts.HostFixture>
{
    public sealed class HostFixture : IAsyncLifetime
    {
        private string _webRoot = "";

        internal RecordingLoggerProvider Logs { get; } = new();
        internal Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            _webRoot = Path.Combine(Path.GetTempPath(), $"intochat-day0-webroot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_webRoot);
            await File.WriteAllTextAsync(Path.Combine(_webRoot, "index.html"), "<!DOCTYPE html><html><body>IntoChat OS Day-0 Shell</body></html>");

            Host = await Gate20EdgeApprovalWallFacts.EdgeKernelHarness.StartAsync(TestContext.Current.CancellationToken, builder =>
            {
                builder.Environment.WebRootPath = _webRoot;
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [AuthKeys.Day0EphemeralTry] = "true",
                    ["IntoChatOS:ForwardedHeaders:ForwardLimit"] = "2",
                    ["IntoChatOS:ForwardedHeaders:KnownNetworks:0"] = "127.0.0.0/8",
                    ["IntoChatOS:ForwardedHeaders:KnownNetworks:1"] = "::1/128",
                });
                builder.Logging.AddProvider(Logs);
            });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (Host is not null)
                {
                    await Host.DisposeAsync();
                }
            }
            finally
            {
                Logs.Dispose();
                DigitalBrain.Testing.TempDirectory.DeleteIfPresent(_webRoot);
            }
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private Gate20EdgeApprovalWallFacts.EdgeKernelHarness Host => fixture.Host;
    private RecordingLoggerProvider Logs => fixture.Logs;

    private HttpClient NewBrowser() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
    })
    {
        BaseAddress = Host.BaseAddress,
    };

    [Fact]
    public async Task Day0Boot_AutoMintsEphemeralBrainInRam_GatesExternalCapabilitiesWith428_AndPromotesInPlaceOnClaim()
    {
        var ephemeralRegistry = Host.Services.GetRequiredService<EphemeralBrainRegistry>();
        var activeBeforeProbes = ephemeralRegistry.ActiveCount;

        // 0. Non-browser probes on / and /index.html must NOT mint RAM brains.
        using (var probe = NewBrowser())
        {
            using var rootProbe = await probe.GetAsync("/", Ct);
            Assert.False(rootProbe.Headers.Contains("Set-Cookie"));

            using var indexReq = new HttpRequestMessage(HttpMethod.Get, "/index.html");
            indexReq.Headers.TryAddWithoutValidation("Accept", "*/*");
            using var indexProbe = await probe.SendAsync(indexReq, Ct);
            Assert.False(indexProbe.Headers.Contains("Set-Cookie"));
        }
        Assert.Equal(activeBeforeProbes, ephemeralRegistry.ActiveCount);

        using var browser = NewBrowser();
        var storeDir = Host.Services.GetRequiredService<IOptions<BrainStoreOptions>>().Value.Directory;

        // 1. Unauthenticated visitor hits GET /identity/session -> Day-0 bootstrap auto-mints an HttpOnly, SameSite=Lax ephemeral session.
        var cookieName = Host.Services.GetRequiredService<IOptions<IdentityHostOptions>>().Value.CookieName;
        using var sessionResp = await browser.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionResp.StatusCode);
        Assert.True(sessionResp.Headers.TryGetValues("Set-Cookie", out var setCookies));
        var setCookieHeader = string.Join("; ", setCookies);
        Assert.Contains(cookieName + "=", setCookieHeader, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.True(sessionResp.Headers.CacheControl?.NoStore == true);

        using var sessionDoc = JsonDocument.Parse(await sessionResp.Content.ReadAsStringAsync(Ct));
        var root = sessionDoc.RootElement;
        var ephemeralBrainId = root.GetProperty("brainId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ephemeralBrainId));
        Assert.True(GuestSurface.IsGuestPrincipal(ephemeralBrainId));

        var residency = root.GetProperty("residency");
        Assert.True(residency.GetProperty("isEphemeral").GetBoolean());
        Assert.True(residency.GetProperty("ttlSeconds").GetInt64() > 0);
        Assert.Equal(15, residency.GetProperty("remainingTurns").GetInt32());
        Assert.Equal(16 * 1024 * 1024, residency.GetProperty("memoryLimitBytes").GetInt64());

        // 2. Dedicated GET /identity/residency surface matches for Slice OSS-3 Top Guest Pill binding.
        var residencySnap = await browser.GetFromJsonAsync<BrainResidencySnapshot>("/identity/residency", Ct);
        Assert.NotNull(residencySnap);
        Assert.True(residencySnap.IsEphemeral);
        Assert.Equal(15, residencySnap.RemainingTurns);
        Assert.Equal(16 * 1024 * 1024, residencySnap.MemoryLimitBytes);

        // 3. Zero files exist on disk for the ephemeral brain — pure RAM residency (matches BrainStorageKeys.DirectoryOf).
        var brainHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ephemeralBrainId!))).ToLowerInvariant();
        var brainDiskDir = Path.Combine(storeDir, brainHash);
        Assert.False(Directory.Exists(brainDiskDir));

        // 4. External OAuth connectors (login, probe, disconnect) cleanly return HTTP 428 Precondition Required + CLAIM_REQUIRED (not opaque 403).
        using var connectorResp = await browser.PostAsJsonAsync(
            $"/brains/{ephemeralBrainId}/connections/gh-main/login",
            new { connectorId = "github", authMethodId = "oauth" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, connectorResp.StatusCode);
        using (var claimDoc = JsonDocument.Parse(await connectorResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(CapabilityDecision.ClaimRequiredCode, claimDoc.RootElement.GetProperty("code").GetString());
            Assert.True(claimDoc.RootElement.GetProperty("requiresClaim").GetBoolean());
            Assert.Equal(nameof(BrainCapability.ExternalConnector), claimDoc.RootElement.GetProperty("capability").GetString());
            Assert.Equal("github", claimDoc.RootElement.GetProperty("resource").GetString());
            Assert.Equal("/identity/claim", claimDoc.RootElement.GetProperty("claimEndpoint").GetString());
        }

        using var probeResp = await browser.PostAsync($"/brains/{ephemeralBrainId}/connections/github/gh-main/probe", content: null, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, probeResp.StatusCode);
        using var disconnectResp = await browser.PostAsync($"/brains/{ephemeralBrainId}/connections/github/gh-main/disconnect", content: null, Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, disconnectResp.StatusCode);

        // 5. Durable vault secrets also return HTTP 428 CLAIM_REQUIRED.
        using var secretResp = await browser.PostAsJsonAsync(
            $"/secrets/{ephemeralBrainId}",
            new { name = "API_KEY", label = "API Key", value = "sk-test-123" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, secretResp.StatusCode);
        using (var secretClaimDoc = JsonDocument.Parse(await secretResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(CapabilityDecision.ClaimRequiredCode, secretClaimDoc.RootElement.GetProperty("code").GetString());
            Assert.Equal(nameof(BrainCapability.DurableSecrets), secretClaimDoc.RootElement.GetProperty("capability").GetString());
        }

        // 6. Background automations / schedules throw ClaimRequiredException for ephemeral brains.
        var edge = Host.Services.GetRequiredService<EdgeAuthority>();
        var proxies = Host.Services.GetRequiredService<INeuronProxies>();
        using (edge.Enter(edge.Guest(ephemeralBrainId, ephemeralBrainId, new BrainId(ephemeralBrainId))))
        {
            var reminder = proxies.Get<IReminder>($"{ephemeralBrainId}/daily-brief");
            var claimError = await Assert.ThrowsAsync<ClaimRequiredException>(() =>
                reminder.Start(TimeSpan.FromHours(1), TimeSpan.FromHours(24)));
            Assert.Equal(BrainCapability.BackgroundAutomation, claimError.Decision.Capability);
            Assert.True(claimError.Decision.RequiresClaim);
        }

        // Still zero files on disk and zero durable DirectoryNeuron entries before claim!
        Assert.False(Directory.Exists(brainDiskDir));
        using (edge.Enter(edge.Module("identity.directory", new BrainId("system"))))
        {
            var durableDirectory = proxies.Get<IDirectoryStore>(IDirectoryStore.Key);
            Assert.False(await durableDirectory.BrainExists(ephemeralBrainId!));
        }

        // BornEphemeral + OsDefaults shipped app installation must emit zero AppNeuron warnings.
        Assert.DoesNotContain(Logs.Entries, entry =>
            entry.Level >= LogLevel.Warning
            && entry.Category.Contains("AppNeuron", StringComparison.Ordinal));

        // 7. In-place promotion via POST /identity/claim (password registration): exact BrainId preserved!
        var username = "claimer-" + Guid.NewGuid().ToString("N")[..8];
        using var claimResp = await browser.PostAsJsonAsync(
            "/identity/claim",
            new { principalId = username, password = "correct-horse-battery-staple", displayName = "Claimer User" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, claimResp.StatusCode);
        using (var claimedDoc = JsonDocument.Parse(await claimResp.Content.ReadAsStringAsync(Ct)))
        {
            Assert.Equal(ephemeralBrainId, claimedDoc.RootElement.GetProperty("brainId").GetString());
            Assert.Equal(username, claimedDoc.RootElement.GetProperty("principalId").GetString());
            Assert.True(claimedDoc.RootElement.GetProperty("claimed").GetBoolean());
            Assert.False(claimedDoc.RootElement.GetProperty("residency").GetProperty("isEphemeral").GetBoolean());
        }

        // 7b. A second ephemeral visitor attempting to register-claim with the already-registered username is rejected (409), even with the same password.
        using var browser2 = NewBrowser();
        using var sessionResp2 = await browser2.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionResp2.StatusCode);
        using var duplicateClaimResp = await browser2.PostAsJsonAsync(
            "/identity/claim",
            new { principalId = username, password = "correct-horse-battery-staple", displayName = "Duplicate Claimer" },
            Ct);
        Assert.Equal(HttpStatusCode.Conflict, duplicateClaimResp.StatusCode);

        // 8. Exact BrainId was promoted in-place from RAM to encrypted durable disk storage!
        Assert.True(Directory.Exists(brainDiskDir));
        Assert.True(File.Exists(Path.Combine(brainDiskDir, "brain.key")));
        Assert.True(File.Exists(Path.Combine(brainDiskDir, "current")));

        // 9. Brain neuron state now reports Persistent(username) and session reports isEphemeral = false.
        var brainState = await edge.AsKernel(
            new BrainId(ephemeralBrainId),
            () => proxies.Get<IBrain>(IBrain.Key(ephemeralBrainId)).Read());
        var persistent = Assert.IsType<BrainResidency.Persistent>(brainState.Residency);
        Assert.Equal(username, persistent.OwnerPrincipalId);

        var afterResidency = await browser.GetFromJsonAsync<BrainResidencySnapshot>("/identity/residency", Ct);
        Assert.NotNull(afterResidency);
        Assert.False(afterResidency.IsEphemeral);

        // 10. Durable secrets are now unlocked for the claimed owner!
        using var secretAfterResp = await browser.PostAsJsonAsync(
            $"/secrets/{username}",
            new { name = "API_KEY", label = "API Key", value = "sk-live-456" },
            Ct);
        Assert.Equal(HttpStatusCode.OK, secretAfterResp.StatusCode);

        Assert.DoesNotContain(Logs.Entries, entry =>
            entry.Level >= LogLevel.Warning
            && entry.Category.Contains("AppNeuron", StringComparison.Ordinal));
    }

    internal sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    internal sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> entries = new();

        public IReadOnlyList<LogEntry> Entries => [.. entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
        }
    }

    [Fact]
    public async Task Day0GuestShellJourney_ConnectActChatsPreferencesDownloads_Returns200WithZero403s_AndEnforcesBoundaries()
    {
        var cookieName = Host.Services.GetRequiredService<IOptions<IdentityHostOptions>>().Value.CookieName;

        // 1. ForwardedHeaders (X-Forwarded-Proto: https) runs before Day0EphemeralBootstrap / CookieAuthentication,
        //    so Set-Cookie: os.session carries '; secure' behind HTTPS ingress (e.g. Azure Container Apps).
        using (var httpsProxyClient = NewBrowser())
        {
            using var httpsReq = new HttpRequestMessage(HttpMethod.Get, "/");
            httpsReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            httpsReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            using var httpsResp = await httpsProxyClient.SendAsync(httpsReq, Ct);
            Assert.Equal(HttpStatusCode.OK, httpsResp.StatusCode);
            Assert.True(httpsResp.Headers.TryGetValues("Set-Cookie", out var httpsSetCookies));
            var httpsCookie = string.Join("; ", httpsSetCookies);
            Assert.Contains(cookieName + "=", httpsCookie, StringComparison.Ordinal);
            Assert.Contains("httponly", httpsCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", httpsCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("; secure", httpsCookie, StringComparison.OrdinalIgnoreCase);
        }

        // 2. Full Day-0 visitor journey: GET / -> /identity/session -> POST /apps/connect -> GET /apps/node -> POST /act
        //    -> GET /chats -> GET/PUT /preferences/workspace -> GET /connectors -> GET /downloads -> 200 OK with zero 403s.
        using var browserA = NewBrowser();
        using var rootReq = new HttpRequestMessage(HttpMethod.Get, "/");
        rootReq.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        using var rootResp = await browserA.SendAsync(rootReq, Ct);
        Assert.Equal(HttpStatusCode.OK, rootResp.StatusCode);

        using var sessionAResp = await browserA.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionAResp.StatusCode);
        using var sessionADoc = JsonDocument.Parse(await sessionAResp.Content.ReadAsStringAsync(Ct));
        var tryA = sessionADoc.RootElement.GetProperty("brainId").GetString()!;
        Assert.True(GuestSurface.IsGuestPrincipal(tryA));

        using var connectResp = await browserA.PostAsJsonAsync($"/brains/{tryA}/apps/connect", new { }, Ct);
        var connectBody = await connectResp.Content.ReadAsStringAsync(Ct);
        Assert.True(connectResp.StatusCode == HttpStatusCode.OK, $"POST /brains/{tryA}/apps/connect failed: {(int)connectResp.StatusCode} {connectBody}");
        using var connectDoc = JsonDocument.Parse(connectBody);
        var rootViewName = connectDoc.RootElement.GetProperty("root").GetProperty("name").GetString()!;
        Assert.Equal(IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(tryA), "desktop").ViewKey, rootViewName);

        using var nodeResp = await browserA.GetAsync($"/brains/{tryA}/apps/node?kind=view&name={Uri.EscapeDataString(rootViewName)}", Ct);
        var nodeBody = await nodeResp.Content.ReadAsStringAsync(Ct);
        Assert.True(nodeResp.StatusCode == HttpStatusCode.OK, $"GET /brains/{tryA}/apps/node failed: {(int)nodeResp.StatusCode} {nodeBody}");
        Assert.Contains("dock-ino", nodeBody, StringComparison.Ordinal);

        // Press dock-ino through POST /brains/{tryA}/act -> real DesktopApp -> real WorkspaceNeuron.Apply.
        using var nodeDoc = JsonDocument.Parse(nodeBody);
        var dockCommand = FindCommandByKey(nodeDoc.RootElement, "dock-ino")
            ?? throw new Xunit.Sdk.XunitException($"Expected command for 'dock-ino' in desktop view: {nodeBody}");
        using var actResp = await browserA.PostAsJsonAsync($"/brains/{tryA}/act", new
        {
            neuron = IntoChat.OS.Host.Primitives.AppRef.Of(new BrainId(tryA), "desktop").Key,
            target = dockCommand.GetProperty("target").GetString(),
            signal = dockCommand.GetProperty("signal").GetString(),
            args = dockCommand.TryGetProperty("args", out var cmdArgs) ? cmdArgs.Clone() : (JsonElement?)null,
        }, Ct);
        var actBody = await actResp.Content.ReadAsStringAsync(Ct);
        Assert.True(actResp.StatusCode == HttpStatusCode.OK, $"POST /brains/{tryA}/act failed: {(int)actResp.StatusCode} {actBody}");

        using var chatsResp = await browserA.GetAsync($"/brains/{tryA}/chats", Ct);
        Assert.Equal(HttpStatusCode.OK, chatsResp.StatusCode);

        using var prefsResp = await browserA.GetAsync($"/brains/{tryA}/preferences/workspace", Ct);
        Assert.Equal(HttpStatusCode.OK, prefsResp.StatusCode);

        using var putPrefsResp = await browserA.PutAsJsonAsync($"/brains/{tryA}/preferences/workspace", new { newTabApp = "ino" }, Ct);
        Assert.Equal(HttpStatusCode.OK, putPrefsResp.StatusCode);

        using var connectorsResp = await browserA.GetAsync("/connectors", Ct);
        Assert.Equal(HttpStatusCode.OK, connectorsResp.StatusCode);

        using var downloadsResp = await browserA.GetAsync("/downloads", Ct);
        Assert.Equal(HttpStatusCode.OK, downloadsResp.StatusCode);
        var downloadsHtml = await downloadsResp.Content.ReadAsStringAsync(Ct);
        Assert.Contains("IntoChat-Setup-win-x64.exe", downloadsHtml, StringComparison.Ordinal);

        using var downloadResp = await browserA.GetAsync("/download", Ct);
        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);

        // 3. Unmatched route with a try-* cookie falls through cleanly to 404 (never falsely 403).
        using var notFoundResp = await browserA.GetAsync("/this-route-does-not-exist-404", Ct);
        Assert.Equal(HttpStatusCode.NotFound, notFoundResp.StatusCode);

        // 4. Operator, device-pairing, and host-install routes stay closed (403).
        using var devicesResp = await browserA.GetAsync($"/brains/{tryA}/devices/this", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, devicesResp.StatusCode);

        using var integrationsResp = await browserA.GetAsync("/integrations", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, integrationsResp.StatusCode);

        // 5. Cross-brain isolation: try-A cannot access try-B (403 Forbidden).
        using var browserB = NewBrowser();
        using var sessionBResp = await browserB.GetAsync("/identity/session", Ct);
        Assert.Equal(HttpStatusCode.OK, sessionBResp.StatusCode);
        using var sessionBDoc = JsonDocument.Parse(await sessionBResp.Content.ReadAsStringAsync(Ct));
        var tryB = sessionBDoc.RootElement.GetProperty("brainId").GetString()!;
        Assert.NotEqual(tryA, tryB);

        using var crossConnect = await browserA.PostAsJsonAsync($"/brains/{tryB}/apps/connect", new { }, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, crossConnect.StatusCode);
        using var crossChats = await browserA.GetAsync($"/brains/{tryB}/chats", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, crossChats.StatusCode);
        using var crossPrefs = await browserA.GetAsync($"/brains/{tryB}/preferences/workspace", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, crossPrefs.StatusCode);

        // 6. External OAuth and durable secrets still return 428 CLAIM_REQUIRED.
        using var oauthResp = await browserA.PostAsJsonAsync(
            $"/brains/{tryA}/connections/gh-main/login",
            new { connectorId = "github", authMethodId = "oauth" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, oauthResp.StatusCode);

        using var secretResp = await browserA.PostAsJsonAsync(
            $"/secrets/{tryA}",
            new { name = "API_KEY", label = "API Key", value = "sk-test-789" },
            Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, secretResp.StatusCode);

        // 7. Logout succeeds cleanly for the guest session.
        using var logoutResp = await browserA.PostAsync("/identity/logout", content: null, Ct);
        Assert.Equal(HttpStatusCode.NoContent, logoutResp.StatusCode);
    }

    private static JsonElement? FindCommandByKey(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == key
                && element.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Object)
            {
                return command.Clone();
            }
            foreach (var property in element.EnumerateObject())
            {
                if (FindCommandByKey(property.Value, key) is { } found) { return found; }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (FindCommandByKey(item, key) is { } found) { return found; }
            }
        }
        return null;
    }
}
