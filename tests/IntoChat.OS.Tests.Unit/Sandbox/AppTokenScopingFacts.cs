using System.Text;
using System.Text.Json;
using DigitalBrain;
using DigitalBrain.Confirmation;
using IntoChat.OS.Host.Surface;
using IntoChat.OS.Kernel.Sandbox;
using IntoChat.OS.Modules.Apps.CodeBlocks;
using IntoChat.OS.Modules.Google.Gmail;
using IntoChat.OS.Modules.Microsoft.GitHub;
using IntoChat.OS.Modules.Notifications;
using IntoChat.Sdk;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace IntoChat.OS.Tests.Unit.Sandbox;

[NeuronContract("demo.widgets")]
public interface IDemoWidgetContract
{
    Task<string> ListWidgetsAsync(CancellationToken ct = default);

    [PersonOnly("Deleting widgets permanently mutates production state.")]
    Task DeleteWidgetAsync(string widgetId, CancellationToken ct = default);
}

[DigitalBrain.Access.Internal]
[NeuronContract("demo.internal")]
public interface IDemoInternalContract
{
    Task SecretOpAsync();
}

public sealed class AppTokenScopingFacts
{
    private static AppTokenService CreateTokens(TimeProvider? time = null, int ttlMinutes = 30)
        => new(
            Options.Create(new SandboxOptions
            {
                TokenSigningMaterial = "unit-test-signing-material-32bytes!",
                TokenTtlMinutes = ttlMinutes,
            }),
            time);

    [Fact]
    public void ContractDiscoveryFindsNeuronContractInterfacesAndPersonOnlyMethods()
    {
        var contracts = NeuronContractDiscovery.Discover([typeof(IDemoWidgetContract).Assembly, typeof(IRepository).Assembly, typeof(INotificationsInbox).Assembly]);

        var demo = Assert.Single(contracts, c => c.FullName == typeof(IDemoWidgetContract).FullName);
        Assert.Equal("demo.widgets", demo.Alias);
        var listMethod = Assert.Single(demo.Methods, m => m.Name == nameof(IDemoWidgetContract.ListWidgetsAsync));
        Assert.False(listMethod.RequiresPersonApproval);
        var deleteMethod = Assert.Single(demo.Methods, m => m.Name == nameof(IDemoWidgetContract.DeleteWidgetAsync));
        Assert.True(deleteMethod.RequiresPersonApproval);
        Assert.Contains("mutates production state", deleteMethod.ApprovalReason, StringComparison.OrdinalIgnoreCase);

        // Internal and authority contracts are never exposed for orchestration discovery.
        Assert.DoesNotContain(contracts, c => c.FullName == typeof(IDemoInternalContract).FullName);
        Assert.DoesNotContain(contracts, c => c.FullName == typeof(IConfirmationRequests).FullName);
        Assert.DoesNotContain(contracts, c => c.FullName == typeof(IClientGrants).FullName);

        // Real OS contracts (github, notifications) are discoverable and filterable by alias.
        var scoped = NeuronContractDiscovery.Discover(
            [typeof(IRepository).Assembly, typeof(INotificationsInbox).Assembly],
            filterAliases: ["github", "notifications"]);
        Assert.Contains(scoped, c => c.FullName == typeof(IRepository).FullName);
        Assert.Contains(scoped, c => c.FullName == typeof(INotifications).FullName || c.FullName == typeof(INotificationsInbox).FullName);
        Assert.DoesNotContain(scoped, c => c.FullName == typeof(IFolder).FullName);

        var prompt = NeuronContractDiscovery.FormatPromptCatalog([demo]);
        Assert.Contains("os.Get<" + typeof(IDemoWidgetContract).FullName + ">(key)", prompt, StringComparison.Ordinal);
        Assert.Contains("[PersonOnly]", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopedTokenRoundTripsAllowedContractsAndRejectsTampering()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 25, 18, 0, 0, TimeSpan.Zero));
        var tokens = CreateTokens(clock);

        var scopedToken = tokens.IssueScoped(new BrainId("brain-1"), "orch-abc123", ["Notifications", "github", "github"]);
        Assert.True(tokens.TryVerify(scopedToken, out var verified));
        Assert.NotNull(verified);
        Assert.True(verified.IsScoped);
        Assert.Equal("brain-1", verified.BrainId);
        Assert.Equal("orch-abc123", verified.AppKey);
        Assert.NotNull(verified.AllowedContracts);
        Assert.Equal(["github", "notifications"], verified.AllowedContracts);

