using DigitalBrain.Framework.Parts;
using DigitalBrain.Platform.Parts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace DigitalBrain.Platform.Tests.Unit.Parts;

public sealed class CustodyOptionsFacts
{
    private static readonly string CustodyKey = $"{PartsOptions.Key}:{nameof(PartsOptions.Custody)}";

    [Fact(Timeout = 120_000)]
    public async Task ProductionRefusesConfiguredCustody()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var brain = await ModuleTest.Create().WithPartLog()
                .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Configured))
                .ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new ProductionEnvironment())))
                .StartAsync(ct);
        });
        Assert.Contains(CustodyKey, Describe(refused), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), Describe(refused), StringComparison.Ordinal);
    }

    [Fact(Timeout = 120_000)]
    public async Task TpmCustodyWithoutAProviderIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var brain = await ModuleTest.Create().WithPartLog()
                .WithConfiguration(CustodyKey, nameof(MasterKeyCustodyKind.Tpm))
                .StartAsync(ct);
        });
        Assert.Contains(CustodyKey, Describe(refused), StringComparison.Ordinal);
    }

    [Fact]
    public void CustodyDefaultsToConfigured() => Assert.Equal(MasterKeyCustodyKind.Configured, new PartsOptions().Custody);

    // The refusal may surface wrapped by the host; what matters is the chain names the key.
    private static string Describe(Exception exception) => exception.ToString();

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "DigitalBrain.Platform.Tests.Unit";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
