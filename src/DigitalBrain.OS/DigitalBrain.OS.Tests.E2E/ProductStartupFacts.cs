using DigitalBrain.Flutter;
namespace DigitalBrain.OS.Tests.E2E;

[Collection("Product host")]
public sealed class ProductStartupFacts(ProductHostFixture host)
{
    [Fact]
    public async Task IntoChatHealthReturnsOk()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var brain = await host.LeaseAsync(ct);
        using var response = await brain.HttpClient.GetAsync("/health", ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
