using IntoChat.Cloud.Auth.Core;
using IntoChat.Cloud.Auth.Licensing;
using IntoChat.OS.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IntoChat.Cloud.Auth.Tests;

// The licence gate result and the start-up guard (AUTH-08, R6), each guard fact on its own host. The host's own gate (the kernel
// ILicenseCheck adapter over AuthLicenseGate) lands with the cloud composition in 09-09.
[Trait("host", "own")]
public sealed class StartupGuardFacts
{
    private static AuthLicenseGate Gate() => new(TestLicense.Verifier());

    [Fact]
    public void Gate_ValidToken_IsOkWithHolderAndExpiry()
    {
        var result = Gate().Check(TestLicense.Valid());

        Assert.True(result.Ok);
        Assert.Equal("Test Org", result.Holder);
        Assert.NotNull(result.ExpiresAt);
        Assert.Null(result.Failure);
    }

    [Fact]
    public void Gate_MissingExpiredInvalid_NameTheFailureNeverTheToken()
    {
        const string garbage = "not-a-real-licence-token-value";
        var gate = Gate();

        Assert.Equal(LicenseFailure.Missing, gate.Check(null).Failure);
        Assert.Equal(LicenseFailure.Missing, gate.Check("  ").Failure);
        Assert.Equal(LicenseFailure.Expired, gate.Check(TestLicense.Expired()).Failure);
        var invalid = gate.Check(garbage);
        Assert.Equal(LicenseFailure.Invalid, invalid.Failure);
        Assert.DoesNotContain(garbage, invalid.ToString());
    }

    [Fact]
    public void Gate_ReadsTheTokenFromTheModuleOptionsSection() =>
        Assert.Equal(AuthModule.OptionsSection + ":License", AuthLicenseGate.TokenKey);

    [Fact(Timeout = 180_000)]
    public async Task Licensed_ValidToken_AuthRoutesMappedAndHealthy()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await AuthHost.Compose(TestBrain.Create(), TestLicense.Valid()).StartAsync(ct);

        Assert.Contains("/auth/license", Routes(brain));
        Assert.Equal("Healthy", await brain.HttpClient.GetStringAsync("/health", ct));
    }

    [Fact(Timeout = 180_000)]
    public async Task Licensed_OpenPosture_StartFailsWithSecuredMessage()
    {
        var error = await StartFailureAsync(AuthHost.Compose(TestBrain.Create(), TestLicense.Valid(), posture: "Open"));

        Assert.Contains("Posture", error);
        Assert.Contains("Secured", error);
    }

    [Fact(Timeout = 180_000)]
    public async Task Licensed_NoPublicBaseUrl_StartFails()
    {
        var error = await StartFailureAsync(AuthHost.Compose(TestBrain.Create(), TestLicense.Valid(), publicBaseUrl: null));

        Assert.Contains("PublicBaseUrl", error);
    }

    internal static string[] Routes(ITestBrain brain) =>
        [.. brain.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Select(endpoint => "/" + endpoint.RoutePattern.RawText?.TrimStart('/'))];

    private static async Task<string> StartFailureAsync(TestBrain brain)
    {
        var token = TestLicense.Valid();
        try
        {
            await using var started = await brain.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var text = error.ToString();
            Assert.DoesNotContain(token, text);
            return text;
        }

        Assert.Fail("The host started.");
        return "";
    }
}