        // Tampering with the payload to widen AllowedContracts breaks the HMAC signature.
        var parts = scopedToken.Split('.');
        Assert.Equal(2, parts.Length);
        var forgedPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            BrainId = "brain-1",
            AppKey = "orch-abc123",
            ExpiresUnix = clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds(),
            AllowedContracts = new[] { "github", "notifications", "folder", "ai" },
        });
        var forgedSegment = Convert.ToBase64String(forgedPayload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var tamperedToken = $"{forgedSegment}.{parts[1]}";
        Assert.False(tokens.TryVerify(tamperedToken, out _));

        // Expiration invalidates the scoped token.
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(tokens.TryVerify(scopedToken, out _));
    }

    [Fact]
    public void UnscopedLegacyTokenRemainsUnscopedAndAuthorityScopeMintingIsRefused()
    {
        var tokens = CreateTokens();

        var legacy = tokens.Issue("brain-1", "app-legacy");
        Assert.True(tokens.TryVerify(legacy, out var verified));
        Assert.NotNull(verified);
        Assert.False(verified.IsScoped);
        Assert.Null(verified.AllowedContracts);

        Assert.Throws<UnauthorizedAccessException>(() =>
            tokens.IssueScoped(new BrainId("brain-1"), "orch-bad", ["notifications", typeof(IConfirmationRequests).FullName!]));
        Assert.Throws<UnauthorizedAccessException>(() =>
            tokens.IssueScoped(new BrainId("brain-1"), "orch-bad", [typeof(IClientGrants).FullName!]));
    }

    [Fact]
    public async Task SandboxEdgeEndpointsRejectsOutOfScopeContractsWith403AndAllowsGrantedScope()
    {
        var tokens = CreateTokens();
        var token = tokens.IssueScoped(new BrainId("brain-scope"), "orch-run-1", ["github", "notifications"]);
        Assert.True(tokens.TryVerify(token, out var identity));
        Assert.NotNull(identity);

        // In-scope contracts pass scope enforcement.
        SandboxEdgeEndpoints.EnforceTokenScope(identity, typeof(INotifications).FullName!);
        SandboxEdgeEndpoints.EnforceTokenScope(identity, typeof(INotificationsInbox).FullName!);
        SandboxEdgeEndpoints.EnforceTokenScope(identity, typeof(IRepository).FullName!);
        SandboxEdgeEndpoints.EnforceTokenScope(identity, "notifications");
        SandboxEdgeEndpoints.EnforceTokenScope(identity, "github");

        // Out-of-scope contracts are rejected with 403 Forbidden by InvokeEdgeAsync.
        var services = new ServiceCollection().BuildServiceProvider();
        var ai = new SdkAiAdapter(services);
        var tables = new SdkTablesAdapter(services);

        string[] disallowedContracts =
        [
            typeof(IFolder).FullName!,
            typeof(IAi).FullName!,
            typeof(ITableHandle).FullName!,
            typeof(IGmail).FullName!,
            typeof(IConfirmationRequests).FullName!,
            typeof(IClientGrants).FullName!,
            "gmail",
            "folder",
        ];

        foreach (var disallowed in disallowedContracts)
        {
            var outcome = await SandboxEdgeEndpoints.InvokeEdgeAsync(
                identity,
                new SandboxEdgeEndpoints.EdgeInvokeRequest(disallowed, "ListAsync", null, "[]"),
                ai,
                tables,
                services,
                TestContext.Current.CancellationToken);

            Assert.Equal(StatusCodes.Status403Forbidden, outcome.StatusCode);
            Assert.NotNull(outcome.Response.Error);
        }

        // Empty-scope token denies every contract invocation.
        var emptyToken = tokens.IssueScoped(new BrainId("brain-scope"), "orch-run-empty", []);
        Assert.True(tokens.TryVerify(emptyToken, out var emptyIdentity));
        var emptyOutcome = await SandboxEdgeEndpoints.InvokeEdgeAsync(
            emptyIdentity!,
            new SandboxEdgeEndpoints.EdgeInvokeRequest(typeof(INotifications).FullName!, nameof(INotifications.ListAsync), null, "[]"),
            ai,
            tables,
            services,
            TestContext.Current.CancellationToken);
        Assert.Equal(StatusCodes.Status403Forbidden, emptyOutcome.StatusCode);
    }
}
