using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Brains;
using DigitalBrain.Capabilities.Identity;
using DigitalBrain.Runtime.Http.Surface;
using DigitalBrain.Testing;
using Grpc.Core;
using IntoChat.OS.Host.Callers;
using IntoChat.OS.Kernel.Platform;
using IntoChat.OS.Modules.Identity.Accounts;
using IntoChat.OS.Modules.Identity.Contracts.Accounts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IntoChat.OS.Tests.Unit.Authority;

public sealed class BrainResidencyFacts
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void EphemeralResidency_HasExpectedDefaults_AndRoundTripsJson()
    {
        var ephemeral = BrainResidency.Ephemeral.Default;

        Assert.Equal(BrainResidency.Ephemeral.DefaultMaxMemoryBytes, ephemeral.MaxMemoryBytes);
        Assert.Equal(16 * 1024 * 1024, ephemeral.MaxMemoryBytes);
        Assert.Equal(TimeSpan.FromHours(24), ephemeral.IdleTtl);
        Assert.Equal(15, ephemeral.MaxTrialTurns);

        var persistent = new BrainResidency.Persistent("alice");
        Assert.Equal("alice", persistent.OwnerPrincipalId);

        BrainResidency asBaseEphemeral = ephemeral;
        BrainResidency asBasePersistent = persistent;
        var roundTrippedEphemeral = JsonSerializer.Deserialize<BrainResidency>(JsonSerializer.Serialize(asBaseEphemeral));
        var roundTrippedPersistent = JsonSerializer.Deserialize<BrainResidency>(JsonSerializer.Serialize(asBasePersistent));

        Assert.Equal(ephemeral, Assert.IsType<BrainResidency.Ephemeral>(roundTrippedEphemeral));
        Assert.Equal(persistent, Assert.IsType<BrainResidency.Persistent>(roundTrippedPersistent));
    }

    [Fact]
    public void DefaultBrainCapabilityPolicy_AllowsCoreDesktopInRam_AndReturnsClaimPromptForExternalCapabilities()
    {
        var policy = new DefaultBrainCapabilityPolicy();
        var ephemeral = BrainResidency.Ephemeral.Default;

        Assert.True(policy.Evaluate(ephemeral, BrainCapability.Desktop).Allowed);
        Assert.True(policy.Evaluate(ephemeral, BrainCapability.Gallery).Allowed);
        Assert.True(policy.Evaluate(ephemeral, BrainCapability.BuiltInApp, "ino").Allowed);

        var turnAllowed = policy.Evaluate(ephemeral, BrainCapability.AssistantTurn, "ino", turnsUsed: 14);
        Assert.True(turnAllowed.Allowed);
        Assert.Equal(1, turnAllowed.RemainingTurns);

        var turnExhausted = policy.Evaluate(ephemeral, BrainCapability.AssistantTurn, "ino", turnsUsed: 15);
        Assert.False(turnExhausted.Allowed);
        Assert.True(turnExhausted.RequiresClaim);
        Assert.Equal(CapabilityDecision.TrialTurnsExhaustedCode, turnExhausted.Code);
        Assert.Equal(0, turnExhausted.RemainingTurns);

        foreach (var (cap, resource) in new (BrainCapability, string?)[]
        {
            (BrainCapability.ExternalConnector, "github"),
            (BrainCapability.ExternalConnector, "google"),
            (BrainCapability.ExternalConnector, "salesforce"),
            (BrainCapability.BackgroundAutomation, "daily-digest"),
            (BrainCapability.DurableSecrets, "OPENAI_API_KEY"),
            (BrainCapability.DeviceEnrollment, "laptop"),
        })
        {
            var denied = policy.Evaluate(ephemeral, cap, resource);
            Assert.False(denied.Allowed);
            Assert.True(denied.RequiresClaim);
            Assert.Equal(CapabilityDecision.ClaimRequiredCode, denied.Code);
            Assert.Equal(cap, denied.Capability);
            Assert.Equal(resource, denied.Resource);
            Assert.False(string.IsNullOrWhiteSpace(denied.Message));
        }

        var persistent = new BrainResidency.Persistent("alice");
        foreach (var cap in Enum.GetValues<BrainCapability>())
        {
            var decision = policy.Evaluate(persistent, cap, "res", turnsUsed: 999);
            Assert.True(decision.Allowed);
            Assert.False(decision.RequiresClaim);
        }
    }

    [Fact]
    public async Task EphemeralBrainRegistry_TracksTurns_RefreshesIdleTtl_AndEvictsExpiredAndLruUnderPressure()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var storage = new FakeResidencyStorage();
        var options = Options.Create(new EphemeralBrainOptions
        {
            MaxMemoryBytes = 1024,
            IdleTtl = TimeSpan.FromHours(24),
            MaxTrialTurns = 3,
            MaxActiveBrains = 2,
            TotalMemoryBudgetBytes = 2048,
        });
        var registry = new EphemeralBrainRegistry(clock, options, storage);

        var first = registry.Mint();
        Assert.True(GuestSurface.IsGuestPrincipal(first.BrainId));
        storage.SetMemory(first.BrainId, 512);

        var snap = registry.GetSnapshot(first.BrainId);
        Assert.True(snap.IsEphemeral);
        Assert.Equal(3, snap.RemainingTurns);
        Assert.Equal(512, snap.MemoryUsedBytes);
        Assert.Equal(1024, snap.MemoryLimitBytes);
        Assert.Equal(24 * 3600, snap.TtlSeconds);

        // Consume all 3 trial turns; 4th is denied atomically.
        Assert.True(registry.TryConsumeTurn(first.BrainId, out var r1));
        Assert.Equal(2, r1);
        Assert.True(registry.TryConsumeTurn(first.BrainId, out var r2));
        Assert.Equal(1, r2);
        Assert.True(registry.TryConsumeTurn(first.BrainId, out var r3));
        Assert.Equal(0, r3);
        Assert.False(registry.TryConsumeTurn(first.BrainId, out var r4));
        Assert.Equal(0, r4);

        // Advance 12h, touch first, mint second -> first is newer than second after touch.
        clock.Advance(TimeSpan.FromHours(12));
        var second = registry.Mint();
        clock.Advance(TimeSpan.FromMinutes(1));
        registry.Touch(first.BrainId);

        // Minting a third exceeds MaxActiveBrains = 2 -> LRU (second) is evicted.
        var third = registry.Mint();
        Assert.True(registry.Contains(first.BrainId));
        Assert.False(registry.Contains(second.BrainId));
        Assert.True(registry.Contains(third.BrainId));

        // Advance past 24h idle TTL -> remaining brains are evicted and storage EvictAsync is called.
        clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(5));
        var evicted = await registry.SweepAsync(Ct);
        Assert.Equal(2, evicted);
        Assert.False(registry.Contains(first.BrainId));
        Assert.False(registry.Contains(third.BrainId));
        Assert.Contains(first.BrainId, storage.Evicted);
        Assert.Contains(third.BrainId, storage.Evicted);
    }

    [Fact]
    public async Task EphemeralBrainRegistry_EvictsLruUnderAggregateMemoryPressure()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var storage = new FakeResidencyStorage();
        var options = Options.Create(new EphemeralBrainOptions
        {
            MaxMemoryBytes = 1024,
            TotalMemoryBudgetBytes = 1500,
            MaxActiveBrains = 10,
        });
        var registry = new EphemeralBrainRegistry(clock, options, storage);

        var a = registry.Mint();
        storage.SetMemory(a.BrainId, 900);
        clock.Advance(TimeSpan.FromSeconds(10));
        var b = registry.Mint();
        storage.SetMemory(b.BrainId, 900);

        // Aggregate 1800 > 1500 -> sweeping evicts oldest (a), keeping b (900 <= 1500).
        var evicted = await registry.SweepAsync(Ct);
        Assert.Equal(1, evicted);
        Assert.False(registry.Contains(a.BrainId));
        Assert.True(registry.Contains(b.BrainId));
        Assert.Contains(a.BrainId, storage.Evicted);
    }

    [Fact]
    public void BrainCallTranslate_MapsClaimRequiredAndMemoryLimitToStructuredGrpcErrors()
    {
        var claimDecision = CapabilityDecision.RequireClaim(BrainCapability.ExternalConnector, "Claim to connect GitHub.", "github");
        var claimRpc = BrainCall.Translate(new ClaimRequiredException(claimDecision));
        Assert.Equal(StatusCode.FailedPrecondition, claimRpc.StatusCode);
        Assert.Equal(CapabilityDecision.ClaimRequiredCode, claimRpc.Trailers.GetValue(BrainCall.ReasonTrailer));
        Assert.Equal("true", claimRpc.Trailers.GetValue("requires-claim"));
        Assert.Equal("ExternalConnector", claimRpc.Trailers.GetValue("capability"));
        Assert.Equal("/identity/claim", claimRpc.Trailers.GetValue("claim-endpoint"));

        var memRpc = BrainCall.Translate(new BrainMemoryLimitExceededException("try-123", 17_000_000, 16 * 1024 * 1024));
        Assert.Equal(StatusCode.ResourceExhausted, memRpc.StatusCode);
        Assert.Equal("EPHEMERAL_MEMORY_LIMIT", memRpc.Trailers.GetValue(BrainCall.ReasonTrailer));
        Assert.Equal("true", memRpc.Trailers.GetValue("requires-claim"));
    }

    [Fact]
    public async Task BrainNeuron_CreateEphemeral_AndInPlaceClaim_PromotesAndPreventsHijack()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        using var sp = sc.BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp);
        os.Register(typeof(DigitalBrain.Runtime.Orleans.BrainStoreOptions).Assembly);

        var brainId = "try-" + Guid.NewGuid().ToString("N")[..12];
        var brainIdTyped = new BrainId(brainId);
        await os.Edge.AsKernel(brainIdTyped, async () =>
        {
            var brain = os.Get<IBrain>(new NeuronId(brainIdTyped, IBrain.Type, "main"));

            await brain.CreateEphemeral(new Owner(brainId), BrainResidency.Ephemeral.Default);

            var before = await brain.Read();
            Assert.Equal(brainId, before.Owner.AccountId);
            var ephemeral = Assert.IsType<BrainResidency.Ephemeral>(before.Residency);
            Assert.Equal(16 * 1024 * 1024, ephemeral.MaxMemoryBytes);
            Assert.Equal(15, ephemeral.MaxTrialTurns);

            // An ephemeral try-* principal cannot claim a brain.
            await Assert.ThrowsAsync<ArgumentException>(() => brain.Claim("try-other"));

            // Promote in-place to persistent owner "alice".
            var claimed = await brain.Claim("alice", "acct-alice");
            Assert.Equal("acct-alice", claimed.Owner.AccountId);
            var persistent = Assert.IsType<BrainResidency.Persistent>(claimed.Residency);
            Assert.Equal("alice", persistent.OwnerPrincipalId);

            // Idempotent for the same owner.
            var again = await brain.Claim("alice", "acct-alice");
            Assert.Equal(persistent, again.Residency);

            // Refuses hijack by a different principal.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Claim("mallory", "acct-mallory"));
            return true;
        });
    }

    [Fact]
    public async Task Day0EphemeralBootstrap_AutoMintsHttpOnlyCookieOnRoot_AndThrottlesByIpPrefix()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var births = new RecordingBrainBirths();
        var cookieName = new IdentityHostOptions().CookieName;

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Production",
            ApplicationName = typeof(BrainResidencyFacts).Assembly.GetName().Name,
            ContentRootPath = Path.GetTempPath(),
        });
        builder.WebHost.UseSetting(WebHostDefaults.PreventHostingStartupKey, "true");
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AuthKeys.Posture] = IdentityPosture.Secured.ToString(),
            [AuthKeys.Day0EphemeralTry] = "true",
        });
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthKeys.SectionName);
        builder.Services.AddOptions<EphemeralBrainOptions>().Configure(o => o.MintPerMinutePerIpPrefix = 2);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Authority);
        builder.Services.AddSingleton(IntoChat.OS.Tests.TestEdge.Callers);
        builder.Services.AddSingleton<EphemeralBrainRegistry>();
        builder.Services.AddSingleton<IBrainResidencyRegistry>(sp => sp.GetRequiredService<EphemeralBrainRegistry>());
        builder.Services.AddSingleton<IBrainBirths>(births);
        builder.Services.AddSingleton<DigitalBrain.Runtime.Http.ISessionIssuer, AccountSessionIssuer>();
        builder.Services.AddSingleton(sp => new Day0EphemeralBootstrap.MintThrottle(
            new SlidingWindowThrottle(2, TimeSpan.FromMinutes(1), clock)));
        builder.Services.AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = cookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
            });
        builder.Services.AddAuthorization();

        await using var app = builder.Build();
        app.UseDay0EphemeralBootstrap();
        app.UseAuthentication();
        app.UseAccountSession();
        app.MapGet("/", (HttpContext http, IBrainResidencyRegistry residency) =>
        {
            var brainId = http.User.FindFirst(SessionClaims.Brain)?.Value ?? "";
            return Results.Ok(residency.GetSnapshot(brainId));
        }).WithMetadata(GuestSurface.Door.Instance);
        await app.StartAsync(Ct);

        try
        {
            var handler = new HttpClientHandler { UseCookies = true, CookieContainer = new CookieContainer() };
            using var browser1 = new HttpClient(handler) { BaseAddress = new Uri(app.Urls.First()) };

            using var firstResp = await browser1.GetAsync("/", Ct);
            Assert.Equal(HttpStatusCode.OK, firstResp.StatusCode);
            Assert.True(firstResp.Headers.TryGetValues("Set-Cookie", out var setCookies));
            var cookieHeader = string.Join("; ", setCookies);
            Assert.Contains(cookieName + "=", cookieHeader, StringComparison.Ordinal);
            Assert.Contains("httponly", cookieHeader, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", cookieHeader, StringComparison.OrdinalIgnoreCase);
            Assert.True(firstResp.Headers.CacheControl?.NoStore == true);

            var snap1 = await firstResp.Content.ReadFromJsonAsync<BrainResidencySnapshot>(Ct);
            Assert.NotNull(snap1);
            Assert.True(snap1.IsEphemeral);
            Assert.Equal(15, snap1.RemainingTurns);
            Assert.Equal(16 * 1024 * 1024, snap1.MemoryLimitBytes);
            Assert.Single(births.Minted);

            // Reusing the same browser cookie does NOT mint a second ephemeral brain.
            using var repeatResp = await browser1.GetAsync("/", Ct);
            Assert.Equal(HttpStatusCode.OK, repeatResp.StatusCode);
            Assert.Single(births.Minted);

            // Explicit Authorization header is never hijacked by Day-0 bootstrap.
            using var noCookie = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            using var authReq = new HttpRequestMessage(HttpMethod.Get, "/");
            authReq.Headers.TryAddWithoutValidation("Authorization", "Bearer external-token");
            using var authResp = await noCookie.SendAsync(authReq, Ct);
            Assert.False(authResp.Headers.Contains("Set-Cookie"));
            Assert.Single(births.Minted);

            // Second fresh browser succeeds (limit = 2/min/IP-prefix), third gets 429.
            using var browser2 = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(app.Urls.First()) };
            using var secondResp = await browser2.GetAsync("/", Ct);
            Assert.Equal(HttpStatusCode.OK, secondResp.StatusCode);
            Assert.Equal(2, births.Minted.Count);

            using var thirdResp = await browser2.GetAsync("/", Ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, thirdResp.StatusCode);
            Assert.Equal(2, births.Minted.Count);
        }
        finally
        {
            await app.StopAsync(Ct);
        }
    }

    [Fact]
    public async Task IBrainBirths_DefaultBornEphemeral_FailsClosedWithNotSupported()
    {
        IBrainBirths minimal = new MinimalBrainBirths();
        var owner = new Member
        {
            PrincipalId = "try-abc",
            AccountId = "try-abc",
            BrainId = "try-abc",
            DisplayName = "Guest",
            Role = MemberRole.Owner,
            JoinedAt = DateTimeOffset.UtcNow,
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => minimal.BornEphemeral(owner, BrainResidency.Ephemeral.Default));
    }

    [Fact]
    public async Task EphemeralBrainRegistry_ClaimPinning_PreventsConcurrentClaimAndSweeperOrLruEviction()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var storage = new FakeResidencyStorage();
        var options = Options.Create(new EphemeralBrainOptions
        {
            MaxMemoryBytes = 1024,
            IdleTtl = TimeSpan.FromMinutes(10),
            MaxActiveBrains = 1,
            TotalMemoryBudgetBytes = 500,
        });
        var registry = new EphemeralBrainRegistry(clock, options, storage);

        var pinned = registry.Mint();
        storage.SetMemory(pinned.BrainId, 900);

        Assert.True(registry.TryBeginClaim(pinned.BrainId));
        // Second concurrent claim attempt is rejected while pinned.
        Assert.False(registry.TryBeginClaim(pinned.BrainId));

        // Even past IdleTtl and over aggregate memory budget, sweeper must not evict pinned brain.
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await registry.SweepAsync(Ct));
        Assert.True(registry.Contains(pinned.BrainId));
        Assert.DoesNotContain(pinned.BrainId, storage.Evicted);

        // Minting a new brain under MaxActiveBrains = 1 also must not evict the pinned brain.
        var other = registry.Mint();
        Assert.True(registry.Contains(pinned.BrainId));
        Assert.True(registry.Contains(other.BrainId));

        // After EndClaim, the expired brain can be swept normally.
        registry.EndClaim(pinned.BrainId);
        Assert.True(await registry.SweepAsync(Ct) >= 1);
        Assert.False(registry.Contains(pinned.BrainId));
        Assert.Contains(pinned.BrainId, storage.Evicted);
    }

    [Fact]
    public async Task BrainNeuron_Claim_VerifiesPersonPrincipalNotAccountId_AndRefusesAccountIdSpoof()
    {
        var sc = new ServiceCollection();
        sc.AddLogging();
        using var sp = sc.BuildServiceProvider();
        await using var os = TestOS.Local(o => o.Services = sp);
        os.Register(typeof(DigitalBrain.Runtime.Orleans.BrainStoreOptions).Assembly);

        var brainId = "try-" + Guid.NewGuid().ToString("N")[..12];
        var brainIdTyped = new BrainId(brainId);
        await os.Edge.AsKernel(brainIdTyped, async () =>
        {
            var brain = os.Get<IBrain>(new NeuronId(brainIdTyped, IBrain.Type, "main"));
            await brain.CreateEphemeral(new Owner(brainId), BrainResidency.Ephemeral.Default);
            return true;
        });

        // Direct person whose Principal ("alice") differs from Account GUID ("acct-guid-123") can claim for "alice".
        var aliceCaller = os.Edge.Person("alice", "acct-guid-123", brainIdTyped);
        using (os.Edge.Enter(aliceCaller))
        {
            var brain = os.Get<IBrain>(new NeuronId(brainIdTyped, IBrain.Type, "main"));
            // Cannot claim for someone else's principal even if matching own account or not.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Claim("bob", "acct-guid-123"));
            // Cannot claim using own Account GUID as the principal id.
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Claim("acct-guid-123", "acct-guid-123"));

            var claimed = await brain.Claim("alice", "acct-guid-123");
            Assert.Equal("acct-guid-123", claimed.Owner.AccountId);
            Assert.Equal("alice", Assert.IsType<BrainResidency.Persistent>(claimed.Residency).OwnerPrincipalId);
        }

        // Kernel attempting to claim the already-persistent brain using the AccountId as ownerPrincipalId must be rejected.
        await os.Edge.AsKernel(brainIdTyped, async () =>
        {
            var brain = os.Get<IBrain>(new NeuronId(brainIdTyped, IBrain.Type, "main"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => brain.Claim("acct-guid-123", "acct-guid-123"));
            return true;
        });
    }

    [Fact]
    public async Task BrainCall_EnforceCapabilityAsync_BlocksConnectorAutomationDeviceAndSecretCallsForEphemeralBrains()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));
        var registry = new EphemeralBrainRegistry(clock, Options.Create(new EphemeralBrainOptions()));
        var ephemeral = registry.Mint();
        var policy = new DefaultBrainCapabilityPolicy(registry);

        var sc = new ServiceCollection();
        sc.AddSingleton<IBrainResidencyRegistry>(registry);
        sc.AddSingleton<IBrainCapabilityPolicy>(policy);
        using var sp = sc.BuildServiceProvider();

        var guestCaller = IntoChat.OS.Tests.TestEdge.Authority.Guest(ephemeral.BrainId, ephemeral.BrainId, new BrainId(ephemeral.BrainId));

        foreach (var (contract, key, expected) in new (string, string, BrainCapability)[]
        {
            ("DigitalBrain.Capabilities.Connections.IConnection", $"{ephemeral.BrainId}/connection/github/main", BrainCapability.ExternalConnector),
            ("IntoChat.OS.Modules.GitHub.Repository.IRepository", $"{ephemeral.BrainId}/connection/github/main", BrainCapability.ExternalConnector),
            ("IntoChat.OS.Modules.Time.Reminders.IReminder", $"{ephemeral.BrainId}/daily", BrainCapability.BackgroundAutomation),
            ("IntoChat.OS.Modules.Time.Timers.ITimer", $"{ephemeral.BrainId}/t1", BrainCapability.BackgroundAutomation),
            ("IntoChat.OS.Modules.Devices.IDeviceEnrollment", $"{ephemeral.BrainId}/device-enrollment", BrainCapability.DeviceEnrollment),
            ("IntoChat.OS.Modules.Identity.Secrets.IOsVault", $"{ephemeral.BrainId}/vault", BrainCapability.DurableSecrets),
        })
        {
            var error = await Assert.ThrowsAsync<ClaimRequiredException>(() =>
                BrainCall.EnforceCapabilityAsync(sp, guestCaller, ephemeral.BrainId, contract, key, surface: null, Ct));
            Assert.Equal(expected, error.Decision.Capability);
        }

        // Administrative identity neurons are refused with UnauthorizedAccessException for guests.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            BrainCall.EnforceCapabilityAsync(sp, guestCaller, ephemeral.BrainId, "IGrantsNeuron", $"{ephemeral.BrainId}/grants", surface: null, Ct));

        // Core desktop/workspace/app neurons remain allowed for ephemeral guests.
        await BrainCall.EnforceCapabilityAsync(sp, guestCaller, ephemeral.BrainId, "IWorkspace", $"{ephemeral.BrainId}/main", surface: null, Ct);

        // Persistent brains are allowed for all capabilities.
        var ownerCaller = IntoChat.OS.Tests.TestEdge.Authority.Person("alice", "acct-alice", new BrainId("brain-alice"));
        await BrainCall.EnforceCapabilityAsync(sp, ownerCaller, "brain-alice", "IntoChat.OS.Modules.Time.Reminders.IReminder", "brain-alice/daily", surface: null, Ct);
        await BrainCall.EnforceCapabilityAsync(sp, ownerCaller, "brain-alice", "IntoChat.OS.Modules.Devices.IDeviceEnrollment", "brain-alice/device-enrollment", surface: null, Ct);
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset current = start;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan delta) => current += delta;
    }

    private sealed class FakeResidencyStorage : IBrainResidencyStorage
    {
        private readonly Dictionary<string, long> memory = new(StringComparer.Ordinal);
        public List<string> Promoted { get; } = [];
        public List<string> Evicted { get; } = [];

        public void SetMemory(string brainId, long bytes) => memory[brainId] = bytes;
        public Task PromoteAsync(string brainId, CancellationToken ct = default) { Promoted.Add(brainId); return Task.CompletedTask; }
        public Task EvictAsync(string brainId, CancellationToken ct = default) { Evicted.Add(brainId); memory.Remove(brainId); return Task.CompletedTask; }
        public long GetMemoryUsedBytes(string brainId) => memory.GetValueOrDefault(brainId);
    }

    private sealed class MinimalBrainBirths : IBrainBirths
    {
        public Task<Member> Born(BrainProvision provision) => Task.FromResult(provision.Member);
    }

    private sealed class RecordingBrainBirths : IBrainBirths
    {
        public List<(Member Owner, BrainResidency.Ephemeral Residency)> Minted { get; } = [];
        public Task<Member> Born(BrainProvision provision) => Task.FromResult(provision.Member);
        public Task<Member> BornEphemeral(Member owner, BrainResidency.Ephemeral residency)
        {
            Minted.Add((owner, residency));
            return Task.FromResult(owner);
        }
    }
}
